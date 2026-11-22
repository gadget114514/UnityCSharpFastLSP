using System.Text;
using System.Text.Json.Nodes;

namespace UnityCSharpFastLSP.Server.Mcp;

/// <summary>
/// エージェントがファイルを直接編集する前提で、LSP サーバーへのドキュメント同期を肩代わりする。
/// uri ごとに version / mtime / size を持ち、未オープンなら didOpen、変化があれば全文 didChange を送る。
/// didClose は <see cref="CloseAll"/>（lsp_reset）でだけ送る。
/// </summary>
public sealed class DocumentSyncTracker
{
    private sealed record Entry(int Version, DateTime LastWriteUtc, long Length);

    private readonly LspLoopbackClient _client;
    private readonly bool _allExtensions;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _docs = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="client">同期通知の送信先。</param>
    /// <param name="allExtensions">true なら全拡張子を同期（プロキシ）。false なら .cs のみ（スタンドアロン）。</param>
    public DocumentSyncTracker(LspLoopbackClient client, bool allExtensions)
    {
        _client = client;
        _allExtensions = allExtensions;
    }

    /// <summary>
    /// uri のファイルをサーバーの状態と揃える。ファイルが無い・対象外の拡張子・file: 以外の uri は何もしない。
    /// 同じ uri への並行呼び出しで didOpen が二重に出ないよう、全体をロックする。
    /// </summary>
    public void Sync(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) || !parsed.IsFile) return;

        string path = parsed.LocalPath;
        if (!_allExtensions && !string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase)) return;

        lock (_gate)
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;

            DateTime lastWriteUtc = info.LastWriteTimeUtc;
            long length = info.Length;

            if (_docs.TryGetValue(uri, out Entry? entry))
            {
                if (entry.LastWriteUtc == lastWriteUtc && entry.Length == length) return;

                string? text = ReadText(path);
                if (text == null) return;

                int version = entry.Version + 1;
                _docs[uri] = new Entry(version, lastWriteUtc, length);
                _client.Notify("textDocument/didChange", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
                    ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = text }),
                });
                return;
            }

            string? openText = ReadText(path);
            if (openText == null) return;

            _docs[uri] = new Entry(1, lastWriteUtc, length);
            _client.Notify("textDocument/didOpen", new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["uri"] = uri,
                    ["languageId"] = string.Equals(Path.GetExtension(path), ".cs", StringComparison.OrdinalIgnoreCase) ? "csharp" : "yaml",
                    ["version"] = 1,
                    ["text"] = openText,
                },
            });
        }
    }

    /// <summary>同期中の全ドキュメントに didClose を送り、トラッカーを空にする（lsp_reset 用）。</summary>
    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (string uri in _docs.Keys)
            {
                _client.Notify("textDocument/didClose", new JsonObject
                {
                    ["textDocument"] = new JsonObject { ["uri"] = uri },
                });
            }
            _docs.Clear();
        }
    }

    /// <summary>共有読み取りでファイル全文を読む。読めなければ null（スキップ）。</summary>
    private static string? ReadText(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
