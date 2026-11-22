using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UnityCSharpFastLSP.Protocol;

public static class JsonRpcErrorCode
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
}

public sealed class LspMessageWriter
{
    private readonly Stream _output;
    private readonly object _lock = new();

    public LspMessageWriter(Stream output)
    {
        _output = output;
    }

    public void WriteResponse<T>(object? id, T? result, JsonTypeInfo<T> typeInfo)
    {
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id,
            result
        });

        WriteFramedMessage(payloadBytes);
    }

    public void WriteRawResponse(object? id, byte[] rawResultBytes)
    {
        string rawJson = Encoding.UTF8.GetString(rawResultBytes);
        string msg = $"{{\"jsonrpc\":\"2.0\",\"id\":{(id is null ? "null" : JsonSerializer.Serialize(id))},\"result\":{rawJson}}}";
        WriteFramedMessage(Encoding.UTF8.GetBytes(msg));
    }

    public void WriteError(object? id, int code, string message)
    {
        string idJson = id is null ? "null" : JsonSerializer.Serialize(id);
        string msgJson = JsonSerializer.Serialize(message);
        string msg = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"error\":{{\"code\":{code},\"message\":{msgJson}}}}}";
        WriteFramedMessage(Encoding.UTF8.GetBytes(msg));
    }

    public void WriteNotification<T>(string method, T @params, JsonTypeInfo<T> typeInfo)
    {
        byte[] payloadBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            method,
            @params
        });

        WriteFramedMessage(payloadBytes);
    }

    public void WriteFramedMessage(byte[] body)
    {
        byte[] frame = Frame(body);

        // ヘッダーとボディを 1 回の Write で書く。プロキシでは Upstream 転送スレッドも同じ
        // 出力ストリームに書くため、フレーム単位で不可分にしないと混線する。
        lock (_lock)
        {
            _output.Write(frame, 0, frame.Length);
            _output.Flush();
        }
    }

    /// <summary>LSP の Content-Length ヘッダーを付けたフレームを 1 つのバッファで返す。</summary>
    public static byte[] Frame(ReadOnlySpan<byte> body)
    {
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        byte[] frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame.AsSpan(header.Length));
        return frame;
    }
}
