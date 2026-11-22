using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using UnityCSharpFastLSP.Server.Mcp;

namespace UnityCSharpFastLSP.Tests.Mcp;

/// <summary>
/// Unity 風のワークスペース（一時ディレクトリ）。テストごとに固有のディレクトリを使うので並列実行しても衝突しない。
/// </summary>
internal sealed class TempUnityWorkspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "FastLspMcpTest_" + Guid.NewGuid().ToString("N"));

    private TempUnityWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(Root, "Assets", "Scripts"));
    }

    /// <summary>Player.cs / Enemy.cs を含む最小のサンプル。Enemy.cs は Player.cs を参照する。</summary>
    public static TempUnityWorkspace CreateSample()
    {
        var ws = new TempUnityWorkspace();
        ws.Write("Assets/Scripts/Player.cs", """
            namespace Game.Core
            {
                public class Player
                {
                    public int Health = 100;
                    public void TakeDamage(int amount)
                    {
                        Health -= amount;
                    }
                }
            }
            """);
        ws.Write("Assets/Scripts/Enemy.cs", """
            namespace Game.Core
            {
                public class Enemy
                {
                    public void Attack(Player target)
                    {
                        target.TakeDamage(10);
                    }
                }
            }
            """);
        return ws;
    }

    /// <summary>Root からの相対パスにファイルを書く（既存ならば上書き）。絶対パスを返す。</summary>
    public string Write(string relativePath, string text)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    public string FullPath(string relativePath) => Path.Combine(Root, relativePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // テスト後の掃除に失敗しても結果には影響させない
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// stdout 相当の出力ストリーム。改行区切りの JSON を 1 行ずつ解析し、id ごとに応答を振り分ける。
/// id を持たないメッセージ（parse error 等）は <see cref="NextUnmatchedAsync"/> で読む。
/// </summary>
internal sealed class McpLineSink : Stream
{
    private readonly object _gate = new();
    private readonly List<byte> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _byId = new();
    private readonly Channel<JsonNode> _unmatched = Channel.CreateUnbounded<JsonNode>();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            foreach (byte b in data)
            {
                if (b != (byte)'\n')
                {
                    _pending.Add(b);
                    continue;
                }

                string line = Encoding.UTF8.GetString(_pending.ToArray());
                _pending.Clear();
                if (line.Length == 0) continue;
                Dispatch(JsonNode.Parse(line)!);
            }
        }
    }

    private void Dispatch(JsonNode message)
    {
        JsonNode? id = message["id"];
        if (id == null)
        {
            _unmatched.Writer.TryWrite(message);
            return;
        }

        TaskCompletionSource<JsonNode> tcs = GetOrCreate(id.ToJsonString());
        tcs.TrySetResult(message);
    }

    private TaskCompletionSource<JsonNode> GetOrCreate(string key) =>
        _byId.GetOrAdd(key, _ => new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>id に対応する応答を待つ。タイムアウトなら <see cref="TimeoutException"/>。</summary>
    public Task<JsonNode> WaitForResponseAsync(int id, TimeSpan timeout) =>
        GetOrCreate(id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Task.WaitAsync(timeout);

    /// <summary>id を持たないメッセージを 1 件待つ。</summary>
    public async Task<JsonNode> NextUnmatchedAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        return await _unmatched.Reader.ReadAsync(cts.Token);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>
/// <see cref="McpServer"/> をプロセス内で起動し、改行区切り JSON-RPC で駆動するハーネス。
/// 入力は <see cref="Pipe"/>、出力は <see cref="McpLineSink"/>。
/// </summary>
internal sealed class McpTestSession : IAsyncDisposable
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Pipe _input = new();
    private readonly McpLineSink _output = new();
    private readonly McpServer _server;
    private readonly Task<int> _run;
    private int _nextId = 1;

    public McpTestSession(McpServerOptions options)
    {
        _server = new McpServer(options, _input.Reader.AsStream(), _output);
        _run = _server.RunAsync();
    }

    public static McpTestSession Standalone(string root, TimeSpan? timeout = null) =>
        new(new McpServerOptions(root, Proxy: false, UpstreamPath: null, Settings: null, Timeout: timeout ?? DefaultTimeout));

    public static McpTestSession Proxy(string root, string? upstreamPath, TimeSpan? timeout = null) =>
        new(new McpServerOptions(root, Proxy: true, UpstreamPath: upstreamPath, Settings: null, Timeout: timeout ?? DefaultTimeout));

    /// <summary>JSON-RPC リクエストを送り、応答メッセージ全体（jsonrpc/id/result/error）を返す。</summary>
    public async Task<JsonNode> RequestAsync(string method, JsonNode? parameters = null, TimeSpan? timeout = null)
    {
        int id = _nextId++;
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters != null)
        {
            message["params"] = parameters;
        }

        await SendAsync(message.ToJsonString()).ConfigureAwait(false);
        return await _output.WaitForResponseAsync(id, timeout ?? DefaultTimeout).ConfigureAwait(false);
    }

    /// <summary>tools/call を送り、応答全体を返す。</summary>
    public Task<JsonNode> CallToolAsync(string name, JsonObject? arguments = null, TimeSpan? timeout = null) =>
        RequestAsync("tools/call", new JsonObject
        {
            ["name"] = name,
            ["arguments"] = arguments ?? new JsonObject(),
        }, timeout);

    /// <summary>生の 1 行を送る（不正 JSON のテスト用）。</summary>
    public Task SendRawLineAsync(string line) => SendAsync(line);

    public Task<JsonNode> NextUnmatchedAsync() => _output.NextUnmatchedAsync(DefaultTimeout);

    private async Task SendAsync(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _input.Writer.WriteAsync(bytes).ConfigureAwait(false);
    }

    /// <summary>入力を閉じて MCP サーバーを終了させ、RunAsync の完了を待つ。</summary>
    public async ValueTask DisposeAsync()
    {
        await _input.Writer.CompleteAsync().ConfigureAwait(false);
        try
        {
            await _run.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        finally
        {
            _server.Dispose();
        }
    }

    // ---------- 応答の取り出しヘルパー ----------

    /// <summary>応答が成功（error なし）であることを確認し、result を返す。</summary>
    public static JsonObject ResultOf(JsonNode response)
    {
        if (response["error"] is JsonNode error)
        {
            throw new InvalidOperationException($"JSON-RPC error: {error.ToJsonString()}");
        }
        return (JsonObject)response["result"]!;
    }

    /// <summary>tools/call の content[0].text を JSON として解析する（"null" は null）。</summary>
    public static JsonNode? TextOf(JsonObject toolResult)
    {
        string text = toolResult["content"]![0]!["text"]!.GetValue<string>();
        return JsonNode.Parse(text);
    }
}
