using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Server.Mcp;

namespace UnityCSharpFastLSP.Server;

internal static class Program
{
    private const string HelpDescription =
@"UnityCSharpFastLSP Server (Ultra-Fast C# Language Server for Unity)
Powered by csff CST & Lightweight Semantics

USAGE:
  UnityCSharpFastLSP.Server [options]

MODES:
  (default)        Run as LSP server over stdio (JSON-RPC 2.0). Normal mode for editors and AI agents.
  --mcp            Run as MCP (Model Context Protocol) server over stdio (newline-delimited JSON-RPC).
                   Tools map 1:1 to LSP methods (textDocument/definition -> textDocument_definition).
                    Standalone answers only the fast methods (others return error -32601);
                   add --proxy to fall back to an upstream LSP server for the rest.

OPTIONS:
  --stdio              Use standard input/output for LSP JSON-RPC communication (default: true).
  --proxy              Enable Intelligent Hybrid Proxy mode.
                       - Supported features are answered immediately by FastLSP (<0.3ms).
                       - Unhandled heavy requests / empty fast results are forwarded to an upstream LSP server.
                       - Upstream server is resolved via priority: 1) --upstream / --upstream-kind,
                         2) kind-specific environment variable, 3) PATH, 4) Standard directories.
  --upstream-kind <k>  Fallback upstream server: auto (default), unitycsharp, csharp-ls, omnisharp.
                       'auto' tries UnityCSharpLSP -> csharp-ls -> OmniSharp until one starts.
                       The chosen executable gets per-server args (unitycsharp: --stdio,
                       csharp-ls: none, omnisharp: -lsp [-s <workspace>]).
  --upstream <path>    Explicit path to the upstream LSP executable (e.g. UnityCSharpLSP.Server.exe,
                       csharp-ls, OmniSharp.exe). Explicit paths are tried first.
  --workspace <path>   Workspace root used as the upstream working directory (and OmniSharp -s).
                       MCP mode default: nearest ancestor of the current directory that contains
                       Assets/ or ProjectSettings/ (or the current directory).
  --settings <json>    MCP mode: settings sent with workspace/didChangeConfiguration at startup
                       (effective with --proxy only). Use @<file> to read the JSON from a file.
  --timeout <sec>      MCP mode: timeout for each LSP request in seconds (default: 120).
  --clear-capabilities-cache [kind]
                       Delete the cached upstream capabilities (all kinds, or only auto / unitycsharp /
                       csharp-ls / omnisharp) and exit. See UPSTREAM CAPABILITIES CACHE below.
  --selftest, --test   Run end-to-end verification self-test (cross-file definition jumps) and exit.
  --print-config       Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
                       With --mcp, print the MCP configuration snippet (mcpServers format) instead.
  -h, --help           Show this help and exit.

INTELLIGENT HYBRID PROXY PRIORITY:
  Request Processing:
    - Supported features (completion, hover, definition, declaration, implementation, typeDefinition, references,
      formatting, signatureHelp, semanticTokens, documentSymbol, workspace/symbol):
      FAST PATH -> Answered directly by FastLSP (0.1ms - 0.4ms).
    - Heavy features (rename, codeAction, compiler diagnostics CS0029/CS0103):
      HEAVY PATH -> Forwarded to upstream Roslyn server.
  Upstream Server Discovery (--upstream-kind auto):
    1. Explicit path passed to --upstream <path> (kind inferred from its file name)
    2. Kind-specific environment variable:
       UNITY_CSHARP_LSP_PATH (UnityCSharpLSP), CSHARP_LS_PATH (csharp-ls), OMNISHARP_PATH (OmniSharp)
    3. OS PATH resolution (UnityCSharpLSP.Server.exe, csharp-ls, omnisharp, ...)
    4. Standard installation directories (%LOCALAPPDATA%\Programs\..., ~/.dotnet/tools, ...)
    5. Providers are tried in order: UnityCSharpLSP -> csharp-ls -> OmniSharp.
       With --upstream-kind <k> only that provider is tried. Upstream failure is non-fatal:
       FastLSP keeps answering its fast methods; heavy / empty-result requests cannot fall back.

