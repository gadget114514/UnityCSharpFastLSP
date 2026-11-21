using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
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
        if (string.IsNullOrEmpty(resolvedPath) || !File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                "Upstream Roslyn LSP server was not found.\n" +
                "Exploration order:\n" +
                "  1. Explicit argument (--upstream <path>)\n" +
                "  2. Environment variable (UNITY_CSHARP_LSP_PATH)\n" +
                "  3. PATH environment variable auto-discovery (UnityCSharpLSP.Server.exe, UnityCSharpLSP.exe, csharp-ls.exe)\n" +
                "  4. Standard installation directories (%LOCALAPPDATA%\\Programs\\UnityCSharpLSP, etc.)\n" +
                "  5. FAILED: No upstream server found. Please ensure UnityCSharpLSP is on PATH or specify --upstream <path>."
            );
        }

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
            if (_upstreamProcess == null)
            {
                throw new InvalidOperationException($"Failed to launch upstream process: {resolvedPath}");
            }

            _upstreamInput = _upstreamProcess.StandardInput;
            Console.Error.WriteLine($"LspProxy: Connected to upstream Roslyn server: {resolvedPath}");

            // Upstream の出力を非同期に読み取りクライアントに転送
            Task.Run(() => ReadUpstreamLoop(_upstreamProcess.StandardOutput.BaseStream, _cts.Token));
            Task.Run(() => ReadUpstreamStderrLoop(_upstreamProcess.StandardError, _cts.Token));
        }
        catch (Exception ex) when (ex is not FileNotFoundException)
        {
            throw new InvalidOperationException($"LspProxy: Failed to start upstream server '{resolvedPath}': {ex.Message}", ex);
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint SearchPath(
        string? lpPath,
        string lpFileName,
        string? lpExtension,
        int nBufferLength,
        StringBuilder lpBuffer,
        out IntPtr lpFilePart
    );

    private static string? SearchInPathUsingOsApi(string fileName)
    {
        if (OperatingSystem.IsWindows())
        {
            var sb = new StringBuilder(1024);
            uint len = SearchPath(null, fileName, null, sb.Capacity, sb, out _);
            if (len > 0 && len < sb.Capacity)
            {
                return sb.ToString();
            }
        }
        return null;
    }

    private static string? ResolveUpstreamPath(string? explicitPath)
    {
        // 1. コマンドライン引数 (--upstream <path>)
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        // 2. 環境変数 UNITY_CSHARP_LSP_PATH
        string env = Environment.GetEnvironmentVariable("UNITY_CSHARP_LSP_PATH") ?? "";
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        // 3. OS の API (SearchPath) による PATH 自動探索
        string[] targetNames = OperatingSystem.IsWindows()
            ? ["UnityCSharpLSP.Server.exe", "UnityCSharpLSP.exe", "csharp-ls.exe"]
            : ["UnityCSharpLSP.Server", "UnityCSharpLSP", "csharp-ls"];

        foreach (var name in targetNames)
        {
            string? found = SearchInPathUsingOsApi(name);
            if (!string.IsNullOrEmpty(found) && File.Exists(found))
            {
                return Path.GetFullPath(found);
            }
        }

        // 4. 標準インストール先ディレクトリのフォールバック
        string[] standardCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\UnityCSharpLSP\UnityCSharpLSP.Server.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"UnityCSharpLSP\UnityCSharpLSP.Server.exe"),
            @"D:\wsgit\UnityCSharpLSP\artifacts\publish\UnityCSharpLSP.Server.exe",
            @"D:\wsgit\UnityCSharpLSP\src\UnityCSharpLSP.Server\bin\Release\net8.0\UnityCSharpLSP.Server.exe"
        ];

        foreach (var c in standardCandidates)
        {
            if (File.Exists(c)) return Path.GetFullPath(c);
        }

        // 5. 見つからない場合は null (コンストラクタ側でエラー終了)
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
