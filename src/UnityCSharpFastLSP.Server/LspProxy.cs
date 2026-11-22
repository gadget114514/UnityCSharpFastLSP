using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Server;

public sealed class LspProxy : IDisposable
{
    private readonly LspServer _fastServer;
    private readonly Stream _clientOutput;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _pendingMessages = new();
    private Process? _upstreamProcess;
    private StreamWriter? _upstreamInput;
    private volatile bool _upstreamReady;

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

    /// <summary>FastLSP が直接応答するメソッドか（false なら Upstream へ委譲される）。</summary>
    public static bool IsFastPath(string method) => FastMethods.Contains(method);

    /// <summary>Upstream の起動に失敗した、または起動後に終了した場合のエラー内容。利用可能なら null。</summary>
    public string? UpstreamError
    {
        get
        {
            if (_upstreamStartError != null) return _upstreamStartError;
            var proc = _upstreamProcess;
            return _upstreamReady && proc != null && proc.HasExited
                ? $"Upstream UnityCSharpLSP exited (code {proc.ExitCode})."
                : null;
        }
    }

    private volatile string? _upstreamStartError;

    public LspProxy(Stream clientOutput, string? upstreamExecutablePath = null)
    {
        _clientOutput = clientOutput;
        _fastServer = new LspServer(clientOutput, isProxy: true);

        // バックグラウンドで Upstream を非同期起動 (メインスレッドおよび initialize を一切待たせない！)
        Task.Run(() => InitUpstreamBackground(upstreamExecutablePath, _cts.Token));
    }

    private void InitUpstreamBackground(string? upstreamExecutablePath, CancellationToken ct)
    {
        try
        {
            _upstreamProcess = StartUpstreamProcess(upstreamExecutablePath);
            _upstreamInput = _upstreamProcess.StandardInput;
            _upstreamReady = true;

            // Upstream の出力を非同期に読み取りクライアントに転送
            Task.Run(() => ReadUpstreamLoop(_upstreamProcess.StandardOutput.BaseStream, ct));
            Task.Run(() => ReadUpstreamStderrLoop(_upstreamProcess.StandardError, ct));

            // 起動前に蓄積されていた通知/リクエストを一気にフラッシュ転送
            while (_pendingMessages.TryDequeue(out var msg))
            {
                ForwardDirect(msg);
            }
        }
        catch (Exception ex)
        {
            _upstreamStartError = ex.Message;
            Console.Error.WriteLine($"[LspProxy] Failed to initialize upstream server in background: {ex.Message}");
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
            // FastLSP 側で Capabilities (rename, codeAction を含む) を即座に返答 (< 0.2ms)！
            _fastServer.HandleMessage(message);

            // Upstream にも非同期転送して Roslyn のバックグラウンド初期化を開始
            ForwardToUpstream(message);
            return true;
        }

        if (method is "initialized" or "shutdown" or "exit")
        {
            bool res = _fastServer.HandleMessage(message);
            ForwardToUpstream(message);
            return res;
        }

        // 4. FastLSP 単体ではできない重い機能 (rename, codeAction など)
        // Upstream (UnityCSharpLSP) に委譲
        ForwardToUpstream(message);
        return true;
    }

    private void ForwardToUpstream(byte[] message)
    {
        if (_upstreamReady && _upstreamInput != null && _upstreamProcess != null && !_upstreamProcess.HasExited)
        {
            ForwardDirect(message);
        }
        else
        {
            _pendingMessages.Enqueue(message);
        }
    }

    private void ForwardDirect(byte[] message)
    {
        if (_upstreamInput == null || _upstreamProcess == null || _upstreamProcess.HasExited)
        {
            return;
        }

        try
        {
            byte[] frame = LspMessageWriter.Frame(message);
            lock (_upstreamInput)
            {
                _upstreamInput.BaseStream.Write(frame, 0, frame.Length);
                _upstreamInput.BaseStream.Flush();
            }
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
                if (root.TryGetProperty("result", out var resElem) && resElem.ValueKind == JsonValueKind.Object &&
                    resElem.TryGetProperty("capabilities", out _))
                {
                    continue;
                }

                // Upstream の診断やレスポンス (rename, codeAction 等) をクライアントに透過転送
                byte[] frame = LspMessageWriter.Frame(message);
                lock (_clientOutput)
                {
                    _clientOutput.Write(frame, 0, frame.Length);
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

    private static Process StartUpstreamProcess(string? explicitPath)
    {
        var candidates = new List<string>();

        // 1. コマンドライン引数 (--upstream <path>)
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }

        // 2. 環境変数 UNITY_CSHARP_LSP_PATH
        string env = Environment.GetEnvironmentVariable("UNITY_CSHARP_LSP_PATH") ?? "";
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        // 3. 実行ファイル名 (PATH探索はOSに任せる)
        candidates.Add("UnityCSharpLSP.Server.exe");
        candidates.Add("UnityCSharpLSP.exe");
        candidates.Add("UnityCSharpLSP.Server");
        candidates.Add("UnityCSharpLSP");

        // 4. 標準インストール先ディレクトリのフォールバック
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\UnityCSharpLSP\UnityCSharpLSP.Server.exe"));
        candidates.Add(@"D:\wsgit\UnityCSharpLSP\artifacts\publish\UnityCSharpLSP.Server.exe");

        foreach (var cmd in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = "--stdio",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                var proc = Process.Start(psi);
                if (proc != null && !proc.HasExited)
                {
                    Console.Error.WriteLine($"LspProxy: Successfully spawned upstream Roslyn server: {cmd}");
                    return proc;
                }
            }
            catch (Exception)
            {
                // OS が PATH または指定パスから実行可能ファイルを見つけられなかった場合、次の候補へ
                continue;
            }
        }

        // 5. すべて見つからなかった場合はエラー終了
        throw new InvalidOperationException(
            "Upstream Roslyn LSP server could not be started.\n" +
            "Priority order checked:\n" +
            "  1. Explicit argument (--upstream <path>)\n" +
            "  2. Environment variable (UNITY_CSHARP_LSP_PATH)\n" +
            "  3. OS PATH resolution (UnityCSharpLSP.Server.exe, UnityCSharpLSP.exe)\n" +
            "  4. Standard installation directories (%LOCALAPPDATA%\\Programs\\UnityCSharpLSP, etc.)\n" +
            "  5. FAILED: No upstream server could be executed. Please ensure UnityCSharpLSP is in PATH or specify --upstream <path>."
        );
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
