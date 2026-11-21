using System.Text;
using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Server;
using UnityCSharpFastLSP.Syntax;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

public class Phase1To3IntegrationTests
{
    private const string SampleCode = """
        using UnityEngine;

        namespace Game.Runtime
        {
            public class Player : MonoBehaviour
            {
                [SerializeField] private int _health = 100;

                private void Start()
                {
                    _health = 50;
                }

                public int Health => _health;
            }
        }
        """;

    [Fact]
    public void IncrementalParserUpdatesOffsetsCorrectly()
    {
        var initial = CsffSyntaxAdapter.Parse(SampleCode, version: 1);
        int targetOffset = SampleCode.IndexOf("50", StringComparison.Ordinal);

        // "50" を "12345" に変更
        var modified = IncrementalParser.ApplyChange(
            initial,
            changeStart: targetOffset,
            changeLength: 2,
            newText: "12345",
            newVersion: 2
        );

        Assert.Equal(2, modified.Version);
        Assert.Contains("12345", modified.Source);
        Assert.True(modified.Tokens.Length > 0);

        // 全文再パース結果とトークン数が一致すること
        var fullReparsed = CsffSyntaxAdapter.Parse(modified.Source, version: 2);
        Assert.Equal(fullReparsed.Tokens.Length, modified.Tokens.Length);
    }

    [Fact]
    public void DocumentSymbolsHierarchyIsConstructed()
    {
        var snapshot = CsffSyntaxAdapter.Parse(SampleCode);
        var symbols = DocumentSymbolProvider.GetDocumentSymbols(snapshot);

        Assert.NotEmpty(symbols);
        var ns = Assert.Single(symbols, s => s.Name == "Game");
        Assert.NotNull(ns.Children);

        var cls = Assert.Single(ns.Children!, c => c.Name == "Player");
        Assert.NotNull(cls.Children);
        Assert.Contains(cls.Children!, m => m.Name == "Start" && m.Kind == SymbolKind.Method);
        Assert.Contains(cls.Children!, f => f.Name == "_health" && f.Kind == SymbolKind.Field);
        Assert.Contains(cls.Children!, p => p.Name == "Health" && p.Kind == SymbolKind.Property);
    }

    [Fact]
    public void SyntaxDiagnosticsEmitsForIncompleteCode()
    {
        var snapshot = CsffSyntaxAdapter.Parse("public class Broken { void M(");
        var diagnostics = SyntaxDiagnosticsProvider.GetDiagnostics(snapshot);

        Assert.NotEmpty(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostics[0].Severity);
    }

    [Fact]
    public void SymbolIndexSupportsDifferentialUpdates()
    {
        var index = new SymbolIndex();
        string uri = "file:///Dummy.cs";

        var snap1 = CsffSyntaxAdapter.Parse("public class A { void M1() {} }");
        index.UpdateFileSymbols(uri, SymbolExtractor.ExtractSymbols(uri, snap1));

        Assert.NotEmpty(index.FindDefinitions("M1"));

        // ファイル更新 (M1 -> M2)
        var snap2 = CsffSyntaxAdapter.Parse("public class A { void M2() {} }");
        index.UpdateFileSymbols(uri, SymbolExtractor.ExtractSymbols(uri, snap2));

        Assert.Empty(index.FindDefinitions("M1"));
        Assert.NotEmpty(index.FindDefinitions("M2"));
    }

