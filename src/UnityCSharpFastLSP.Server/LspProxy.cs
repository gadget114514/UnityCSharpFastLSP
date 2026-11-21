using System.Diagnostics;
using System.Text.Json;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Server;

public sealed class LspProxy : IDisposable
{
    private readonly LspServer _fastServer;
    private readonly Stream _clientOutput;
    private readonly Process? _upstreamProcess;
    private readonly StreamWriter? _upstreamInput;
    private readonly CancellationTokenSource _cts = new();

    private static readonly HashSet<string> FastMethods = new(StringComparer.Ordinal)
    {
        "textDocument/completion",
        "textDocument/hover",
        "textDocument/formatting",
        "textDocument/definition",
        "textDocument/declaration",
        "textDocument/implementation",
        "textDocument/typeDefinition",
        "textDocument/references",
        "textDocument/signatureHelp",
        "textDocument/semanticTokens/full",
        "textDocument/documentHighlight",
        "textDocument/documentSymbol",
        "workspace/symbol"
    };

    public LspProxy(Stream clientOutput, string? upstreamExecutablePath = null)
    {
        _clientOutput = clientOutput;
        _fastServer = new LspServer(clientOutput);

        string? resolvedPath = ResolveUpstreamPath(upstreamExecutablePath);
        if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    Arguments = "--stdio",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                _upstreamProcess = Process.Start(psi);
                if (_upstreamProcess != null)
                {
                    _upstreamInput = _upstreamProcess.StandardInput;
                    Console.Error.WriteLine($"LspProxy: Connected to upstream Roslyn server: {resolvedPath}");

                    // Upstream の出力を非同期に読み取りクライアントに転送
                    Task.Run(() => ReadUpstreamLoop(_upstreamProcess.StandardOutput.BaseStream, _cts.Token));
                    Task.Run(() => ReadUpstreamStderrLoop(_upstreamProcess.StandardError, _cts.Token));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LspProxy: Failed to start upstream server '{resolvedPath}': {ex.Message}");
            }
        }
        else
        {
            Console.Error.WriteLine("LspProxy: No upstream server found. Running in standalone ultra-fast mode.");
        }
    }

    public bool HandleClientMessage(byte[] message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        JsonElement root = doc.RootElement;
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";

        // 1. 同期系通知 (didOpen, didChange, didClose)
        if (method is "textDocument/didOpen" or "textDocument/didChange" or "textDocument/didClose")
        {
            // FastLSP が即時構文パースと診断発行
            _fastServer.HandleMessage(message);

            // Upstream にも転送して Roslyn の状態を最新に保つ
            ForwardToUpstream(message);
            return true;
        }

        // 2. FastLSP が得意とする超高速リクエスト (0.15ms)
        if (FastMethods.Contains(method))
        {
            return _fastServer.HandleMessage(message);
        }

        // 3. initialize / initialized / shutdown / exit
        if (method == "initialize")
        {
            // FastLSP 側で初期化
            _fastServer.HandleMessage(message);

            // Upstream にも initialize を転送
            ForwardToUpstream(message);
            return true;
        }

        if (method is "initialized" or "shutdown" or "exit")
        {
            bool res = _fastServer.HandleMessage(message);
            ForwardToUpstream(message);
            return res;
        }

        // 4. FastLSP 単体ではできない重い機能 (rename, codeAction, workspace/symbol など)
        // Upstream (UnityCSharpLSP) にすべて委譲！
        if (_upstreamProcess != null && !_upstreamProcess.HasExited)
        {
            ForwardToUpstream(message);
            return true;
        }

        // Upstream がない場合は FastLSP のデフォルトフォールバック
        return _fastServer.HandleMessage(message);
    }

    private void ForwardToUpstream(byte[] message)
    {
        if (_upstreamInput == null || _upstreamProcess == null || _upstreamProcess.HasExited)
        {
            return;
        }

        try
        {
            byte[] header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {message.Length}\r\n\r\n");
            _upstreamInput.BaseStream.Write(header, 0, header.Length);
            _upstreamInput.BaseStream.Write(message, 0, message.Length);
            _upstreamInput.BaseStream.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LspProxy: Error forwarding to upstream: {ex.Message}");
        }
    }

    private async Task ReadUpstreamLoop(Stream upstreamStream, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!LspMessageReader.TryReadMessage(upstreamStream, out byte[]? message) || message == null)
            {
                break;
            }

            try
            {
                // Upstream からのメッセージ種別を確認
                using var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                // initialize のレスポンスは FastLSP がすでに返答しているためスキップ
                if (root.TryGetProperty("result", out var resElem) &&
                    resElem.TryGetProperty("capabilities", out _))
                {
                    continue;
                }

                // Upstream の診断やレスポンス (rename, codeAction 等) をクライアントに透過転送
                byte[] header = System.Text.Encoding.ASCII.GetBytes($"Content-Length: {message.Length}\r\n\r\n");
                lock (_clientOutput)
                {
                    _clientOutput.Write(header, 0, header.Length);
                    _clientOutput.Write(message, 0, message.Length);
                    _clientOutput.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LspProxy: Error forwarding from upstream: {ex.Message}");
            }
        }
    }

    private async Task ReadUpstreamStderrLoop(StreamReader stderr, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await stderr.ReadLineAsync(ct);
            if (line == null) break;
            Console.Error.WriteLine($"[Upstream Roslyn] {line}");
        }
    }

    private static string? ResolveUpstreamPath(string? explicitPath)
    {
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
        {
            return explicitPath;
        }

        string env = Environment.GetEnvironmentVariable("UNITY_CSHARP_LSP_PATH") ?? "";
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        // デフォルトの探索パス
        string[] candidates =
        [
            @"D:\wsgit\UnityCSharpLSP\artifacts\publish\UnityCSharpLSP.Server.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\UnityCSharpLSP\UnityCSharpLSP.Server.exe"),
            "UnityCSharpLSP.Server.exe",
            "UnityCSharpLSP.exe"
        ];

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            if (_upstreamProcess != null && !_upstreamProcess.HasExited)
            {
                _upstreamProcess.Kill();
                _upstreamProcess.Dispose();
            }
        }
        catch { }
    }
}
