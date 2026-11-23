using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Server.Mcp;

/// <summary>LSP の ResponseError。</summary>
public sealed record LspError(int Code, string Message);

/// <summary>LSP リクエストの結果（result か error のどちらか）。</summary>
public sealed record LspResult(JsonNode? Result, LspError? Error);

/// <summary>
/// プロセス内で LSP サーバー（スタンドアロンの <see cref="LspServer"/> または
/// <see cref="LspProxy"/>）に LSP クライアントとして接続する。
/// サーバーの出力ストリームを <see cref="FrameSinkStream"/> で受け、id ごとにレスポンスを振り分ける。
/// </summary>
public sealed class LspLoopbackClient : IDisposable
{
    private readonly FrameSinkStream _sink;
    private readonly Func<byte[], bool> _send;
    private readonly LspProxy? _proxy;
    private readonly RequestCancellationRegistry _cancellationRegistry = new();
    private readonly object _sendGate = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<LspResult>> _pending = new();
    private readonly ConcurrentDictionary<string, JsonNode> _diagnostics = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;

    private LspLoopbackClient(string? upstreamPath, bool proxy, UpstreamKind upstreamKind = UpstreamKind.Auto, string? workspacePath = null)
    {
        _sink = new FrameSinkStream(OnServerMessage);
        if (proxy)
        {
            _proxy = new LspProxy(_sink, upstreamPath, upstreamKind, workspacePath, cancellationRegistry: _cancellationRegistry);
            _send = _proxy.HandleClientMessage;
        }
        else
        {
            var server = new LspServer(_sink, cancellationRegistry: _cancellationRegistry);
            _send = server.HandleMessage;
        }
    }

    /// <summary>FastLSP 単体（未対応メソッドは -32601）。</summary>
    public static LspLoopbackClient CreateStandalone() => new(null, proxy: false);

    /// <summary>プロキシ構成（FastLSP 非対応メソッドは選択された upstream にフォールバック）。</summary>
    public static LspLoopbackClient CreateProxy(
        string? upstreamPath,
        UpstreamKind upstreamKind = UpstreamKind.Auto,
        string? workspacePath = null) => new(upstreamPath, proxy: true, upstreamKind, workspacePath);

    public bool IsProxy => _proxy != null;

    /// <summary>
    /// このメソッドを今すぐ処理できない理由。Upstream が必要なのに起動していない場合のみ非 null。
    /// </summary>
    public string? UnavailableReason(string method)
    {
        if (_proxy == null || LspProxy.IsFastPath(method)) return null;
        return _proxy.UpstreamError;
    }

    /// <summary>publishDiagnostics のキャッシュ（uri → params）。</summary>
    public IReadOnlyDictionary<string, JsonNode> CachedDiagnostics => _diagnostics;

