using UnityCSharpFastLSP.Protocol;

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

OPTIONS:
  --stdio              Use standard input/output for LSP JSON-RPC communication (default: true).
  --proxy              Enable Intelligent Hybrid Proxy mode.
                       - Supported features are answered immediately by FastLSP (<0.3ms).
                       - Unhandled heavy requests are forwarded to upstream Roslyn server.
                       - Upstream server is resolved via priority: 1) --upstream, 2) UNITY_CSHARP_LSP_PATH,
                         3) PATH environment variable, 4) Standard directories. (5: Fails with error).
  --upstream <path>    Explicit path to upstream Roslyn LSP server (e.g. UnityCSharpLSP.Server.exe).
                       If omitted with --proxy, automatically searches following priority 2 -> 3 -> 4.
  --selftest, --test   Run end-to-end verification self-test (cross-file definition jumps) and exit.
  --print-config       Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
  -h, --help           Show this help and exit.

INTELLIGENT HYBRID PROXY PRIORITY:
  Request Processing:
    - Supported features (completion, hover, definition, declaration, implementation, typeDefinition, references,
      formatting, signatureHelp, semanticTokens, documentSymbol, workspace/symbol):
      FAST PATH -> Answered directly by FastLSP (0.1ms - 0.4ms).
    - Heavy features (rename, codeAction, compiler diagnostics CS0029/CS0103):
      HEAVY PATH -> Forwarded to upstream Roslyn server.
  Upstream Server Discovery:
    1. Explicit path passed to --upstream <path>
    2. Environment variable UNITY_CSHARP_LSP_PATH
    3. OS PATH resolution (OS searches PATH for UnityCSharpLSP.Server.exe, UnityCSharpLSP.exe)
    4. Standard installation directories (%LOCALAPPDATA%\Programs\UnityCSharpLSP, etc.)
    5. FAILED -> Terminates immediately with error code 1 if upstream is not found.

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

NOTES:
  - stdout is strictly reserved for LSP JSON-RPC; all logs and diagnostics output to stderr.
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

            if (args.Contains("--print-config"))
            {
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
        }

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();

        bool isProxy = args.Contains("--proxy") || args.Any(a => a.StartsWith("--upstream"));
        string? upstreamPath = null;
        int uIdx = Array.IndexOf(args, "--upstream");
        if (uIdx >= 0 && uIdx + 1 < args.Length)
        {
            upstreamPath = args[uIdx + 1];
        }

        if (isProxy)
        {
            Console.Error.WriteLine("UnityCSharpFastLSP.Server: Running in Intelligent Proxy mode");
            try
            {
                using var proxy = new LspProxy(output, upstreamPath);
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
}
