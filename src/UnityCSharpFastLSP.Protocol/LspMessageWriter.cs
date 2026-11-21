using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace UnityCSharpFastLSP.Protocol;

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
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        lock (_lock)
        {
            _output.Write(header, 0, header.Length);
            _output.Write(body, 0, body.Length);
            _output.Flush();
        }
    }
}
