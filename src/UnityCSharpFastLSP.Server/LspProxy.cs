using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Server;

public sealed class LspProxy : IDisposable
{
    private readonly LspServer _fastServer;
    private readonly Stream _clientOutput;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentQueue<byte[]> _pendingMessages = new();
    private readonly object _pendingLock = new();

    /// <summary>upstream プロセスを起動する関数（テストで差し替え可能）。</summary>
    private readonly Func<UpstreamAttempt, string?, Process> _upstreamStarter;
    private Process? _upstreamProcess;
    private StreamWriter? _upstreamInput;
    private volatile bool _upstreamReady;
    private readonly UpstreamKind _requestedKind;
    private readonly string? _workspacePath;
    private volatile UpstreamKind _upstreamKind;
    private readonly UpstreamCapabilitiesCache _capabilitiesCache;

    /// <summary>クライアントの initialize リクエスト id（生 JSON）。upstream の initialize 応答の識別に使う。</summary>
    private volatile string? _initializeRequestId;

    /// <summary>今回 advertise に使ったキャッシュ済み upstream capabilities（無ければ null）。</summary>
    private JsonObject? _advertisedUpstreamCapabilities;

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
        "workspace/symbol",
    };

    /// <summary>
    /// <c>unitylsp/*</c> 拡張メソッド。ルーティングの優先順位が <see cref="FastMethods"/> と逆で、
    /// upstream が capabilities.experimental.unitylsp.methods で advertise していれば upstream だけが答え、
    /// FastLSP 側のその機能（GUID 索引など）は完全に止める（フォールバックもしない）。
    /// advertise していないときだけ FastLSP が答える。
    /// （<see cref="FastMethods"/> は FastLSP 優先で、空結果のときだけ upstream にフォールバックする。）
    /// </summary>
    public const string UnityLspPrefix = "unitylsp/";

    /// <summary>
    /// upstream が advertise した unitylsp/* メソッド。upstream の initialize 応答で確定し、
    /// それまでは前回キャッシュした capabilities の値を使う（キャッシュも無ければ空 = FastLSP が答える）。
    /// </summary>
    private volatile HashSet<string> _upstreamUnityLspMethods = new(StringComparer.Ordinal);

    public static bool IsUnityLspMethod(string method) => method.StartsWith(UnityLspPrefix, StringComparison.Ordinal);

    /// <summary>
    /// FastLSP が応答し得るメソッドか（false なら Upstream へ委譲される）。
    /// unitylsp/* は upstream が advertise していなければ FastLSP が応答するので true（upstream 不在でもエラーにしない）。
    /// </summary>
    public static bool IsFastPath(string method) => FastMethods.Contains(method) || IsUnityLspMethod(method);

    /// <summary>FastLSP 側の GUID 機能（索引・unitylsp/guid* 応答）が有効か。upstream が advertise すると false。</summary>
    public bool FastGuidFeatureEnabled => _fastServer.State.WorkspaceManager.GuidIndex.Enabled;

    public RequestCancellationRegistry CancellationRegistry => _fastServer.CancellationRegistry;

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
        string? workspacePath = null,
        UpstreamCapabilitiesCache? capabilitiesCache = null,
        Func<UpstreamAttempt, string?, Process>? upstreamStarter = null,
        RequestCancellationRegistry? cancellationRegistry = null)
    {
        _upstreamStarter = upstreamStarter ?? StartUpstreamProcess;
        _capabilitiesCache = capabilitiesCache ?? UpstreamCapabilitiesCache.CreateDefault();
        _clientOutput = clientOutput;
        _fastServer = new LspServer(clientOutput, isProxy: true, cancellationRegistry);
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
                var proc = _upstreamStarter(attempt, _workspacePath);
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
                DrainPending();
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

        // upstream が起動しなかったので advertise は無い: unitylsp/* は FastLSP が受け持つ
        ApplyUpstreamUnityLspMethods(new HashSet<string>(StringComparer.Ordinal), "upstream could not be started");

        // 起動待ちで溜まっていたリクエストにはエラーを返す（通知は捨てる）
        DrainPending();
    }

    public bool HandleClientMessage(byte[] message)
    {
        using JsonDocument doc = JsonDocument.Parse(message);
        JsonElement root = doc.RootElement;
        string method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";

        if (method == "$/cancelRequest")
        {
            if (root.TryGetProperty("params", out var cancelParams) &&
                cancelParams.ValueKind == JsonValueKind.Object &&
                cancelParams.TryGetProperty("id", out JsonElement cancelId))
            {
                CancellationRegistry.Cancel(cancelId.GetRawText());
            }
            ForwardToUpstream(message);
            return true;
        }

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
            return HandleFastRequest(message, allowFallback: true);
        }

        // 2b. unitylsp/* 拡張: upstream 優先、FastLSP はフォールバック
        if (IsUnityLspMethod(method))
        {
            return HandleUnityLspRequest(root, method, message);
        }

        // 3. initialize / initialized / shutdown / exit
        if (method == "initialize")
        {
            // FastLSP 側で Capabilities (rename, codeAction を含む) を即座に返答 (< 0.2ms)！
            // upstream の応答は待たず、前回キャッシュした upstream capabilities をマージして advertise する。
            if (root.TryGetProperty("id", out JsonElement idElem))
            {
                _initializeRequestId = idElem.GetRawText();
            }
            HandleInitialize(message);

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
    /// FastLSP の initialize 応答を捕捉し、キャッシュ済みの upstream capabilities をマージしてクライアントへ返す。
    /// </summary>
    private void HandleInitialize(byte[] message)
    {
        // FastLSP がインデックスを始める前に、キャッシュ済み upstream の advertise で unitylsp/* の担当を決める
        JsonObject? cached = _capabilitiesCache.Load(_requestedKind);
        _advertisedUpstreamCapabilities = cached;
        ApplyUpstreamUnityLspMethods(UpstreamCapabilitiesCache.GetUnityLspMethods(cached), "cached capabilities");

        _fastServer.BeginCapture();
        List<byte[]> captured;
        try
        {
            _fastServer.HandleMessage(message);
        }
        finally
        {
            captured = _fastServer.EndCapture();
        }

        if (cached != null)
        {
            Console.Error.WriteLine(
                $"[LspProxy] Advertising cached {UpstreamServerRegistry.DisplayName(_requestedKind)} capabilities ({_capabilitiesCache.GetPath(_requestedKind)})");
        }

        foreach (byte[] body in captured)
        {
            WriteToClient(cached != null ? MergeCapabilities(body, cached) : body);
        }
    }

    /// <summary>initialize 応答の result.capabilities に upstream の capabilities を足した本文を返す。</summary>
    private static byte[] MergeCapabilities(byte[] body, JsonObject upstream)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject root &&
                root["result"]?["capabilities"] is JsonObject fast)
            {
                UpstreamCapabilitiesCache.MergeInto(fast, upstream);
                return Encoding.UTF8.GetBytes(root.ToJsonString());
            }
        }
        catch (JsonException)
        {
        }
        return body;
    }

    /// <summary>upstream の initialize 応答から capabilities を取り出し、kind ごとのキャッシュを更新する。</summary>
    private void SaveUpstreamCapabilities(JsonElement result)
    {
        if (JsonNode.Parse(result.GetRawText()) is not JsonObject resultNode) return;
        if (resultNode["capabilities"] is not JsonObject capabilities) return;

        ApplyUpstreamUnityLspMethods(UpstreamCapabilitiesCache.GetUnityLspMethods(capabilities), "upstream initialize");
        JsonNode? serverInfo = resultNode["serverInfo"];

        UpstreamKind actual = _upstreamKind;
        bool changed = _capabilitiesCache.Save(actual, capabilities, serverInfo);
        if (_requestedKind != actual)
        {
            // auto 指定時は「auto で選ばれたもの」として次回の auto 起動でも使えるよう保存する
            changed |= _capabilitiesCache.Save(_requestedKind, capabilities, serverInfo);
        }

        bool matchesAdvertised = _advertisedUpstreamCapabilities != null &&
            JsonNode.DeepEquals(_advertisedUpstreamCapabilities, capabilities);
        if (changed || !matchesAdvertised)
        {
            Console.Error.WriteLine(
                $"[LspProxy] Cached {UpstreamServerRegistry.DisplayName(actual)} capabilities to {_capabilitiesCache.GetPath(actual)}" +
                (matchesAdvertised ? "" : "; they differ from what was advertised and take effect on the next start"));
        }
    }

    /// <summary>
    /// unitylsp/* リクエスト（upstream 優先）。upstream がそのメソッドを advertise していれば必ず upstream に送る
    /// （起動中はキュー、使えなければ RequestFailed。FastLSP にはフォールバックしない）。
    /// advertise していなければ FastLSP が答える。
    /// </summary>
    private bool HandleUnityLspRequest(JsonElement root, string method, byte[] message)
    {
        if (_upstreamUnityLspMethods.Contains(method))
        {
            ForwardToUpstream(message);
            return true;
        }

        // FastLSP に unitylsp/* 通知は無い
        if (!root.TryGetProperty("id", out _)) return true;

        return HandleFastRequest(message, allowFallback: false);
    }

    /// <summary>
    /// upstream が advertise した unitylsp/* を反映する。upstream が担当する機能は FastLSP 側で完全に止め
    /// （GUID 索引を破棄し、以後は作らない）、担当しない機能は FastLSP 側で有効にする。
    /// </summary>
    private void ApplyUpstreamUnityLspMethods(HashSet<string> methods, string source)
    {
        _upstreamUnityLspMethods = methods;

        bool upstreamOwnsGuid = methods.Contains(LspServer.GuidDefinitionMethod) &&
            methods.Contains(LspServer.GuidReferencesMethod);
        _fastServer.State.WorkspaceManager.SetGuidIndexEnabled(!upstreamOwnsGuid);

        Console.Error.WriteLine(methods.Count == 0
            ? $"[LspProxy] Upstream advertises no unitylsp/* methods ({source}); FastLSP answers them."
            : $"[LspProxy] Upstream advertises {string.Join(", ", methods.Order(StringComparer.Ordinal))} ({source}); " +
              (upstreamOwnsGuid ? "FastLSP GUID feature is stopped." : "those go to upstream."));
    }

    /// <summary>
    /// FastLSP に処理させつつ応答を捕捉し、結果が空なら Upstream (UnityCSharpLSP) に委譲する。
    /// 非空なら捕捉した応答をクライアントへ返すので、通常の高速パスのレイテンシは変わらない。
    /// </summary>
    private bool HandleFastRequest(byte[] message, bool allowFallback)
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

        if (allowFallback && IsEmptyResult(response) && CanFallbackToUpstream())
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

    /// <summary>upstream が起動済みで生存しているか。</summary>
    private bool IsUpstreamAlive =>
        _upstreamReady && _upstreamInput != null && _upstreamProcess != null && !_upstreamProcess.HasExited;

    private void ForwardToUpstream(byte[] message)
    {
        if (IsUpstreamAlive)
        {
            ForwardDirect(message);
        }
        else if (UpstreamError != null)
        {
            FailRequest(message);
        }
        else
        {
            _pendingMessages.Enqueue(message);
            // Enqueue と起動完了／起動失敗が競合して取り残されないよう、状態が確定していれば即座に処理する
            DrainPending();
        }
    }

    /// <summary>
    /// 溜まっているメッセージを処理する。upstream が生きていれば転送し、起動失敗・終了済みならリクエストに
    /// エラーを返す。まだ起動中なら何もしない。
    /// </summary>
    private void DrainPending()
    {
        lock (_pendingLock)
        {
            while (_pendingMessages.TryPeek(out _))
            {
                bool alive = IsUpstreamAlive;
                if (!alive && UpstreamError == null) return; // 起動中

                if (!_pendingMessages.TryDequeue(out byte[]? message)) return;
                if (alive)
                {
                    ForwardDirect(message);
                }
                else
                {
                    FailRequest(message);
                }
            }
        }
    }

    private void ForwardDirect(byte[] message)
    {
        StreamWriter? input = _upstreamInput;
        if (input == null || _upstreamProcess == null || _upstreamProcess.HasExited)
        {
            FailRequest(message);
            return;
        }

        try
        {
            byte[] frame = LspMessageWriter.Frame(message);
            lock (input)
            {
                input.BaseStream.Write(frame, 0, frame.Length);
                input.BaseStream.Flush();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"LspProxy: Error forwarding to upstream: {ex.Message}");
            FailRequest(message);
        }
    }

    /// <summary>
    /// upstream に届けられなかったメッセージがリクエストなら、RequestFailed のエラー応答をクライアントへ返す。
    /// 通知と、FastLSP が既に応答済みのリクエスト（initialize / shutdown）には何も返さない。
    /// </summary>
    private void FailRequest(byte[] message)
    {
        JsonObject response;
        string method;
        try
        {
            if (JsonNode.Parse(message) is not JsonObject request) return;
            if (!request.TryGetPropertyValue("id", out JsonNode? id)) return;
            method = request["method"]?.GetValue<string>() ?? "";
            if (method is "" or "initialize" or "shutdown") return;

            string reason = UpstreamError ?? "Upstream LSP server is not available.";
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id?.DeepClone(),
                ["error"] = new JsonObject
                {
                    ["code"] = JsonRpcErrorCode.RequestFailed,
                    ["message"] = $"{method} needs the upstream LSP server, which is not available. {reason}",
                },
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return;
        }

        WriteToClient(Encoding.UTF8.GetBytes(response.ToJsonString()));
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

                // initialize のレスポンスは FastLSP がすでに返答しているためクライアントへは送らない。
                // capabilities は次回起動の advertise 用にキャッシュする。
                if (!root.TryGetProperty("method", out _) &&
                    root.TryGetProperty("result", out var resElem) && resElem.ValueKind == JsonValueKind.Object &&
                    resElem.TryGetProperty("capabilities", out _) &&
                    IsInitializeResponse(root))
                {
                    SaveUpstreamCapabilities(resElem);
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

    private bool IsInitializeResponse(JsonElement root)
    {
        string? initId = _initializeRequestId;
        // id を記録できていない場合は従来どおり capabilities を持つ応答を initialize 応答とみなす
        if (initId == null) return true;
        return root.TryGetProperty("id", out JsonElement id) && id.GetRawText() == initId;
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
