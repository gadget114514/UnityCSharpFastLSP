using System.Diagnostics;
using System.Text;
using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;

const int DefaultWarmup = 50;
const int DefaultIterations = 400;

const string SampleSource = """
using UnityEngine;

// 日本語コメント 😀 絵文字の位置確認
public class SampleController : MonoBehaviour
{
    [SerializeField] private int _hp;

#if UNITY_EDITOR
    void OnEditorTick() { _hp++; }
#endif

    void Start() { _hp = 1; }
    void Update() { _hp++; }
}
""";



string? fileArg = null;
int warmup = DefaultWarmup;
int iterations = DefaultIterations;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--file" when i + 1 < args.Length:
            fileArg = args[++i];
            break;
        case "--warmup" when i + 1 < args.Length:
            warmup = int.Parse(args[++i]);
            break;
        case "--iterations" when i + 1 < args.Length:
            iterations = int.Parse(args[++i]);
            break;
        case "--help":
            Console.WriteLine("Phase0.Bench [--file <path>] [--warmup N] [--iterations N]");
            return;
    }
}

string primaryName = "embedded Unity sample";
string primarySource = SampleSource;

if (fileArg is not null)
{
    primaryName = Path.GetFileName(fileArg);
    primarySource = File.ReadAllText(fileArg);
}

Console.WriteLine("=== Phase 0: CST dump ===");
Console.WriteLine($"source: {primaryName}, {primarySource.Length} chars, {new LineMap(primarySource).LineCount} lines");

var dumpSnapshot = CsffSyntaxAdapter.Parse(primarySource);
Console.WriteLine($"nodes={dumpSnapshot.Nodes.Length} tokens={dumpSnapshot.Tokens.Length} complete={dumpSnapshot.IsComplete} recovered={dumpSnapshot.RecoveredTokens}");

Console.WriteLine("\n=== Phase 4: In-Process Micro-Benchmark (Pure Logic, No IPC) ===");
var initSnapshot = CsffSyntaxAdapter.Parse(primarySource, 1);
int changePos = primarySource.IndexOf("1", StringComparison.Ordinal);
if (changePos < 0) changePos = primarySource.Length / 2;

for (int i = 0; i < warmup; i++)
{
    var mod = IncrementalParser.ApplyChange(initSnapshot, changePos, 1, "99", 2);
    _ = SyntaxDiagnosticsProvider.GetDiagnostics(mod);
}

var didChangeSamples = new double[iterations];
for (int i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    var mod = IncrementalParser.ApplyChange(initSnapshot, changePos, 1, "99", 2);
    _ = SyntaxDiagnosticsProvider.GetDiagnostics(mod);
    sw.Stop();
    didChangeSamples[i] = sw.Elapsed.TotalMilliseconds;
}
Array.Sort(didChangeSamples);
var didChangeStats = CalcStats(didChangeSamples);

var index = new SymbolIndex();
index.UpdateFileSymbols("file:///Primary.cs", SymbolExtractor.ExtractSymbols("file:///Primary.cs", initSnapshot));
var model = new DocumentSemanticModel(initSnapshot, index);
var (cLine, cCol) = initSnapshot.Lines.GetPosition(changePos);
var pos = new Position(cLine, cCol);

for (int i = 0; i < warmup; i++)
{
    _ = CompletionProvider.GetCompletions(model, pos);
}

var completionSamples = new double[iterations];
for (int i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    _ = CompletionProvider.GetCompletions(model, pos);
    sw.Stop();
    completionSamples[i] = sw.Elapsed.TotalMilliseconds;
}
Array.Sort(completionSamples);
var completionStats = CalcStats(completionSamples);

for (int i = 0; i < warmup; i++)
{
    _ = HoverProvider.GetHover(model, pos);
}

var hoverSamples = new double[iterations];
for (int i = 0; i < iterations; i++)
{
    var sw = Stopwatch.StartNew();
    _ = HoverProvider.GetHover(model, pos);
    sw.Stop();
    hoverSamples[i] = sw.Elapsed.TotalMilliseconds;
}
Array.Sort(hoverSamples);
var hoverStats = CalcStats(hoverSamples);