    [Fact]
    public void HoverAndCompletionIncludeUnityContext()
    {
        var snapshot = CsffSyntaxAdapter.Parse(SampleCode);
        var index = new SymbolIndex();
        string uri = "file:///Player.cs";
        index.UpdateFileSymbols(uri, SymbolExtractor.ExtractSymbols(uri, snapshot));

        var model = new DocumentSemanticModel(snapshot, index);

        // Hover for Start (Unity Lifecycle)
        int startOffset = SampleCode.IndexOf("Start", StringComparison.Ordinal);
        var (line, col) = snapshot.Lines.GetPosition(startOffset);
        var hover = HoverProvider.GetHover(model, new Position(line, col));
        Assert.NotNull(hover);
        Assert.Contains("Start", hover.Contents.Value);

        // Completion
        var completions = CompletionProvider.GetCompletions(model, new Position(line, col));
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, c => c.Label == "Update");
        Assert.Contains(completions.Items, c => c.Label == "SerializeField");
    }

    [Fact]
    public void ServerHandlesLspLifecycleAndRequests()
    {
        using var outputStream = new MemoryStream();
        var server = new LspServer(outputStream);

        // 1. initialize
        string initRequest = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{}}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(initRequest)));

        // 2. didOpen
        string didOpen = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method = "textDocument/didOpen",
            @params = new
            {
                textDocument = new
                {
                    uri = "file:///Test.cs",
                    languageId = "csharp",
                    version = 1,
                    text = SampleCode
                }
            }
        });
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(didOpen)));

        // 3. documentSymbol
        string docSymbolReq = """
            {"jsonrpc":"2.0","id":2,"method":"textDocument/documentSymbol","params":{"textDocument":{"uri":"file:///Test.cs"}}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(docSymbolReq)));

        // 4. hover
        string hoverReq = """
            {"jsonrpc":"2.0","id":3,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///Test.cs"},"position":{"line":8,"character":22}}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(hoverReq)));

        // 5. completion
        string completionReq = """
            {"jsonrpc":"2.0","id":4,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///Test.cs"},"position":{"line":8,"character":22}}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(completionReq)));

        // 6. formatting
        string formatReq = """
            {"jsonrpc":"2.0","id":5,"method":"textDocument/formatting","params":{"textDocument":{"uri":"file:///Test.cs"},"options":{"tabSize":4,"insertSpaces":true}}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(formatReq)));

        // 7. didChange (incremental)
        string didChange = """
            {"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///Test.cs","version":2},"contentChanges":[{"range":{"start":{"line":10,"character":20},"end":{"line":10,"character":22}},"text":"999"}]}}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(didChange)));

        // 8. shutdown
        string shutdownReq = """
            {"jsonrpc":"2.0","id":6,"method":"shutdown"}
            """;
        Assert.True(server.HandleMessage(Encoding.UTF8.GetBytes(shutdownReq)));

        // 9. exit
        string exitNotif = """
            {"jsonrpc":"2.0","method":"exit"}
            """;
        Assert.False(server.HandleMessage(Encoding.UTF8.GetBytes(exitNotif)));

        // 出力ストリームにレスポンスが書き込まれていること
        Assert.True(outputStream.Length > 0);
    }

    [Fact]
    public void DotMemberCompletionFiltersByTypeForVarNew()
    {
        const string code = """
            public class Enemy
            {
                public int Hp;
                public void TakeDamage(int dmg) {}
            }

            public class GameController
            {
                void Update()
                {
                    var enemy = new Enemy();
                    enemy.
                }
            }
            """;
        var snapshot = CsffSyntaxAdapter.Parse(code);
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Test.cs", SymbolExtractor.ExtractSymbols("file:///Test.cs", snapshot));
        var model = new DocumentSemanticModel(snapshot, index);

        int dotOffset = code.IndexOf("enemy.", StringComparison.Ordinal) + "enemy.".Length;
        var (line, col) = snapshot.Lines.GetPosition(dotOffset);

        var completions = CompletionProvider.GetCompletions(model, new Position(line, col));
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, c => c.Label == "Hp");
        Assert.Contains(completions.Items, c => c.Label == "TakeDamage");
        // Unrelated symbols or methods must NOT be present
        Assert.DoesNotContain(completions.Items, c => c.Label == "Update");
        Assert.DoesNotContain(completions.Items, c => c.Label == "Awake");
    }

    [Fact]
    public void DotMemberCompletionSupportsThisAndExplicitType()
    {
        const string code = """
            public class Player
            {
                public int Health;
                public void Attack() {}

                void InternalMethod()
                {
                    this.
                }

                void OtherMethod(Player target)
                {
                    target.
                }
            }
            """;
        var snapshot = CsffSyntaxAdapter.Parse(code);
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Player.cs", SymbolExtractor.ExtractSymbols("file:///Player.cs", snapshot));
        var model = new DocumentSemanticModel(snapshot, index);

        // Test this.
        int thisOffset = code.IndexOf("this.", StringComparison.Ordinal) + "this.".Length;
        var (tLine, tCol) = snapshot.Lines.GetPosition(thisOffset);
        var thisCompletions = CompletionProvider.GetCompletions(model, new Position(tLine, tCol));
        Assert.NotNull(thisCompletions);
        Assert.Contains(thisCompletions.Items, c => c.Label == "Health");
        Assert.Contains(thisCompletions.Items, c => c.Label == "Attack");

        // Test parameter target.
        int targetOffset = code.IndexOf("target.", StringComparison.Ordinal) + "target.".Length;
        var (pLine, pCol) = snapshot.Lines.GetPosition(targetOffset);
        var targetCompletions = CompletionProvider.GetCompletions(model, new Position(pLine, pCol));
        Assert.NotNull(targetCompletions);
        Assert.Contains(targetCompletions.Items, c => c.Label == "Health");
        Assert.Contains(targetCompletions.Items, c => c.Label == "Attack");
    }

    [Fact]
    public void CrossFileMemberCompletionViaSymbolIndex()
    {
        var enemySnapshot = CsffSyntaxAdapter.Parse("public class Boss { public int Shield; public void Roar() {} }");
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Boss.cs", SymbolExtractor.ExtractSymbols("file:///Boss.cs", enemySnapshot));

        const string usageCode = """
            public class Battle
            {
                private Boss _boss;
                void Fight()
                {
                    _boss.
                }
            }
            """;
        var usageSnapshot = CsffSyntaxAdapter.Parse(usageCode);
        var model = new DocumentSemanticModel(usageSnapshot, index);

        int offset = usageCode.IndexOf("_boss.", StringComparison.Ordinal) + "_boss.".Length;
        var (line, col) = usageSnapshot.Lines.GetPosition(offset);

        var completions = CompletionProvider.GetCompletions(model, new Position(line, col));
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, c => c.Label == "Shield");
        Assert.Contains(completions.Items, c => c.Label == "Roar");
        Assert.DoesNotContain(completions.Items, c => c.Label == "Fight");
    }

    [Fact]
    public void DefinitionJumpResolvesLocalAndCrossFileTargets()
    {
        var bossCode = "public class Boss { public void Roar() {} }";
        var bossSnap = CsffSyntaxAdapter.Parse(bossCode);
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Boss.cs", SymbolExtractor.ExtractSymbols("file:///Boss.cs", bossSnap));

        const string code = """
            public class Arena
            {
                void Start()
                {
                    var boss = new Boss();
                    boss.Roar();
                }
            }
            """;
        var snap = CsffSyntaxAdapter.Parse(code);
        index.UpdateFileSymbols("file:///Arena.cs", SymbolExtractor.ExtractSymbols("file:///Arena.cs", snap));
        var model = new DocumentSemanticModel(snap, index);

        // 1. Definition jump on local var 'boss' in boss.Roar()
        int bossUsageOffset = code.IndexOf("boss.Roar", StringComparison.Ordinal);
        var (bLine, bCol) = snap.Lines.GetPosition(bossUsageOffset);
        var bossDefs = ScopedResolver.ResolveScopedDefinition(model, "file:///Arena.cs", new Position(bLine, bCol));
        Assert.NotEmpty(bossDefs);
        // Must point to 'var boss = new Boss()' declaration line
        int bossDeclOffset = code.IndexOf("boss = new", StringComparison.Ordinal);
        var (declLine, declCol) = snap.Lines.GetPosition(bossDeclOffset);
        Assert.Equal(declLine, bossDefs[0].Range.Start.Line);

        // 2. Definition jump on 'Roar' in boss.Roar()
        int roarUsageOffset = code.IndexOf("Roar", StringComparison.Ordinal);
        var (rLine, rCol) = snap.Lines.GetPosition(roarUsageOffset);
        var roarDefs = ScopedResolver.ResolveScopedDefinition(model, "file:///Arena.cs", new Position(rLine, rCol));
        Assert.NotEmpty(roarDefs);
        Assert.Equal("file:///Boss.cs", roarDefs[0].Uri);
    }

    [Fact]
    public void InheritanceHierarchyMergesBaseMembers()
    {
        const string baseCode = """
            public class Character
            {
                public int MaxHp;
                public void Heal() {}
            }
            """;
        var baseSnap = CsffSyntaxAdapter.Parse(baseCode);
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Character.cs", SymbolExtractor.ExtractSymbols("file:///Character.cs", baseSnap));

        const string playerCode = """
            public class Hero : Character
            {
                public int Level;
            }
            """;
        var playerSnap = CsffSyntaxAdapter.Parse(playerCode);
        index.UpdateFileSymbols("file:///Hero.cs", SymbolExtractor.ExtractSymbols("file:///Hero.cs", playerSnap));

        const string testCode = """
            public class Main
            {
                void Run()
                {
                    Hero hero = new Hero();
                    hero.
                }
            }
            """;
        var testSnap = CsffSyntaxAdapter.Parse(testCode);
        var model = new DocumentSemanticModel(testSnap, index);

        int dotOffset = testCode.IndexOf("hero.", StringComparison.Ordinal) + "hero.".Length;
        var (line, col) = testSnap.Lines.GetPosition(dotOffset);

        var completions = CompletionProvider.GetCompletions(model, new Position(line, col));
        Assert.NotNull(completions);
        // Derived member
        Assert.Contains(completions.Items, c => c.Label == "Level");
        // Base class members
        Assert.Contains(completions.Items, c => c.Label == "MaxHp");
        Assert.Contains(completions.Items, c => c.Label == "Heal");
    }

    [Fact]
    public void UnityApiDictionaryProvidesInstantMembers()
    {
        const string code = """
            public class TestUnity
            {
                void Update()
                {
                    transform.
                }
            }
            """;
        var snap = CsffSyntaxAdapter.Parse(code);
        var index = new SymbolIndex();
        var model = new DocumentSemanticModel(snap, index);

        int dotOffset = code.IndexOf("transform.", StringComparison.Ordinal) + "transform.".Length;
        var (line, col) = snap.Lines.GetPosition(dotOffset);

        var completions = CompletionProvider.GetCompletions(model, new Position(line, col));
        Assert.NotNull(completions);
        Assert.Contains(completions.Items, c => c.Label == "position");
        Assert.Contains(completions.Items, c => c.Label == "Translate");
        Assert.Contains(completions.Items, c => c.Label == "LookAt");
    }

    [Fact]
    public void SignatureHelpReturnsMethodParameters()
    {
        const string code = """
            public class Calc
            {
                public void Add(int a, int b) {}

                void Test()
                {
                    Add(10, 
                }
            }
            """;
        var snap = CsffSyntaxAdapter.Parse(code);
        var index = new SymbolIndex();
        index.UpdateFileSymbols("file:///Calc.cs", SymbolExtractor.ExtractSymbols("file:///Calc.cs", snap));
        var model = new DocumentSemanticModel(snap, index);

        int offset = code.IndexOf("Add(10,", StringComparison.Ordinal) + "Add(10,".Length;
        var (line, col) = snap.Lines.GetPosition(offset);

        var help = SignatureHelpProvider.GetSignatureHelp(model, new Position(line, col));
        Assert.NotNull(help);
        Assert.NotEmpty(help.Signatures);
        Assert.Equal(1, help.ActiveParameter); // Second parameter after comma
    }

    [Fact]
    public void SemanticTokensReturnsEncodedTokens()
    {
        var snap = CsffSyntaxAdapter.Parse("public class MyClass { int _val = 42; }");
        var tokens = SemanticTokensProvider.GetSemanticTokens(snap);

        Assert.NotNull(tokens);
        Assert.NotEmpty(tokens.Data);
        Assert.True(tokens.Data.Count % 5 == 0); // 5 integers per token
    }

    [Fact]
    public void DocumentHighlightReturnsAllOccurrences()
    {
        const string code = "public class A { int x = 1; void M() { x = 2; } }";
        var snap = CsffSyntaxAdapter.Parse(code);
        var index = new SymbolIndex();
        var model = new DocumentSemanticModel(snap, index);

        int offset = code.IndexOf("x = 1", StringComparison.Ordinal);
        var (line, col) = snap.Lines.GetPosition(offset);

        var highlights = DocumentHighlightProvider.GetHighlights(model, new Position(line, col));
        Assert.Equal(2, highlights.Count);
    }
}
