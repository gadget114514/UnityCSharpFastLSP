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
    private readonly UpstreamKind _requestedKind;
    private readonly string? _workspacePath;
    private volatile UpstreamKind _upstreamKind;

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
                ? $"Upstream {UpstreamServerRegistry.DisplayName(_upstreamKind)} exited (code {proc.ExitCode})."
                : null;
        }
    }

    private volatile string? _upstreamStartError;

    /// <summary>実際に起動した upstream の種類（未起動なら要求された種類）。</summary>
    public UpstreamKind SelectedKind => _upstreamReady ? _upstreamKind : _requestedKind;

    public LspProxy(
        Stream clientOutput,
        string? upstreamExecutablePath = null,
        UpstreamKind upstreamKind = UpstreamKind.Auto,
        string? workspacePath = null)
    {
        _clientOutput = clientOutput;
        _fastServer = new LspServer(clientOutput, isProxy: true);
        _requestedKind = upstreamKind;
        _workspacePath = workspacePath;
        _upstreamKind = upstreamKind;

        // バックグラウンドで Upstream を非同期起動 (メインスレッドおよび initialize を一切待たせない！)
        Task.Run(() => InitUpstreamBackground(upstreamExecutablePath, _cts.Token));
    }

    private void InitUpstreamBackground(string? upstreamExecutablePath, CancellationToken ct)
    {
        var attempts = UpstreamServerRegistry.ResolveAttempts(_requestedKind, upstreamExecutablePath);
        var failures = new List<string>();

        foreach (UpstreamAttempt attempt in attempts)
        {
            try
            {
                var proc = StartUpstreamProcess(attempt, _workspacePath);
                _upstreamProcess = proc;
                _upstreamInput = proc.StandardInput;
                _upstreamKind = attempt.Kind;
                _upstreamReady = true;

                string name = UpstreamServerRegistry.DisplayName(attempt.Kind);
                Console.Error.WriteLine($"[LspProxy] Upstream ready: {name} ({proc.StartInfo.FileName})");

                // Upstream の出力を非同期に読み取りクライアントに転送
                Task.Run(() => ReadUpstreamLoop(proc.StandardOutput.BaseStream, ct));
                Task.Run(() => ReadUpstreamStderrLoop(name, proc.StandardError, ct));

                // 起動前に蓄積されていた通知/リクエストを一気にフラッシュ転送
                while (_pendingMessages.TryDequeue(out var msg))
                {
                    ForwardDirect(msg);
                }
                return;
            }
            catch (Exception ex)
            {
                string name = UpstreamServerRegistry.DisplayName(attempt.Kind);
                Console.Error.WriteLine($"[LspProxy] Upstream {name} could not be started: {ex.Message}");
                failures.Add($"{name}: {ex.Message}");
            }
        }

        _upstreamStartError = "No upstream LSP server could be started.\n" + string.Join("\n", failures);
        Console.Error.WriteLine($"[LspProxy] Failed to initialize upstream server in background: {_upstreamStartError}");
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
        //    ただし結果が空 (null / [] / {} / "") の場合は Upstream に委譲する。
        if (FastMethods.Contains(method))
        {
            return HandleFastRequest(message);
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

    /// <summary>
    /// FastLSP に処理させつつ応答を捕捉し、結果が空なら Upstream (UnityCSharpLSP) に委譲する。
    /// 非空なら捕捉した応答をクライアントへ返すので、通常の高速パスのレイテンシは変わらない。
    /// </summary>
    private bool HandleFastRequest(byte[] message)
    {
        _fastServer.BeginCapture();
        bool result;
        List<byte[]> captured;
        try
        {
            result = _fastServer.HandleMessage(message);
        }
        finally
        {
            captured = _fastServer.EndCapture();
        }

        byte[]? response = null;
        foreach (byte[] body in captured)
        {
            if (HasMethod(body))
            {
                // 通知やサーバー発リクエストはそのままクライアントへ流す。
                WriteToClient(body);
            }
            else
            {
                // 1 リクエストにつき応答は 1 つ。最後のものを採用する。
                response = body;
            }
        }

        if (response == null)
        {
            return result;
        }

        if (IsEmptyResult(response) && CanFallbackToUpstream())
        {
            // FastLSP の空応答は捨て、Upstream に同じ id で転送する。
            // Upstream の応答は ReadUpstreamLoop がクライアントへ透過転送する。
            ForwardToUpstream(message);
            return true;
        }

        WriteToClient(response);
        return result;
    }

    /// <summary>Upstream が起動済みで、まだ生存しているか（フォールバック可能か）。</summary>
    private bool CanFallbackToUpstream()
    {
        return _upstreamReady && _upstreamStartError == null
            && _upstreamProcess != null && !_upstreamProcess.HasExited;
    }

    private void WriteToClient(byte[] body)
    {
        byte[] frame = LspMessageWriter.Frame(body);
        lock (_clientOutput)
        {
            _clientOutput.Write(frame, 0, frame.Length);
            _clientOutput.Flush();
        }
    }

    /// <summary>サーバー → クライアントの通知 / リクエスト（"method" を持つ）か。</summary>
    private static bool HasMethod(byte[] body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("method", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 応答の result が空（null / 空文字 / 空配列 / 空オブジェクト）か。
    /// エラー応答はフォールバック対象外として false を返す。
    /// </summary>
    private static bool IsEmptyResult(byte[] body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("error", out _)) return false;

            if (!root.TryGetProperty("result", out JsonElement result)) return true;

            return result.ValueKind switch
            {
                JsonValueKind.Null => true,
                JsonValueKind.String => string.IsNullOrEmpty(result.GetString()),
                JsonValueKind.Array => result.GetArrayLength() == 0,
                JsonValueKind.Object => !result.EnumerateObject().Any(),
                _ => false
            };
        }
        catch (JsonException)
        {
            return false;
        }
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

    private async Task ReadUpstreamStderrLoop(string name, StreamReader stderr, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            string? line = await stderr.ReadLineAsync(ct);
            if (line == null) break;
            Console.Error.WriteLine($"[Upstream {name}] {line}");
        }
    }

    private static Process StartUpstreamProcess(UpstreamAttempt attempt, string? workspacePath)
    {
        string name = UpstreamServerRegistry.DisplayName(attempt.Kind);
        string arguments = UpstreamServerRegistry.GetStartArguments(attempt.Kind, workspacePath);
        var failures = new List<string>();

        foreach (string cmd in UpstreamServerRegistry.GetCandidateCommands(attempt))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = cmd,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
                {
                    psi.WorkingDirectory = workspacePath;
                }

                var proc = Process.Start(psi);
                if (proc != null && !proc.HasExited)
                {
                    return proc;
                }
                failures.Add($"'{cmd}' (exited immediately)");
            }
            catch (Exception ex)
            {
                // OS が PATH または指定パスから実行可能ファイルを見つけられなかった場合、次の候補へ
                failures.Add($"'{cmd}' ({ex.Message})");
            }
        }

        throw new InvalidOperationException(
            $"Could not start {name}. Tried: {string.Join(", ", failures)}.");
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
