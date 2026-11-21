using System.Diagnostics;
using System.Text;
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

const string IncompleteSource = """
public class Broken
{
    void M(
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

Console.WriteLine("=== UTF-16 positions (Japanese comment + emoji) ===");
int expectedOffset = SampleSource.IndexOf("public class SampleController", StringComparison.Ordinal);
var sampleSnapshot = CsffSyntaxAdapter.Parse(SampleSource);
var sampleClass = CsffSyntaxAdapter.GetDeclarations(sampleSnapshot)
    .First(d => d.Kind == "ClassDeclaration");
var (actualOffset, _) = sampleSnapshot.SourceSpan(
    Array.FindIndex(sampleSnapshot.Nodes, n => n.Kind == SampleClassKind.ClassDeclaration));
var (positionLine, positionCharacter) = sampleSnapshot.Lines.GetPosition(expectedOffset);
int roundTrip = sampleSnapshot.Lines.GetOffset(positionLine, positionCharacter);
Console.WriteLine($"expected offset={expectedOffset}, match={roundTrip == expectedOffset && actualOffset == expectedOffset}");

Console.WriteLine("=== Incomplete source ===");
var broken = CsffSyntaxAdapter.Parse(IncompleteSource);
int incomplete = CsffSyntaxAdapter.CountIncompleteMembers(broken);
int badTokens = CsffSyntaxAdapter.CountBadTokens(broken);
Console.WriteLine($"complete={broken.IsComplete} recovered={broken.RecoveredTokens} IncompleteMember={incomplete} BadToken={badTokens}");

Console.WriteLine("\n=== Phase 4: Per-Request Gate Benchmark (Warm, 10x Target) ===");
Console.WriteLine($"warmup={warmup} iterations={iterations}");

// 1. didChange (Incremental Reparse + Syntax Diagnostics)
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

// 2. Completion Benchmark
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

// 3. Hover Benchmark
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

// 比較評価
const double BaselineCompletionMedianMs = 13.79;   // 2026-11-21 実測値
const double BaselineDiagnosticsMedianMs = 3.06;   // 2026-11-21 実測値
const double BaselineHoverMedianMs = 0.73;         // 2026-11-21 実測値

double targetCompletion = BaselineCompletionMedianMs / 10.0;
double targetDiagnostics = BaselineDiagnosticsMedianMs / 10.0;
double targetHover = BaselineHoverMedianMs / 10.0;

Console.WriteLine("\n--- Benchmark Results ---");
Console.WriteLine($"1. didChange -> Diagnostics: p50={F(didChangeStats.P50),8} p95={F(didChangeStats.P95),8} (Baseline: {F(BaselineDiagnosticsMedianMs)}, Target: {F(targetDiagnostics)}) -> Pass: {didChangeStats.P50 <= targetDiagnostics}");
Console.WriteLine($"2. Completion:              p50={F(completionStats.P50),8} p95={F(completionStats.P95),8} (Baseline: {F(BaselineCompletionMedianMs)}, Target: {F(targetCompletion)}) -> Pass: {completionStats.P50 <= targetCompletion}");
Console.WriteLine($"3. Hover:                   p50={F(hoverStats.P50),8} p95={F(hoverStats.P95),8} (Baseline: {F(BaselineHoverMedianMs)}, Target: {F(targetHover)}) -> Pass: {hoverStats.P50 <= targetHover}");

bool allPassed = didChangeStats.P50 <= targetDiagnostics &&
                 completionStats.P50 <= targetCompletion &&
                 hoverStats.P50 <= targetHover;

Console.WriteLine($"\n=== 10x Speedup Verification: {(allPassed ? "PASSED (10x GOAL ACHIEVED)" : "FAILED")} ===");

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

internal static class SampleClassKind
{
    public const int ClassDeclaration = 8855;
}