UPSTREAM CAPABILITIES CACHE (--proxy):
    The proxy answers 'initialize' immediately and does not wait for the upstream server.
    It advertises FastLSP's capabilities merged with the upstream capabilities saved last time
    for the same --upstream-kind. FastLSP's own entries win (textDocumentSync, positionEncoding
    and semanticTokensProvider are never taken from upstream). The upstream's initialize reply
    updates the cache, so changes take effect on the next start. The first start has no cache.
    Location: %LOCALAPPDATA%\UnityCSharpFastLSP\upstream-capabilities\<kind>.json
              (override with UNITY_CSHARP_FAST_LSP_CACHE_DIR). Clear with --clear-capabilities-cache.

LLM / AI AGENT INTEGRATION (OpenCode, Claude Code, Antigravity, Cursor):
  To register this fast LSP server with OpenCode, add to your opencode.json:
    {
      ""lsp"": {
        ""unity-csharp-fast-2nek"": {
          ""command"": [""<path/to/UnityCSharpFastLSP.Server.exe>"", ""--stdio""],
          ""extensions"": ["".cs""]
        }
      }
    }
  Alternatively, run `UnityCSharpFastLSP.Server --print-config` to auto-generate this snippet with your resolved binary path.

MCP CLIENT INTEGRATION (Claude Code, OpenCode, etc.):
  Run `UnityCSharpFastLSP.Server --print-config --mcp [--proxy] [--workspace <path>]` to generate this snippet:
    {
      ""mcpServers"": {
        ""unity-csharp-fast-lsp"": {
          ""command"": ""<path/to/UnityCSharpFastLSP.Server.exe>"",
          ""args"": [""--mcp"", ""--workspace"", ""<path>""]
        }
      }
    }

LLM / AI AGENT GUIDE (MCP):
  Choosing the mode:
    - Read-only navigation (definition, references, symbols, hover, completion):
      use --mcp (standalone, fast, no upstream needed).
    - rename, codeAction, type/call hierarchy, compiler diagnostics, unitycsharp.*
      commands (asset / GUID search): use --mcp --proxy. This needs an upstream LSP server.
      Upstream lookup order: --upstream / --upstream-kind, kind-specific env var, PATH, standard dirs.
  Calling tools:
    - Tool name = LSP method with '/' replaced by '_' (textDocument_definition).
    - Arguments are the LSP params object. Positions are 0-based (line, UTF-16 character).
    - textDocument.uri can be a file:/// URI, an absolute path, or a workspace-relative path.
    - Files are synced from disk automatically; no didOpen is needed.
    - Edit tools (rename, codeAction, formatting, ...) return a WorkspaceEdit only.
      They do not apply it. Apply it with your own file edit tool.
    - After a batch of edits, call lsp_reset to reload the workspace.
  Reading errors:
    - isError with {""code"":-32601} and a message containing --proxy: the tool is not
      supported standalone. Restart the server with --mcp --proxy.
    - Upstream start failure message: set --upstream / --upstream-kind or the kind-specific path env var.
    - An unknown tool name is a JSON-RPC error -32602 (not isError). Check the name.
  Full reference: doc/mcp.md (tool table, routing, known limitations).

