using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using UnityCSharpFastLSP.Protocol;
using Range = UnityCSharpFastLSP.Protocol.Range;

namespace UnityCSharpFastLSP.Workspace;

/// <summary>
/// Unity アセット GUID の索引。
/// 常駐させるのは <c>.meta</c> から得た guid → アセットの対応だけで、
/// 参照（シーン・プレハブ等の YAML に書かれた guid）は問い合わせのたびに並列走査する。
/// </summary>
public sealed class UnityGuidIndex
{
    public const int DefaultMaxResults = 500;
    private const int MaxLineTextLength = 300;
    private const int MetaHeadLines = 16;

    /// <summary>guid を含み得るテキスト（YAML / JSON）アセットの拡張子。</summary>
    private static readonly HashSet<string> ReferenceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".unity", ".prefab", ".asset", ".mat", ".anim", ".controller", ".overrideController",
        ".playable", ".mask", ".physicMaterial", ".physicsMaterial2D", ".spriteatlas", ".spriteatlasv2",
        ".lighting", ".terrainlayer", ".mixer", ".renderTexture", ".preset", ".signal", ".guiskin",
        ".fontsettings", ".shadervariants", ".cubemap", ".flare", ".brush", ".meta",
        ".asmdef", ".asmref",
    };

    private static readonly byte[] ObjectHeaderMarker = "--- !u!"u8.ToArray();
    private static readonly byte[] FileIdMarker = "fileID:"u8.ToArray();

    private readonly record struct GuidEntry(string MetaPath, string AssetPath, int Line, int Character);

    private readonly ConcurrentDictionary<string, GuidEntry> _byGuid = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _guidByAssetPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _rescanGate = new();
    private volatile string? _rootPath;
    private long _lastRescanTicks;
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    private volatile bool _enabled = true;
    private volatile CancellationTokenSource _referenceSearchStop = new();

    /// <summary>進行中の参照検索をすべて止める。</summary>
    public void CancelReferenceSearches()
    {
        try
        {
            _referenceSearchStop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>索引済みの guid 数。</summary>
    public int Count => _byGuid.Count;

    /// <summary>
    /// GUID 機能が有効か。プロキシで upstream が unitylsp/guid* を advertise した場合は無効化され、
    /// 索引を持たず、走査も応答もしない。
    /// </summary>
    public bool Enabled => _enabled;

    /// <summary>有効にする（索引の作成は呼び出し側が <see cref="Index"/> で行う）。</summary>
    public void Enable()
    {
        _referenceSearchStop = new CancellationTokenSource();
        _enabled = true;
    }

    /// <summary>機能を止め、索引を破棄する。</summary>
    public void Disable()
    {
        _enabled = false;
        CancelReferenceSearches();
        _byGuid.Clear();
        _guidByAssetPath.Clear();
    }

    /// <summary>ワークスペース配下の *.meta を走査して guid → アセットを索引する。</summary>
    public int Index(string rootPath)
    {
        if (!_enabled || string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath)) return 0;
        _rootPath = rootPath;

        int count = 0;
        Parallel.ForEach(EnumerateFiles(rootPath, "*.meta"), (metaPath, state) =>
        {
            if (!_enabled)
            {
                state.Stop();
                return;
            }
            if (TryAddMeta(metaPath) != null) Interlocked.Increment(ref count);
        });

        if (!_enabled)
        {
            // 走査中に止められた
            Disable();
            return 0;
        }
        return count;
    }

    /// <summary>32 桁の 16 進文字列か。</summary>
    public static bool IsGuid(string? value)
    {
        if (value == null || value.Length != 32) return false;
        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary>
    /// guid を直接、またはアセット（.meta / スクリプト / 任意アセットの file URI・パス）から guid を求める。
    /// 解決できなければ null。
    /// </summary>
    public string? ResolveGuid(string? guid, string? asset)
    {
        if (!_enabled) return null;

        if (!string.IsNullOrWhiteSpace(guid))
        {
            string trimmed = guid.Trim();
            return IsGuid(trimmed) ? trimmed.ToLowerInvariant() : null;
        }

        if (string.IsNullOrWhiteSpace(asset)) return null;

        string path = ToFullPath(asset.Trim());
        if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
        {
            return TryAddMeta(path);
        }

        if (_guidByAssetPath.TryGetValue(path, out string? known)) return known;
        string meta = path + ".meta";
        return File.Exists(meta) ? TryAddMeta(meta) : null;
    }

    /// <summary>guid の定義（.meta 内の guid 位置とアセット本体）。見つからなければ null。</summary>
    public GuidDefinitionResult? FindDefinition(string guid)
    {
        if (!_enabled) return null;
        if (!TryGetEntry(guid, out GuidEntry entry)) return null;

        var start = new Position(entry.Line, entry.Character);
        var range = new Range(start, start with { Character = entry.Character + guid.Length });
        return new GuidDefinitionResult(
            guid,
            WorkspaceManager.PathToFileUri(entry.AssetPath),
            new Location(WorkspaceManager.PathToFileUri(entry.MetaPath), range));
    }

    /// <summary>
    /// guid を参照しているテキストアセットの位置を走査する（自身の .meta は除く）。
    /// パターンはプロジェクトルート相対パスに対する <see cref="GlobPatternMatcher"/> の glob。
    /// <paramref name="includePatterns"/> を指定すると既定の拡張子フィルタの代わりになり（*.cs なども対象にできる）、
    /// <paramref name="excludePatterns"/> に一致するファイルは常に除く。maxResults が 0 以下なら既定値。
    /// </summary>
    public GuidReferencesResult FindReferences(
        string guid,
        int? maxResults = null,
        IReadOnlyList<string>? includePatterns = null,
        IReadOnlyList<string>? excludePatterns = null,
        CancellationToken ct = default)
    {
        using var searchStop = CancellationTokenSource.CreateLinkedTokenSource(ct, _referenceSearchStop.Token);
        CancellationToken token = searchStop.Token;

        int limit = Math.Clamp(maxResults is > 0 ? maxResults.Value : DefaultMaxResults, 1, 100_000);
        bool hasInclude = includePatterns is { Count: > 0 };
        bool hasExclude = excludePatterns is { Count: > 0 };
        string? root = _enabled ? _rootPath : null;
        bool known = TryGetEntry(guid, out GuidEntry entry);
        string? selfMeta = known ? Path.GetFullPath(entry.MetaPath) : null;

        var found = new ConcurrentBag<GuidReference>();
        int total = 0;
        bool truncated = false;

        if (root != null)
        {
            token.ThrowIfCancellationRequested();
            byte[] needle = Encoding.ASCII.GetBytes(guid);
            var options = new ParallelOptions { CancellationToken = token };
            Parallel.ForEach(EnumerateFiles(root, "*"), options, (path, state) =>
            {
                token.ThrowIfCancellationRequested();

                if (hasInclude || hasExclude)
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (hasExclude && GlobPatternMatcher.MatchesAny(relative, excludePatterns)) return;
                    if (hasInclude
                            ? !GlobPatternMatcher.MatchesAny(relative, includePatterns)
                            : !ReferenceExtensions.Contains(Path.GetExtension(path)))
                    {
                        return;
                    }
                }
                else if (!ReferenceExtensions.Contains(Path.GetExtension(path)))
                {
                    return;
                }

                if (selfMeta != null && string.Equals(Path.GetFullPath(path), selfMeta, StringComparison.OrdinalIgnoreCase)) return;

                foreach (GuidReference reference in ScanFile(path, needle, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (Interlocked.Increment(ref total) > limit)
                    {
                        truncated = true;
                        state.Stop();
                        return;
                    }
                    found.Add(reference);
                }
            });
        }

        var references = found
            .OrderBy(r => r.Uri, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Range.Start.Line)
            .ThenBy(r => r.Range.Start.Character)
            .ToList();

        return new GuidReferencesResult(
            guid,
            known ? WorkspaceManager.PathToFileUri(entry.AssetPath) : null,
            references,
            truncated);
    }

    // ---------- 内部 ----------

    private bool TryGetEntry(string guid, out GuidEntry entry)
    {
        if (_byGuid.TryGetValue(guid, out entry)) return true;

        // 索引後に追加されたアセットかもしれない。1 回だけ再走査する（同時要求は 1 本にまとめる）。
        string? root = _rootPath;
        if (root == null) return false;
        lock (_rescanGate)
        {
            if (_byGuid.TryGetValue(guid, out entry)) return true;

            // 組み込みアセット等、存在しない guid で毎回全走査しないよう間隔を空ける
            long now = Environment.TickCount64;
            if (now - _lastRescanTicks < (long)RescanInterval.TotalMilliseconds) return false;
            _lastRescanTicks = now;
            Index(root);
            return _byGuid.TryGetValue(guid, out entry);
        }
    }

    /// <summary>.meta の先頭から guid を読み、索引に登録する。guid を返す（読めなければ null）。</summary>
    private string? TryAddMeta(string metaPath)
    {
        if (!_enabled) return null;
        try
        {
            using var reader = new StreamReader(metaPath, Encoding.UTF8);
            for (int lineNo = 0; lineNo < MetaHeadLines; lineNo++)
            {
                string? line = reader.ReadLine();
                if (line == null) break;
                if (!line.StartsWith("guid:", StringComparison.Ordinal)) continue;

                string value = line["guid:".Length..].Trim();
                if (!IsGuid(value)) return null;

                string guid = value.ToLowerInvariant();
                string fullMeta = Path.GetFullPath(metaPath);
                string assetPath = fullMeta[..^".meta".Length];
                int character = line.IndexOf(value, "guid:".Length, StringComparison.Ordinal);
                _byGuid[guid] = new GuidEntry(fullMeta, assetPath, lineNo, character);
                _guidByAssetPath[assetPath] = guid;
                return guid;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }

    private static List<GuidReference> ScanFile(string path, byte[] needle, CancellationToken ct)
    {
        var results = new List<GuidReference>();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        ReadOnlySpan<byte> data = bytes;
        if (data.IndexOf(needle) < 0) return results;

        // Binary 直列化のアセット（NUL を含む）は対象外。guid も 16 進文字列では入っていない。
        if (data[..Math.Min(data.Length, 512)].IndexOf((byte)0) >= 0) return results;

        string uri = WorkspaceManager.PathToFileUri(path);
        int searchFrom = 0;
        int countedUpTo = 0;
        int lineNo = 0;

        while (searchFrom < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            int rel = data[searchFrom..].IndexOf(needle);
            if (rel < 0) break;
            int hit = searchFrom + rel;
            searchFrom = hit + needle.Length;

            lineNo += data[countedUpTo..hit].Count((byte)'\n');
            countedUpTo = hit;

            int lineStart = data[..hit].LastIndexOf((byte)'\n') + 1;
            int lineEndRel = data[hit..].IndexOf((byte)'\n');
            int lineEnd = lineEndRel < 0 ? data.Length : hit + lineEndRel;
            if (lineEnd > lineStart && data[lineEnd - 1] == (byte)'\r') lineEnd--;

            int character = Encoding.UTF8.GetCharCount(data[lineStart..hit]);
            var start = new Position(lineNo, character);
            var range = new Range(start, start with { Character = character + needle.Length });

            string lineText = Encoding.UTF8.GetString(data[lineStart..lineEnd]).Trim();
            if (lineText.Length > MaxLineTextLength) lineText = lineText[..MaxLineTextLength] + "…";

            (int? classId, long? objectFileId) = FindObjectHeader(data[..lineStart]);
            long? fileId = FindFileId(data[lineStart..hit]);

            results.Add(new GuidReference(uri, range, lineText, classId, objectFileId, fileId));
        }

        return results;
    }

    /// <summary>直前の YAML ドキュメントヘッダ <c>--- !u!&lt;classId&gt; &amp;&lt;fileID&gt;</c> を読む。</summary>
    private static (int? ClassId, long? FileId) FindObjectHeader(ReadOnlySpan<byte> before)
    {
        int idx = before.LastIndexOf(ObjectHeaderMarker);
        if (idx < 0) return (null, null);

        ReadOnlySpan<byte> rest = before[(idx + ObjectHeaderMarker.Length)..];
        int newline = rest.IndexOf((byte)'\n');
        if (newline >= 0) rest = rest[..newline];

        int? classId = ParseInt(rest, out int consumed) is long c ? (int)c : (int?)null;
        long? fileId = null;
        int amp = rest[consumed..].IndexOf((byte)'&');
        if (amp >= 0)
        {
            fileId = ParseInt(rest[(consumed + amp + 1)..], out _);
        }
        return (classId, fileId);
    }

    /// <summary>同じ行の guid より前にある <c>fileID: &lt;n&gt;</c> を読む。</summary>
    private static long? FindFileId(ReadOnlySpan<byte> linePrefix)
    {
        int idx = linePrefix.LastIndexOf(FileIdMarker);
        if (idx < 0) return null;
        ReadOnlySpan<byte> rest = linePrefix[(idx + FileIdMarker.Length)..].TrimStart((byte)' ');
        return ParseInt(rest, out _);
    }

    private static long? ParseInt(ReadOnlySpan<byte> span, out int consumed)
    {
        consumed = 0;
        bool negative = false;
        if (span.Length > 0 && span[0] == (byte)'-')
        {
            negative = true;
            consumed = 1;
        }

        long value = 0;
        int digits = 0;
        while (consumed < span.Length && span[consumed] is >= (byte)'0' and <= (byte)'9' && digits < 19)
        {
            value = value * 10 + (span[consumed] - '0');
            consumed++;
            digits++;
        }
        return digits == 0 ? null : (negative ? -value : value);
    }

    private static string ToFullPath(string asset)
    {
        if (asset.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(asset, UriKind.Absolute, out Uri? uri) && uri.IsFile)
        {
            return Path.GetFullPath(uri.LocalPath);
        }
        return Path.GetFullPath(asset);
    }

    /// <summary>Assets / Packages / Library/PackageCache を走査する。いずれも無ければルート全体。</summary>
    private static IEnumerable<string> EnumerateFiles(string rootPath, string pattern)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        string[] dirs =
        [
            Path.Combine(rootPath, "Assets"),
            Path.Combine(rootPath, "Packages"),
            Path.Combine(rootPath, "Library", "PackageCache"),
        ];

        bool any = false;
        foreach (string dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            any = true;
            foreach (string file in Directory.EnumerateFiles(dir, pattern, options))
            {
                yield return file;
            }
        }

        if (!any)
        {
            foreach (string file in Directory.EnumerateFiles(rootPath, pattern, options))
            {
                yield return file;
            }
        }
    }
}
