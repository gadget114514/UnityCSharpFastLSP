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
  --stdio          Use standard input/output for LSP JSON-RPC communication (default: true).
  --print-config   Print LLM agent LSP configuration snippet (opencode.json format) to stdout and exit.
  -h, --help       Show this help and exit.

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

        Console.Error.WriteLine("UnityCSharpFastLSP.Server: initialized (Phase 1-3 Fast Server)");

        var server = new LspServer(output);

        while (LspMessageReader.TryReadMessage(input, out byte[]? message))
        {
            if (message is null || !server.HandleMessage(message))
            {
                break;
            }
        }

        return 0;
    }
}
