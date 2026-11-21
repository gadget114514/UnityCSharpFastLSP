using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/didOpen, didChange, didClose と publishDiagnostics の仕様適合を確認する。
/// </summary>
public class TextDocumentSyncSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Sync.cs";

    private static List<string> SymbolNames(LspSession s, string uri)
    {
        var result = s.Request("textDocument/documentSymbol", new { textDocument = new { uri } });
        var names = new List<string>();
        CollectNames(result, names);
        return names;
    }

    private static void CollectNames(JsonElement symbols, List<string> names)
    {
        foreach (var sym in symbols.EnumerateArray())
        {
            names.Add(sym.GetProperty("name").GetString()!);
            if (sym.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                CollectNames(children, names);
            }
        }
    }

    [Fact]
    public void DidOpen_MakesDocumentAvailableToRequests()
    {
        using var s = new LspSession();
        s.Initialize();

        s.OpenDocument(Uri, "public class Player {}");

        Assert.Contains("Player", SymbolNames(s, Uri));
    }

    [Fact]
    public void DidChange_FullTextReplacement_UpdatesDocument()
    {
        // 仕様: range を省略した contentChanges は全文置換。
        using var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, "public class OldName {}");

        s.Notify("textDocument/didChange", new
        {
            textDocument = new { uri = Uri, version = 2 },
            contentChanges = new[] { new { text = "public class NewName {}" } }
        });

        var names = SymbolNames(s, Uri);
        Assert.Contains("NewName", names);
        Assert.DoesNotContain("OldName", names);
    }

    [Fact]
    public void DidChange_IncrementalInsert_UpdatesDocument()
    {
        // 仕様: range 付きの contentChanges は差分適用。挿入（空の range）。
        using var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, "public class A\n{\n}\n");

        s.Notify("textDocument/didChange", new
        {
            textDocument = new { uri = Uri, version = 2 },
            contentChanges = new[]
            {
                new { range = new { start = new { line = 1, character = 0 }, end = new { line = 1, character = 0 } }, text = "    public void Foo() {}\n" }
            }
        });

        Assert.Contains("Foo", SymbolNames(s, Uri));
    }

    [Fact]
    public void DidChange_IncrementalReplace_UpdatesDocument()
    {
        // 仕様: range の範囲を text で置換する。"A" (0,13)-(0,14) を "Renamed" に。
        using var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, "public class A {}");

        s.Notify("textDocument/didChange", new
        {
            textDocument = new { uri = Uri, version = 2 },
            contentChanges = new[]
            {
                new { range = new { start = new { line = 0, character = 13 }, end = new { line = 0, character = 14 } }, text = "Renamed" }
            }
        });

        var names = SymbolNames(s, Uri);
        Assert.Contains("Renamed", names);
        Assert.DoesNotContain("A", names);
    }

    [Fact]
    public void DidChange_ForUnopenedDocument_IsIgnoredWithoutError()
    {
        using var s = new LspSession();
        s.Initialize();

        var ex = Record.Exception(() => s.Notify("textDocument/didChange", new
        {
            textDocument = new { uri = "file:///Assets/NotOpened.cs", version = 2 },
            contentChanges = new[] { new { text = "class X {}" } }
        }));

        Assert.Null(ex);
    }

    [Fact]
    public void DidClose_RemovesDocumentFromServerMemory()
    {
        // 仕様: didClose 後はクライアントの内容は破棄され、ディスク上に無ければ結果は空になる。
        const string closedUri = "file:///nonexistent-dir/Closed.cs";
        using var s = new LspSession();
        s.Initialize();
        s.OpenDocument(closedUri, "public class Gone {}");
        Assert.Contains("Gone", SymbolNames(s, closedUri));

        s.Notify("textDocument/didClose", new { textDocument = new { uri = closedUri } });

        Assert.Empty(SymbolNames(s, closedUri));
    }

    [Fact]
    public void DidClose_PublishesEmptyDiagnosticsToClearClientState()
    {
        using var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, "class A {}");

        s.Notify("textDocument/didClose", new { textDocument = new { uri = Uri } });

        var note = s.WaitForNotification("textDocument/publishDiagnostics", Uri, TimeSpan.FromSeconds(2));
        Assert.NotNull(note);
        Assert.Equal(0, note!.Value.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void PublishDiagnostics_CarriesDocumentVersion()
    {
        // 仕様: publishDiagnostics の version はそのドキュメントの最新バージョンと一致する。
        using var s = new LspSession();
        s.Initialize();

        s.OpenDocument(Uri, "class A {}", version: 7);

        var note = s.WaitForNotification("textDocument/publishDiagnostics", Uri, TimeSpan.FromSeconds(2));
        Assert.NotNull(note);
        Assert.Equal(7, note!.Value.GetProperty("params").GetProperty("version").GetInt32());
    }

    [Fact]
    public void PublishDiagnostics_ValidCode_ReportsNoErrors()
    {
        using var s = new LspSession();
        s.Initialize();

        s.OpenDocument(Uri, "public class A\n{\n    public int X;\n}\n");

        var note = s.WaitForNotification("textDocument/publishDiagnostics", Uri, TimeSpan.FromSeconds(2));
        Assert.NotNull(note);
        Assert.Equal(0, note!.Value.GetProperty("params").GetProperty("diagnostics").GetArrayLength());
    }

    [Fact]
    public void PublishDiagnostics_SyntaxError_ReportsErrorSeverity()
    {
        // 仕様: 構文エラーは Diagnostic.severity = 1 (Error) として報告される。
        using var s = new LspSession();
        s.Initialize();

        s.OpenDocument(Uri, "public class A\n{\n    void M( {\n}\n");

        var note = s.WaitForNotification("textDocument/publishDiagnostics", Uri, TimeSpan.FromSeconds(2));
        Assert.NotNull(note);
        var diags = note!.Value.GetProperty("params").GetProperty("diagnostics");
        Assert.True(diags.GetArrayLength() > 0, "Syntax error must produce at least one diagnostic.");
        Assert.Equal(1, diags[0].GetProperty("severity").GetInt32());
    }

    [Fact]
    public void PublishDiagnostics_Diagnostic_HasRangeAndMessage()
    {
        using var s = new LspSession();
        s.Initialize();

        s.OpenDocument(Uri, "public class A\n{\n    void M( {\n}\n");

        var note = s.WaitForNotification("textDocument/publishDiagnostics", Uri, TimeSpan.FromSeconds(2));
        Assert.NotNull(note);
        var diag = note!.Value.GetProperty("params").GetProperty("diagnostics")[0];
        Assert.True(diag.TryGetProperty("range", out var range));
        Assert.True(range.TryGetProperty("start", out _) && range.TryGetProperty("end", out _));
        Assert.False(string.IsNullOrEmpty(diag.GetProperty("message").GetString()));
    }
}