Console.WriteLine($"In-Process didChange -> Diagnostics: p50={F(didChangeStats.P50),8} p95={F(didChangeStats.P95),8}");
Console.WriteLine($"In-Process Completion:              p50={F(completionStats.P50),8} p95={F(completionStats.P95),8}");
Console.WriteLine($"In-Process Hover:                   p50={F(hoverStats.P50),8} p95={F(hoverStats.P95),8}");

// E2E 計測の実行
RunE2E();

static void RunE2E()
{
    string serverPath = @"D:\wsgit\UnityCSharpFastLSP\src\UnityCSharpFastLSP.Server\bin\Release\net11.0\UnityCSharpFastLSP.Server.exe";
    string workspacePath = @"C:\Users\bluen\AppData\Local\Temp\opencode\perf-workspace";

    Console.WriteLine("\n=== End-to-End Benchmark (Real Server Process + Stdio JSON-RPC IPC) ===");
    if (!File.Exists(serverPath))
    {
        Console.WriteLine($"Server not found at {serverPath}");
        return;
    }

    var psi = new ProcessStartInfo
    {
        FileName = serverPath,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    psi.Environment["DOTNET_ROOT"] = @"C:\Users\bluen\dotnet11";

    var swTotalCold = Stopwatch.StartNew();
    using var process = Process.Start(psi)!;
    var stdin = process.StandardInput.BaseStream;
    var stdout = process.StandardOutput.BaseStream;

    // initialize 送信
    var initSw = Stopwatch.StartNew();
    Send(stdin, new
    {
        jsonrpc = "2.0",
        id = 1,
        method = "initialize",
        @params = new
        {
            rootPath = workspacePath,
            capabilities = new { }
        }
    });

    var initResp = ReadMessage(stdout);
    initSw.Stop();
    swTotalCold.Stop();

    Console.WriteLine($"Cold Start (Process Launch + initialize Response): {swTotalCold.Elapsed.TotalMilliseconds:F2} ms");
    Console.WriteLine($"initialize JSON-RPC IPC roundtrip alone:          {initSw.Elapsed.TotalMilliseconds:F2} ms");

    Send(stdin, new { jsonrpc = "2.0", method = "initialized", @params = new { } });

    string scriptPath = Path.Combine(workspacePath, "Assets", "Scripts", "PlayerController.cs");
    string sampleCode = File.Exists(scriptPath) ? File.ReadAllText(scriptPath) : SampleSource;

    var openSw = Stopwatch.StartNew();
    Send(stdin, new
    {
        jsonrpc = "2.0",
        method = "textDocument/didOpen",
        @params = new
        {
            textDocument = new
            {
                uri = "file:///Assets/Scripts/PlayerController.cs",
                languageId = "csharp",
                version = 1,
                text = sampleCode
            }
        }
    });
    var diagMsg = ReadMessage(stdout);
    openSw.Stop();
    Console.WriteLine($"textDocument/didOpen -> publishDiagnostics:        {openSw.Elapsed.TotalMilliseconds:F2} ms");

    // E2E Hover
    var hoverTimes = new List<double>();
    for (int i = 0; i < 30; i++)
    {
        var sw = Stopwatch.StartNew();
        Send(stdin, new
        {
            jsonrpc = "2.0",
            id = 200 + i,
            method = "textDocument/hover",
            @params = new
            {
                textDocument = new { uri = "file:///Assets/Scripts/PlayerController.cs" },
                position = new { line = 10, character = 10 }
            }
        });
        _ = ReadMessage(stdout);
        sw.Stop();
        if (i >= 5) hoverTimes.Add(sw.Elapsed.TotalMilliseconds);
    }
    hoverTimes.Sort();
    Console.WriteLine($"E2E IPC textDocument/hover (Warm):                 p50={hoverTimes[hoverTimes.Count / 2]:F3} ms, p95={hoverTimes[(int)(hoverTimes.Count * 0.95)]:F3} ms");

    // E2E Completion
    var compTimes = new List<double>();
    for (int i = 0; i < 30; i++)
    {
        var sw = Stopwatch.StartNew();
        Send(stdin, new
        {
            jsonrpc = "2.0",
            id = 300 + i,
            method = "textDocument/completion",
            @params = new
            {
                textDocument = new { uri = "file:///Assets/Scripts/PlayerController.cs" },
                position = new { line = 10, character = 10 }
            }
        });
        _ = ReadMessage(stdout);
        sw.Stop();
        if (i >= 5) compTimes.Add(sw.Elapsed.TotalMilliseconds);
    }
    compTimes.Sort();
    Console.WriteLine($"E2E IPC textDocument/completion (Warm):            p50={compTimes[compTimes.Count / 2]:F3} ms, p95={compTimes[(int)(compTimes.Count * 0.95)]:F3} ms");

    // E2E didChange
    var didChangeTimes = new List<double>();
    for (int i = 0; i < 30; i++)
    {
        var sw = Stopwatch.StartNew();
        Send(stdin, new
        {
            jsonrpc = "2.0",
            method = "textDocument/didChange",
            @params = new
            {
                textDocument = new { uri = "file:///Assets/Scripts/PlayerController.cs", version = 2 + i },
                contentChanges = new[]
                {
                    new
                    {
                        range = new { start = new { line = 5, character = 10 }, end = new { line = 5, character = 12 } },
                        text = "99"
                    }
                }
            }
        });
        _ = ReadMessage(stdout);
        sw.Stop();
        if (i >= 5) didChangeTimes.Add(sw.Elapsed.TotalMilliseconds);
    }
    didChangeTimes.Sort();
    Console.WriteLine($"E2E IPC textDocument/didChange -> Diagnostics:     p50={didChangeTimes[didChangeTimes.Count / 2]:F3} ms, p95={didChangeTimes[(int)(didChangeTimes.Count * 0.95)]:F3} ms");

    Send(stdin, new { jsonrpc = "2.0", id = 999, method = "shutdown" });
    _ = ReadMessage(stdout);
    Send(stdin, new { jsonrpc = "2.0", method = "exit" });
    process.WaitForExit(1000);
}

static void Send(Stream stream, object obj)
{
    byte[] body = JsonSerializer.SerializeToUtf8Bytes(obj);
    byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
    stream.Write(header);
    stream.Write(body);
    stream.Flush();
}

static string ReadMessage(Stream stream)
{
    int contentLength = 0;
    var headerBytes = new List<byte>();

    while (true)
    {
        int b = stream.ReadByte();
        if (b == -1) return "";
        headerBytes.Add((byte)b);
        int count = headerBytes.Count;
        if (count >= 4 &&
            headerBytes[count - 4] == '\r' &&
            headerBytes[count - 3] == '\n' &&
            headerBytes[count - 2] == '\r' &&
            headerBytes[count - 1] == '\n')
        {
            string headerStr = Encoding.ASCII.GetString(headerBytes.ToArray());
            foreach (var line in headerStr.Split("\r\n"))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line.Substring(15).Trim());
                }
            }
            break;
        }
    }

    byte[] buf = new byte[contentLength];
    int total = 0;
    while (total < contentLength)
    {
        int read = stream.Read(buf, total, contentLength - total);
        if (read <= 0) break;
        total += read;
    }

    return Encoding.UTF8.GetString(buf);
}

static string F(double ms) => ms < 1.0 ? $"{ms * 1000.0:F1}us" : $"{ms:F3}ms";

static Stats CalcStats(double[] samples)
{
    double sum = 0;
    foreach (double s in samples) sum += s;
    return new Stats(
        samples[0],
        samples[(int)Math.Floor(0.50 * (samples.Length - 1))],
        samples[(int)Math.Floor(0.95 * (samples.Length - 1))],
        samples[^1],
        sum / samples.Length);
}

internal readonly record struct Stats(double Min, double P50, double P95, double Max, double Avg);
