using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/references と textDocument/documentHighlight の仕様適合を確認する。
/// 仕様:
///  - references: Location[]。context.includeDeclaration=false のとき宣言位置を含めてはならない。
///  - documentHighlight: DocumentHighlight[]（同一ドキュメント内の出現箇所）。
/// </summary>
public class ReferencesAndHighlightSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Refs.cs";

    private const string Code = """
        public class Counter
        {
            private int count;
            private int countMax;

            public void Increment()
            {
                count++;
                if (count > countMax) count = countMax;
            }

            public int Get() => count;
        }
        """;

    private static LspSession OpenSession()
    {
        var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, Code);
        return s;
    }

    private static JsonElement References(LspSession s, (int Line, int Character) p, bool includeDeclaration) =>
        s.Request("textDocument/references", new
        {
            textDocument = new { uri = Uri },
            position = new { line = p.Line, character = p.Character },
            context = new { includeDeclaration }
        });

    private static List<int> Lines(JsonElement locations) =>
        locations.EnumerateArray().Select(l => l.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32()).ToList();

    private static int DeclLine => TextPos.Of(Code, "private int count;").Line;

    // ---------- references ----------

    [Fact]
    public void References_ResultIsArrayOfLocationsInSameDocument()
    {
        using var s = OpenSession();

        var result = References(s, TextPos.Of(Code, "count++"), includeDeclaration: true);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        foreach (var loc in result.EnumerateArray())
        {
            Assert.Equal(Uri, loc.GetProperty("uri").GetString());
        }
    }

    [Fact]
    public void References_IncludeDeclarationTrue_ContainsDeclarationLine()
    {
        using var s = OpenSession();

        var lines = Lines(References(s, TextPos.Of(Code, "count++"), includeDeclaration: true));

        Assert.Contains(DeclLine, lines);
    }

    [Fact]
    public void References_IncludeDeclarationFalse_ExcludesDeclarationLine()
    {
        // 仕様: includeDeclaration=false のとき宣言は結果に含めない。
        using var s = OpenSession();

        var lines = Lines(References(s, TextPos.Of(Code, "count++"), includeDeclaration: false));

        Assert.DoesNotContain(DeclLine, lines);
    }

    [Fact]
    public void References_FindsAllUsages()
    {
        using var s = OpenSession();

        var lines = Lines(References(s, TextPos.Of(Code, "count++"), includeDeclaration: false));

        // count++ / count > / count = / => count を含む行（Get の return 含む）
        Assert.True(lines.Count >= 4, $"Expected at least 4 usages of 'count', got {lines.Count}.");
    }

    [Fact]
    public void References_MatchesWholeWordOnly()
    {
        // 仕様上 countMax は count の参照ではない。countMax の行は count の参照数に含まれない。
        using var s = OpenSession();

        var result = References(s, TextPos.Of(Code, "count++"), includeDeclaration: true);

        foreach (var loc in result.EnumerateArray())
        {
            int startChar = loc.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32();
            int endChar = loc.GetProperty("range").GetProperty("end").GetProperty("character").GetInt32();
            Assert.Equal("count".Length, endChar - startChar);
        }
    }

    [Fact]
    public void References_ContainsNoDuplicateLocations()
    {
        using var s = OpenSession();

        var result = References(s, TextPos.Of(Code, "count++"), includeDeclaration: true);

        var keys = result.EnumerateArray()
            .Select(l => l.GetRawText())
            .ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void References_OnWhitespace_ReturnsEmptyArray()
    {
        using var s = OpenSession();

        var result = References(s, TextPos.Of(Code, "\n\n", withinOffset: 1), includeDeclaration: true);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }

    // ---------- documentHighlight ----------

    private JsonElement Highlight(LspSession s, (int Line, int Character) p) =>
        s.Request("textDocument/documentHighlight", new
        {
            textDocument = new { uri = Uri },
            position = new { line = p.Line, character = p.Character }
        });

    [Fact]
    public void DocumentHighlight_ReturnsArrayWithRanges()
    {
        using var s = OpenSession();

        var result = Highlight(s, TextPos.Of(Code, "count++"));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0);
        Assert.True(result[0].TryGetProperty("range", out _));
    }

    [Fact]
    public void DocumentHighlight_HighlightsEveryOccurrenceOfWord()
    {
        // 仕様: 同じシンボルの出現箇所（count）をすべてハイライトする。countMax は含めない。
        using var s = OpenSession();

        var result = Highlight(s, TextPos.Of(Code, "count++"));

        Assert.True(result.GetArrayLength() >= 4, $"Expected >= 4 highlights, got {result.GetArrayLength()}.");
        foreach (var h in result.EnumerateArray())
        {
            var range = h.GetProperty("range");
            Assert.Equal("count".Length,
                range.GetProperty("end").GetProperty("character").GetInt32()
                - range.GetProperty("start").GetProperty("character").GetInt32());
        }
    }

    [Fact]
    public void DocumentHighlight_Kind_IsWithinSpecRange()
    {
        // 仕様: DocumentHighlightKind は Text=1, Read=2, Write=3。
        using var s = OpenSession();

        var result = Highlight(s, TextPos.Of(Code, "count++"));

        foreach (var h in result.EnumerateArray())
        {
            if (h.TryGetProperty("kind", out var kind))
            {
                Assert.InRange(kind.GetInt32(), 1, 3);
            }
        }
    }

    [Fact]
    public void DocumentHighlight_OnWhitespace_ReturnsEmptyArray()
    {
        using var s = OpenSession();

        var result = Highlight(s, TextPos.Of(Code, "\n\n", withinOffset: 1));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }
}