    public async Task<LspResult> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        long id = Interlocked.Increment(ref _nextId);
        string requestKey = id.ToString(CultureInfo.InvariantCulture);
        _cancellationRegistry.Register(requestKey, ct);
        var tcs = new TaskCompletionSource<LspResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters?.DeepClone(),
        };

        try
        {
            SendNode(message);
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new LspResult(null, new LspError(JsonRpcErrorCode.InternalError,
                $"Request '{method}' timed out after {timeout.TotalSeconds:0} s."));
        }
        finally
        {
            _cancellationRegistry.Unregister(requestKey);
            _pending.TryRemove(id, out _);
        }
    }

    public void Notify(string method, JsonNode? parameters)
    {
        SendNode(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters?.DeepClone(),
        });
    }

    private void SendNode(JsonNode message)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        // LspServer はスレッドセーフではないので送信は直列化する。
        // FastLSP の応答はこの呼び出し中に同期的に sink へ書かれる。
        lock (_sendGate)
        {
            _send(bytes);
        }
    }

    private void OnServerMessage(byte[] body)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[MCP] Invalid LSP message from server: {ex.Message}");
            return;
        }

        if (node is not JsonObject obj) return;

        string? method = obj["method"]?.GetValueKind() == JsonValueKind.String ? obj["method"]!.GetValue<string>() : null;
        JsonNode? idNode = obj["id"];

        if (method != null)
        {
            if (idNode != null)
            {
                // サーバー → クライアントのリクエスト。空応答を返す（送信ゲートの保持者を待たせないよう別スレッドで）。
                JsonNode? result = method == "workspace/configuration"
                    ? new JsonArray((obj["params"]?["items"] as JsonArray)?.Select(_ => (JsonNode?)null).ToArray() ?? [])
                    : null;
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode.DeepClone(), ["result"] = result };
                Task.Run(() => SendNode(response));
                return;
            }

            HandleNotification(method, obj["params"]);
            return;
        }

        if (idNode is null || idNode.GetValueKind() != JsonValueKind.Number) return;
        long id = idNode.GetValue<long>();
        if (!_pending.TryGetValue(id, out var tcs)) return; // shutdown の二重応答など

        if (obj["error"] is JsonObject err)
        {
            int code = err["code"]?.GetValueKind() == JsonValueKind.Number ? err["code"]!.GetValue<int>() : JsonRpcErrorCode.InternalError;
            string message = err["message"]?.ToString() ?? "Unknown error";
            tcs.TrySetResult(new LspResult(null, new LspError(code, message)));
        }
        else
        {
            tcs.TrySetResult(new LspResult(obj["result"]?.DeepClone(), null));
        }
    }

    private void HandleNotification(string method, JsonNode? parameters)
    {
        switch (method)
        {
            case "textDocument/publishDiagnostics":
                if (parameters?["uri"]?.ToString() is string uri)
                {
                    _diagnostics[uri] = parameters.DeepClone();
                }
                break;
            case "window/logMessage":
            case "window/showMessage":
                Console.Error.WriteLine($"[LSP {method}] {parameters?["message"]}");
                break;
        }
    }

    public void Dispose()
    {
        foreach (var tcs in _pending.Values)
        {
            tcs.TrySetResult(new LspResult(null, new LspError(JsonRpcErrorCode.InternalError, "LSP session closed.")));
        }
        _proxy?.Dispose();
    }
}

/// <summary>
/// 書き込まれた LSP フレーム（Content-Length ヘッダー + ボディ）を解析し、ボディごとにコールバックする出力ストリーム。
/// </summary>
internal sealed class FrameSinkStream : Stream
{
    private readonly Action<byte[]> _onMessage;
    private readonly object _gate = new();
    private byte[] _buffer = new byte[64 * 1024];
    private int _length;

    public FrameSinkStream(Action<byte[]> onMessage) => _onMessage = onMessage;

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            if (_length + data.Length > _buffer.Length)
            {
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, _length + data.Length));
            }
            data.CopyTo(_buffer.AsSpan(_length));
            _length += data.Length;

            int consumed = 0;
            while (TryParseFrame(_buffer.AsSpan(consumed, _length - consumed), out int frameLength, out byte[]? body))
            {
                consumed += frameLength;
                try
                {
                    _onMessage(body!);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[MCP] Error handling LSP message: {ex.Message}");
                }
            }

            if (consumed > 0)
            {
                _buffer.AsSpan(consumed, _length - consumed).CopyTo(_buffer);
                _length -= consumed;
            }
        }
    }

    private static bool TryParseFrame(ReadOnlySpan<byte> data, out int frameLength, out byte[]? body)
    {
        frameLength = 0;
        body = null;
        int headerEnd = data.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0) return false;

        int contentLength = -1;
        foreach (string line in Encoding.ASCII.GetString(data[..headerEnd]).Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out int parsed))
            {
                contentLength = parsed;
            }
        }

        int bodyStart = headerEnd + 4;
        if (contentLength < 0 || data.Length - bodyStart < contentLength) return false;

        body = data.Slice(bodyStart, contentLength).ToArray();
        frameLength = bodyStart + contentLength;
        return true;
    }

    public override void Flush() { }
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
