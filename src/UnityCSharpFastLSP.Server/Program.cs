using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Server;

internal static class Program
{
    private static int Main()
    {
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
