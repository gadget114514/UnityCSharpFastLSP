using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/formatting, signatureHelp, semanticTokens/full の仕様適合を確認する。
/// </summary>
public class FormattingSignatureAndTokensSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Format.cs";

    private const string Messy = "public class A{\nint x;\n   void M( ){ }\n}\n";

    private static LspSession OpenSession(string code)
    {
        var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, code);
        return s;
    }

    private static JsonElement Format(LspSession s) =>
        s.Request("textDocument/formatting", new
        {
            textDocument = new { uri = Uri },
            options = new { tabSize = 4, insertSpaces = true }
        });

    private static string NonWhitespace(string text) =>
        new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    // ---------- formatting ----------

    [Fact]
    public void Formatting_ResultIsTextEditArray()
    {
        using var s = OpenSession(Messy);

        var result = Format(s);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
    }

    [Fact]
    public void Formatting_EditsHaveRangeAndNewText()
    {
        using var s = OpenSession(Messy);

        foreach (var edit in Format(s).EnumerateArray())
        {
            Assert.True(edit.TryGetProperty("range", out _));
            Assert.Equal(JsonValueKind.String, edit.GetProperty("newText").ValueKind);
        }
    }

    [Fact]
    public void Formatting_OnlyChangesWhitespace()
    {
        // 仕様（実務上の期待）: フォーマットは空白だけを変更し、トークンは変えない。
        using var s = OpenSession(Messy);

        var edits = Format(s).EnumerateArray().ToList();
        string formatted = TextEditApplier.Apply(Messy, edits);

        Assert.Equal(NonWhitespace(Messy), NonWhitespace(formatted));
    }

    [Fact]
    public void Formatting_IsIdempotent()
    {
        // 仕様（実務上の期待）: 整形を2回適用しても文書は変わらない。
        using var s = OpenSession(Messy);
        string once = TextEditApplier.Apply(Messy, Format(s).EnumerateArray());

        using var s2 = OpenSession(once);
        string twice = TextEditApplier.Apply(once, Format(s2).EnumerateArray());

        Assert.Equal(once.Replace("\r\n", "\n"), twice.Replace("\r\n", "\n"));
    }

    [Fact]
    public void Formatting_EditRange_IsWithinDocument()
    {
        // 仕様: TextEdit.range は文書内の位置を指すこと（存在しない行を指してはならない）。
        // 現状: 末尾が改行で終わる文書で、範囲の終端が「文書の最終行の次の行」を指す。
        using var s = OpenSession(Messy);
        int lineCount = Messy.Split('\n').Length; // 末尾改行なら最後は空行として数える

        foreach (var edit in Format(s).EnumerateArray())
        {
            int endLine = edit.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32();
            Assert.True(endLine < lineCount,
                $"Edit end line {endLine} is outside the document (line count {lineCount}).");
        }
    }

    [Fact]
    public void Formatting_PreservesLineEndingStyle_OfLfDocument()
    {
        // 期待: LF の文書を整形しても改行コードを CRLF に変えない（プラットフォーム依存にしない）。
        using var s = OpenSession(Messy);

        foreach (var edit in Format(s).EnumerateArray())
        {
            string newText = edit.GetProperty("newText").GetString()!;
            Assert.DoesNotContain("\r\n", newText);
        }
    }

    [Fact]
    public void Formatting_UsesRequestedIndentation()
    {
        using var s = OpenSession(Messy);

        string formatted = TextEditApplier.Apply(Messy, Format(s).EnumerateArray());

        Assert.Contains("\n    int x;", formatted);
    }

    [Fact]
    public void Formatting_UnknownDocument_ReturnsEmptyArray()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = s.Request("textDocument/formatting", new
        {
            textDocument = new { uri = "file:///Assets/Nope.cs" },
            options = new { tabSize = 4, insertSpaces = true }
        });

        Assert.Equal(0, result.GetArrayLength());
    }

    // ---------- signatureHelp ----------

    private const string SigCode = """
        public class Calc
        {
            public void Heal(int amount) {}
            public int Add(int a, int b) => a + b;

            public void Run()
            {
                Heal(20);
                Add(1, 2);
            }
        }
        """;

    private static JsonElement SignatureHelp(LspSession s, (int Line, int Character) p) =>
        s.Request("textDocument/signatureHelp", new
        {
            textDocument = new { uri = Uri },
            position = new { line = p.Line, character = p.Character }
        });

    [Fact]
    public void SignatureHelp_InsideCall_ReturnsSignatures()
    {
        using var s = OpenSession(SigCode);

        var result = SignatureHelp(s, TextPos.Of(SigCode, "Heal(20)", withinOffset: "Heal(".Length));

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        var sigs = result.GetProperty("signatures");
        Assert.True(sigs.GetArrayLength() > 0);
        Assert.Contains("Heal", sigs[0].GetProperty("label").GetString()!);
    }

    [Fact]
    public void SignatureHelp_ParameterListsAreExposed()
    {
        using var s = OpenSession(SigCode);

        var result = SignatureHelp(s, TextPos.Of(SigCode, "Add(1, 2)", withinOffset: "Add(".Length));

        var sig = result.GetProperty("signatures")[0];
        Assert.True(sig.TryGetProperty("parameters", out var parameters));
        Assert.Equal(2, parameters.GetArrayLength());
    }

    [Fact]
    public void SignatureHelp_ActiveParameter_TracksComma()
    {
        // 仕様: カンマの後ろにカーソルがあるとき activeParameter は次の引数（1）。
        using var s = OpenSession(SigCode);

        var result = SignatureHelp(s, TextPos.Of(SigCode, "Add(1, 2)", withinOffset: "Add(1, ".Length));

        Assert.Equal(1, result.GetProperty("activeParameter").GetInt32());
    }

    [Fact]
    public void SignatureHelp_OutsideCall_ReturnsNull()
    {
        using var s = OpenSession(SigCode);

        var result = SignatureHelp(s, TextPos.Of(SigCode, "public class Calc"));

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    // ---------- semanticTokens/full ----------

    private const string TokenCode = """
        public class Player
        {
            public int Health;
            public void TakeDamage(int amount)
            {
                Health -= amount;
            }
        }
        """;

    private static JsonElement SemanticTokens(LspSession s) =>
        s.Request("textDocument/semanticTokens/full", new { textDocument = new { uri = Uri } });

    private static List<int> TokenData(LspSession s) =>
        SemanticTokens(s).GetProperty("data").EnumerateArray().Select(e => e.GetInt32()).ToList();

    [Fact]
    public void SemanticTokens_DataLengthIsMultipleOfFive()
    {
        // 仕様: data は (deltaLine, deltaStart, length, tokenType, tokenModifiers) の 5 つ組。
        using var s = OpenSession(TokenCode);

        var data = TokenData(s);

        Assert.True(data.Count > 0);
        Assert.Equal(0, data.Count % 5);
    }

    [Fact]
    public void SemanticTokens_DeltaLineIsNonNegative()
    {
        using var s = OpenSession(TokenCode);
        var data = TokenData(s);

        for (int i = 0; i < data.Count; i += 5)
        {
            Assert.True(data[i] >= 0, $"deltaLine at token {i / 5} is negative.");
        }
    }

    [Fact]
    public void SemanticTokens_DeltaStartIsNonNegativeOnSameLine()
    {
        using var s = OpenSession(TokenCode);
        var data = TokenData(s);

        for (int i = 0; i < data.Count; i += 5)
        {
            if (data[i] == 0 && i > 0)
            {
                Assert.True(data[i + 1] >= 0, $"deltaStart on same line at token {i / 5} is negative.");
            }
        }
    }

    [Fact]
    public void SemanticTokens_LengthsArePositive()
    {
        using var s = OpenSession(TokenCode);
        var data = TokenData(s);

        for (int i = 0; i < data.Count; i += 5)
        {
            Assert.True(data[i + 2] > 0, $"Token {i / 5} has non-positive length.");
        }
    }

    [Fact]
    public void SemanticTokens_TokenTypeIndexIsWithinLegend()
    {
        using var init = new LspSession();
        int legendSize = init.Initialize()
            .GetProperty("capabilities").GetProperty("semanticTokensProvider")
            .GetProperty("legend").GetProperty("tokenTypes").GetArrayLength();

        using var s = OpenSession(TokenCode);
        var data = TokenData(s);

        for (int i = 0; i < data.Count; i += 5)
        {
            Assert.InRange(data[i + 3], 0, legendSize - 1);
        }
    }

    [Fact]
    public void SemanticTokens_UnknownDocument_ReturnsEmptyData()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = s.Request("textDocument/semanticTokens/full", new
        {
            textDocument = new { uri = "file:///Assets/Nope.cs" }
        });

        Assert.Equal(0, result.GetProperty("data").GetArrayLength());
    }
}
