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
}
