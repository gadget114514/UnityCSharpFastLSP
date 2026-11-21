using System.Text;
using System.Text.Json;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// LSP 仕様適合テスト用のハーネス。
/// LspServer を実際の JSON-RPC メッセージ（Content-Length フレーミング込み）で駆動し、
/// サーバーが書き出したフレームを解析して検証できるようにする。
/// </summary>
internal sealed class LspSession : IDisposable
{
    private readonly SyncCaptureStream _output = new();
    private readonly LspServer _server;
    private int _nextId = 1;

    public LspSession()
    {
        _server = new LspServer(_output);
    }

    public void Dispose() => _output.Dispose();

    /// <summary>initialize → initialized を送信する（rootUri なし）。</summary>
    public JsonElement Initialize()
    {
        var response = Request("initialize", new { processId = (int?)null, rootUri = (string?)null, capabilities = new { } });
        Notify("initialized", new { });
        return response;
    }

    /// <summary>textDocument/didOpen を送信する。改行は LF に正規化する。</summary>
    public void OpenDocument(string uri, string text, int version = 1)
    {
        Notify("textDocument/didOpen", new
        {
            textDocument = new { uri, languageId = "csharp", version, text = Normalize(text) }
        });
    }

    /// <summary>リクエストを送信し、対応するレスポンス全体（jsonrpc/id/result/error）を返す。</summary>
    public JsonElement RequestRaw(string method, object parameters)
    {
        int id = _nextId++;
        Send(new { jsonrpc = "2.0", id, method, @params = parameters });

        var response = FindResponse(id);
        Assert.True(response.HasValue, $"No response received for request '{method}' (id={id}).");
        return response.Value;
    }

    /// <summary>リクエストを送信し、成功レスポンスの result を返す。error があれば失敗にする。</summary>
    public JsonElement Request(string method, object parameters)
    {
        var root = RequestRaw(method, parameters);
        Assert.False(root.TryGetProperty("error", out _),
            $"Request '{method}' returned an error: {(root.TryGetProperty("error", out var e) ? e.GetRawText() : "")}");
        Assert.True(root.TryGetProperty("result", out var result), $"Response for '{method}' has no 'result' member.");
        return result;
    }

    /// <summary>通知（id なし）を送信する。</summary>
    public void Notify(string method, object parameters)
    {
        Send(new { jsonrpc = "2.0", method, @params = parameters });
    }

    /// <summary>通知を送信し、HandleMessage の戻り値（false = 終了）を返す。</summary>
    public bool NotifyWithResult(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
        return _server.HandleMessage(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>生の JSON 文字列をそのまま送信する（不正なメッセージのテスト用）。</summary>
    public void SendRawJson(string json)
    {
        _server.HandleMessage(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>指定メソッドの通知（id なし）が uri 付きで届くまで待つ。届かなければ null。</summary>
    public JsonElement? WaitForNotification(string method, string uri, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var msg in AllMessages())
            {
                if (msg.TryGetProperty("method", out var m) && m.GetString() == method &&
                    msg.TryGetProperty("params", out var p) &&
                    p.TryGetProperty("uri", out var u) && u.GetString() == uri)
                {
                    return msg;
                }
            }
            if (DateTime.UtcNow > deadline) return null;
            Thread.Sleep(10);
        }
    }

    /// <summary>これまでにサーバーが書き出した全フレームのバイト数。</summary>
    public int CapturedByteCount => _output.Snapshot().Length;

    /// <summary>これまでにサーバーが書き出した全フレーム（ボディの JSON）を取得する。</summary>
    public List<JsonElement> AllMessages()
    {
        var docs = new List<JsonElement>();
        byte[] data = _output.Snapshot();
        int pos = 0;
        var separator = "\r\n\r\n"u8;

        while (pos < data.Length)
        {
            int headerLen = data.AsSpan(pos).IndexOf(separator);
            if (headerLen < 0) break;

            string header = Encoding.ASCII.GetString(data, pos, headerLen);
            int contentLength = ParseContentLength(header);

            int bodyStart = pos + headerLen + separator.Length;
            Assert.True(bodyStart + contentLength <= data.Length, "Truncated LSP frame in server output.");

            var doc = JsonDocument.Parse(data.AsMemory(bodyStart, contentLength));
            docs.Add(doc.RootElement.Clone());
            pos = bodyStart + contentLength;
        }

        return docs;
    }

    private void Send<T>(T message)
    {
        string json = JsonSerializer.Serialize(message);
        _server.HandleMessage(Encoding.UTF8.GetBytes(json));
    }

    private JsonElement? FindResponse(int id)
    {
        foreach (var msg in AllMessages())
        {
            if (msg.TryGetProperty("id", out var idElem) &&
                idElem.ValueKind == JsonValueKind.Number &&
                idElem.GetInt32() == id)
            {
                return msg;
            }
        }
        return null;
    }

    private static int ParseContentLength(string header)
    {
        foreach (var line in header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line["Content-Length:".Length..].Trim());
            }
        }
        throw new InvalidOperationException($"Missing Content-Length header: '{header}'");
    }