NOTES:
  - stdout is strictly reserved for LSP JSON-RPC (and MCP JSON-RPC in --mcp mode); all logs and diagnostics output to stderr.
  - Supports incremental parsing, live syntax diagnostics, hierarchical symbols, definition/references, and Unity messages/attributes completion.";

    private static int Main(string[] args)
    {
        if (args.Length > 0)
        {
            if (args.Contains("--selftest") || args.Contains("--test"))
            {
                return SelfTestRunner.RunSelfTest();
            }

            if (args.Contains("--help") || args.Contains("-h"))
            {
                Console.WriteLine(HelpDescription);
                return 0;
            }

            if (args.Contains("--clear-capabilities-cache"))
            {
                return ClearCapabilitiesCache(args);
            }

            if (args.Contains("--print-config"))
            {
                if (args.Contains("--mcp"))
                {
                    return PrintMcpConfig(args);
                }

                string exePath = (Environment.ProcessPath ?? "UnityCSharpFastLSP.Server.exe").Replace('\\', '/');
                Console.WriteLine(
$$"""
{
  "lsp": {
    "unity-csharp-fast-2nek": {
      "command": ["{{exePath}}", "--stdio"],
      "extensions": [".cs"]
    }
  }
}
""");
                return 0;
            }

            if (args.Contains("--mcp"))
            {
                return RunMcpMode(args);
            }
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();

        bool isProxy = args.Contains("--proxy") || args.Any(a => a.StartsWith("--upstream"));
        bool kindOk = TryParseUpstreamKind(args, out UpstreamKind upstreamKind);
        bool workspaceOk = TryGetOptionValue(args, "--workspace", out string? workspaceArg);
        if (!kindOk || !workspaceOk)
        {
            return 1;
        }

        string? upstreamPath = null;
        int uIdx = Array.IndexOf(args, "--upstream");
        if (uIdx >= 0 && uIdx + 1 < args.Length)
        {
            upstreamPath = args[uIdx + 1];
        }

        if (isProxy)
        {
            Console.Error.WriteLine($"UnityCSharpFastLSP.Server: Running in Intelligent Proxy mode (upstream={UpstreamServerRegistry.DisplayName(upstreamKind)})");
            try
            {
                using var proxy = new LspProxy(
                    output,
                    upstreamPath,
                    upstreamKind,
                    workspaceArg != null ? Path.GetFullPath(workspaceArg) : null);
                while (LspMessageReader.TryReadMessage(input, out byte[]? message))
                {
                    if (message is null || !proxy.HandleClientMessage(message))
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FATAL ERROR] Proxy initialization failed: {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine("UnityCSharpFastLSP.Server: initialized (Standalone Ultra-Fast Mode)");
            var server = new LspServer(output);
            while (LspMessageReader.TryReadMessage(input, out byte[]? message))
            {
                if (message is null || !server.HandleMessage(message))
                {
                    break;
                }
            }
        }

        return 0;
    }

    // ---------- MCP モード ----------

    /// <summary>
    /// --mcp: stdio の MCP サーバーとして起動する。--proxy 併用で upstream LSP にフォールバック。
    /// stdout は MCP 専用。ログは stderr。
    /// </summary>
    private static int RunMcpMode(string[] args)
    {
        bool workspaceOk = TryGetOptionValue(args, "--workspace", out string? workspaceArg);
        bool settingsOk = TryGetOptionValue(args, "--settings", out string? settingsArg);
        bool timeoutOk = TryGetOptionValue(args, "--timeout", out string? timeoutArg);
        bool upstreamOk = TryGetOptionValue(args, "--upstream", out string? upstreamArg);
        bool kindOk = TryParseUpstreamKind(args, out UpstreamKind upstreamKind);
        if (!workspaceOk || !settingsOk || !timeoutOk || !upstreamOk || !kindOk)
        {
            return 1;
        }

        string root = workspaceArg != null
            ? Path.GetFullPath(workspaceArg)
            : McpServer.FindWorkspaceRoot(Environment.CurrentDirectory);

        JsonNode? settings = null;
        if (settingsArg != null)
        {
            try
            {
                string json = settingsArg.StartsWith('@') ? File.ReadAllText(settingsArg[1..]) : settingsArg;
                settings = JsonNode.Parse(json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"UnityCSharpFastLSP.Server: invalid --settings: {ex.Message}");
                return 1;
            }
        }

        TimeSpan timeout = TimeSpan.FromSeconds(120);
        if (timeoutArg != null)
        {
            if (!int.TryParse(timeoutArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds <= 0)
            {
                Console.Error.WriteLine($"UnityCSharpFastLSP.Server: invalid --timeout '{timeoutArg}' (positive integer seconds expected).");
                return 1;
            }
            timeout = TimeSpan.FromSeconds(seconds);
        }

        bool proxy = args.Contains("--proxy") || args.Any(a => a.StartsWith("--upstream"));
        Console.Error.WriteLine($"UnityCSharpFastLSP.Server: MCP mode ({(proxy ? "proxy" : "standalone")}), workspace={root}, upstream={UpstreamServerRegistry.DisplayName(upstreamKind)}");

        try
        {
            var options = new McpServerOptions(root, proxy, upstreamArg, settings, timeout, upstreamKind);
            using var server = new McpServer(options, Console.OpenStandardInput(), Console.OpenStandardOutput());
            return server.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FATAL ERROR] MCP initialization failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>--print-config --mcp: MCP クライアント用の mcpServers 設定を出力する。</summary>
    private static int PrintMcpConfig(string[] args)
    {
        bool workspaceOk = TryGetOptionValue(args, "--workspace", out string? workspaceArg);
        bool upstreamOk = TryGetOptionValue(args, "--upstream", out string? upstreamArg);
        bool kindOk = TryParseUpstreamKind(args, out UpstreamKind upstreamKind);
        if (!workspaceOk || !upstreamOk || !kindOk)
        {
            return 1;
        }

        string exePath = (Environment.ProcessPath ?? "UnityCSharpFastLSP.Server.exe").Replace('\\', '/');

        var mcpArgs = new JsonArray("--mcp");
        if (args.Contains("--proxy") || upstreamArg != null)
        {
            mcpArgs.Add("--proxy");
        }
        if (upstreamArg != null)
        {
            mcpArgs.Add("--upstream");
            mcpArgs.Add(upstreamArg.Replace('\\', '/'));
        }
        if (upstreamKind != UpstreamKind.Auto)
        {
            mcpArgs.Add("--upstream-kind");
            mcpArgs.Add(UpstreamServerRegistry.CanonicalName(upstreamKind));
        }
        if (workspaceArg != null)
        {
            mcpArgs.Add("--workspace");
            mcpArgs.Add(workspaceArg.Replace('\\', '/'));
        }

        var config = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["unity-csharp-fast-lsp"] = new JsonObject
                {
                    ["command"] = exePath,
                    ["args"] = mcpArgs,
                },
            },
        };

        Console.WriteLine(config.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return 0;
    }

    /// <summary>
    /// --clear-capabilities-cache [kind]: キャッシュ済みの upstream capabilities を削除して終了する。
    /// kind 省略時は全 kind。
    /// </summary>
    private static int ClearCapabilitiesCache(string[] args)
    {
        UpstreamKind? kind = null;
        int index = Array.IndexOf(args, "--clear-capabilities-cache");
        if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            try
            {
                kind = UpstreamServerRegistry.ParseKind(args[index + 1]);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"UnityCSharpFastLSP.Server: {ex.Message}");
                return 1;
            }
        }

        var cache = UpstreamCapabilitiesCache.CreateDefault();
        List<string> deleted;
        try
        {
            deleted = cache.Clear(kind);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"UnityCSharpFastLSP.Server: could not clear capabilities cache: {ex.Message}");
            return 1;
        }

        string target = kind is UpstreamKind k ? UpstreamServerRegistry.CanonicalName(k) : "all kinds";
        Console.WriteLine($"Capabilities cache ({target}) in {cache.Directory}:");
        if (deleted.Count == 0)
        {
            Console.WriteLine("  nothing to delete");
        }
        foreach (string path in deleted)
        {
            Console.WriteLine($"  deleted {path}");
        }
        return 0;
    }

    /// <summary>--upstream-kind を解析する。未指定なら auto（全種を順に探索）。</summary>
    private static bool TryParseUpstreamKind(string[] args, out UpstreamKind kind)
    {
        kind = UpstreamKind.Auto;
        if (!TryGetOptionValue(args, "--upstream-kind", out string? value)) return false;
        try
        {
            kind = UpstreamServerRegistry.ParseKind(value);
            return true;
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"UnityCSharpFastLSP.Server: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// "--name value" 形式のオプション値を取得する。未指定なら true（value = null）。
    /// 指定されたが値が無い、または値が別のオプションの場合は stderr にエラーを出して false。
    /// </summary>
    private static bool TryGetOptionValue(string[] args, string name, out string? value)
    {
        value = null;
        int index = Array.IndexOf(args, name);
        if (index < 0) return true;

        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"UnityCSharpFastLSP.Server: {name} requires a value.");
            return false;
        }

        value = args[index + 1];
        return true;
    }
}
