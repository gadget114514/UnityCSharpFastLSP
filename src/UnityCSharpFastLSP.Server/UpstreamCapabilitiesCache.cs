using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnityCSharpFastLSP.Server;

/// <summary>
/// upstream LSP サーバーが initialize で返した capabilities を upstream kind ごとにディスクへ保存する。
/// プロキシは initialize に即答するため upstream の応答を待てない。そこで前回保存した capabilities を
/// FastLSP の capabilities にマージして advertise し、今回の upstream 応答で次回用のキャッシュを更新する。
/// </summary>
public sealed class UpstreamCapabilitiesCache
{
    /// <summary>キャッシュディレクトリを上書きする環境変数。</summary>
    public const string CacheDirEnv = "UNITY_CSHARP_FAST_LSP_CACHE_DIR";

    /// <summary>
    /// upstream 側の値を採用しない capability。FastLSP が同期方式・位置エンコーディング・
    /// セマンティックトークン凡例を決めているため、upstream の値で上書きすると不整合になる。
    /// </summary>
    private static readonly HashSet<string> FastOwnedKeys = new(StringComparer.Ordinal)
    {
        "textDocumentSync",
        "positionEncoding",
        "semanticTokensProvider",
    };

    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    public string Directory { get; }

    public UpstreamCapabilitiesCache(string directory)
    {
        Directory = directory;
    }

    /// <summary>環境変数 → %LOCALAPPDATA%/UnityCSharpFastLSP/upstream-capabilities の順で決まる既定のキャッシュ。</summary>
    public static UpstreamCapabilitiesCache CreateDefault() => new(DefaultDirectory());

    public static string DefaultDirectory()
    {
        string? overridden = Environment.GetEnvironmentVariable(CacheDirEnv);
        if (!string.IsNullOrWhiteSpace(overridden)) return Path.GetFullPath(overridden);

        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = Path.GetTempPath();
        return Path.Combine(baseDir, "UnityCSharpFastLSP", "upstream-capabilities");
    }

    public string GetPath(UpstreamKind kind) =>
        Path.Combine(Directory, UpstreamServerRegistry.CanonicalName(kind) + ".json");

    /// <summary>保存済みの capabilities を読む。無い・壊れている場合は null。</summary>
    public JsonObject? Load(UpstreamKind kind)
    {
        string path = GetPath(kind);
        try
        {
            if (!File.Exists(path)) return null;
            JsonNode? root = JsonNode.Parse(File.ReadAllText(path));
            return root?["capabilities"] as JsonObject;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"[LspProxy] Ignoring unreadable capabilities cache '{path}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// capabilities を保存する。内容が前回と同じなら書き込まず false を返す。
    /// 一時ファイルに書いてから置き換えるので、途中で落ちても壊れたファイルは残らない。
    /// </summary>
    public bool Save(UpstreamKind kind, JsonObject capabilities, JsonNode? serverInfo = null)
    {
        JsonObject? previous = Load(kind);
        if (previous != null && JsonNode.DeepEquals(previous, capabilities)) return false;

        string path = GetPath(kind);
        var root = new JsonObject
        {
            ["kind"] = UpstreamServerRegistry.CanonicalName(kind),
            ["savedAt"] = DateTimeOffset.UtcNow.ToString("O"),
            ["serverInfo"] = serverInfo?.DeepClone(),
            ["capabilities"] = capabilities.DeepClone(),
        };

        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            string tmp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(IndentedOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LspProxy] Could not write capabilities cache '{path}': {ex.Message}");
            return false;
        }
    }

    /// <summary>指定 kind（null なら全 kind）のキャッシュを削除し、削除したファイルのパスを返す。</summary>
    public List<string> Clear(UpstreamKind? kind = null)
    {
        var deleted = new List<string>();
        IEnumerable<UpstreamKind> kinds = kind is UpstreamKind k ? [k] : Enum.GetValues<UpstreamKind>();
        foreach (UpstreamKind each in kinds)
        {
            string path = GetPath(each);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            deleted.Add(path);
        }
        return deleted;
    }

    /// <summary>
    /// FastLSP の capabilities に upstream の capabilities を足す。FastLSP が既に持つキーと
    /// <see cref="FastOwnedKeys"/> は FastLSP を優先する。<paramref name="fast"/> を直接書き換える。
    /// </summary>
    public static void MergeInto(JsonObject fast, JsonObject upstream)
    {
        // experimental.unitylsp.methods はプロキシがどちらかで答えられるので和集合にする
        MergeUnityLspMethods(fast, upstream);

        foreach (KeyValuePair<string, JsonNode?> pair in upstream)
        {
            if (FastOwnedKeys.Contains(pair.Key)) continue;
            if (fast[pair.Key] is not null) continue;
            if (pair.Value is null) continue;
            fast[pair.Key] = pair.Value.DeepClone();
        }
    }

    /// <summary>capabilities.experimental.unitylsp.methods を読む（無ければ空）。</summary>
    public static HashSet<string> GetUnityLspMethods(JsonObject? capabilities)
    {
        var methods = new HashSet<string>(StringComparer.Ordinal);
        if (capabilities?["experimental"]?["unitylsp"]?["methods"] is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonValue value && value.TryGetValue(out string? name) && !string.IsNullOrEmpty(name))
                {
                    methods.Add(name);
                }
            }
        }
        return methods;
    }

    private static void MergeUnityLspMethods(JsonObject fast, JsonObject upstream)
    {
        HashSet<string> upstreamMethods = GetUnityLspMethods(upstream);
        if (upstreamMethods.Count == 0) return;

        HashSet<string> union = GetUnityLspMethods(fast);
        union.UnionWith(upstreamMethods);

        if (fast["experimental"] is not JsonObject experimental)
        {
            if (fast["experimental"] is not null) return; // 想定外の形は触らない
            experimental = new JsonObject();
            fast["experimental"] = experimental;
        }
        if (experimental["unitylsp"] is not JsonObject unitylsp)
        {
            unitylsp = new JsonObject();
            experimental["unitylsp"] = unitylsp;
        }
        unitylsp["methods"] = new JsonArray(union.OrderBy(m => m, StringComparer.Ordinal)
            .Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
    }
}