    internal static string Normalize(string text) => text.Replace("\r\n", "\n");
}

/// <summary>
/// サーバーの出力先。診断の非同期発行（Task.Run）と読み取りが競合しないよう、
/// 書き込みと読み取りを同じロックで保護する。
/// </summary>
internal sealed class SyncCaptureStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly object _gate = new();

    public byte[] Snapshot()
    {
        lock (_gate) return _buffer.ToArray();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate) _buffer.Write(buffer, offset, count);
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length { get { lock (_gate) return _buffer.Length; } }
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>テストで使うテキスト位置計算ヘルパー。</summary>
internal static class TextPos
{
    /// <summary>text 中で needle が最初に現れる位置（0-based の line/character）を返す。</summary>
    public static (int Line, int Character) Of(string text, string needle, int occurrence = 0, int withinOffset = 0)
    {
        string normalized = text.Replace("\r\n", "\n");
        int idx = -1;
        for (int i = 0; i <= occurrence; i++)
        {
            idx = normalized.IndexOf(needle, idx + 1, StringComparison.Ordinal);
            Assert.True(idx >= 0, $"Needle '{needle}' (occurrence {occurrence}) not found in test source.");
        }
        return OffsetToPosition(normalized, idx + withinOffset);
    }

    public static (int Line, int Character) OffsetToPosition(string normalized, int offset)
    {
        int line = 0;
        int lineStart = 0;
        for (int i = 0; i < offset; i++)
        {
            if (normalized[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }
        return (line, offset - lineStart);
    }

    public static object Pos(int line, int character) => new { line, character };

    public static object At(string text, string needle, int occurrence = 0, int withinOffset = 0)
    {
        var (line, character) = Of(text, needle, occurrence, withinOffset);
        return new { line, character };
    }
}

/// <summary>仕様に沿った TextEdit[] の適用（テスト用）。</summary>
internal static class TextEditApplier
{
    public static string Apply(string text, IEnumerable<JsonElement> edits)
    {
        string result = text.Replace("\r\n", "\n");
        // 後ろの編集から適用しないとオフセットがずれるので、開始位置の降順に並べる
        var ordered = edits
            .Select(e => (
                StartLine: e.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                StartChar: e.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(),
                EndLine: e.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
                EndChar: e.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32(),
                NewText: e.GetProperty("newText").GetString() ?? ""))
            .OrderByDescending(e => e.StartLine)
            .ThenByDescending(e => e.StartChar)
            .ToList();

        foreach (var e in ordered)
        {
            int start = LineCharToOffset(result, e.StartLine, e.StartChar);
            int end = LineCharToOffset(result, e.EndLine, e.EndChar);
            result = result[..start] + e.NewText + result[end..];
        }
        return result;
    }

    /// <summary>
    /// 位置をオフセットに変換する。文書の外を指す位置は、クライアント（VS Code 等）と同様に
    /// 文書末尾・行末へクランプする。範囲外の位置そのものの検証は別テストで行う。
    /// </summary>
    private static int LineCharToOffset(string text, int line, int character)
    {
        int offset = 0;
        for (int l = 0; l < line; l++)
        {
            int nl = text.IndexOf('\n', offset);
            if (nl < 0) return text.Length;
            offset = nl + 1;
        }

        int lineEnd = text.IndexOf('\n', offset);
        if (lineEnd < 0) lineEnd = text.Length;
        return Math.Min(offset + character, lineEnd);
    }
}
