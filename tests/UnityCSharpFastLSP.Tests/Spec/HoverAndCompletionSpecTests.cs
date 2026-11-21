using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/hover と textDocument/completion の仕様適合を確認する。
/// 仕様: Hover は { contents, range? } または null。
/// CompletionList は { isIncomplete, items[] } で、各 item は label を持つ。
/// </summary>
public class HoverAndCompletionSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Hover.cs";

    private const string Code = """
        public class Player
        {
            public int Health;
            public void TakeDamage(int amount) {}

            public void Update()
            {
                var player = new Player();
                player.TakeDamage(10);
                int hp = player.Health;
            }
        }
        """;

    private static LspSession OpenSession()
    {
        var s = new LspSession();
        s.Initialize();
        s.OpenDocument(Uri, Code);
        return s;
    }

    private static object Params(object position) => new { textDocument = new { uri = Uri }, position };

    // ---------- hover ----------

    [Fact]
    public void Hover_OnKnownSymbol_ReturnsMarkupContents()
    {
        using var s = OpenSession();

        var result = s.Request("textDocument/hover", Params(TextPos.At(Code, "TakeDamage(int")));

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.TryGetProperty("contents", out var contents));
        Assert.Equal(JsonValueKind.Object, contents.ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(contents.GetProperty("value").GetString()));
    }

    [Fact]
    public void Hover_MarkupKind_IsMarkdownOrPlainText()
    {
        using var s = OpenSession();

        var contents = s.Request("textDocument/hover", Params(TextPos.At(Code, "TakeDamage(int")))
                        .GetProperty("contents");

        var kind = contents.GetProperty("kind").GetString();
        Assert.True(kind is "markdown" or "plaintext", $"Unexpected MarkupKind '{kind}'.");
    }

    [Fact]
    public void Hover_RangeCoversHoveredWord_WhenPresent()
    {
        using var s = OpenSession();

        var result = s.Request("textDocument/hover", Params(TextPos.At(Code, "TakeDamage(int")));

        if (result.TryGetProperty("range", out var range))
        {
            int width = range.GetProperty("end").GetProperty("character").GetInt32()
                      - range.GetProperty("start").GetProperty("character").GetInt32();
            Assert.Equal("TakeDamage".Length, width);
        }
    }

    [Fact]
    public void Hover_OnWhitespace_ReturnsNull()
    {
        using var s = OpenSession();

        // "public int Health" 行の行頭（インデントの空白上）
        var result = s.Request("textDocument/hover", Params(TextPos.At(Code, "\n    public int Health", withinOffset: 1)));

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    [Fact]
    public void Hover_OnUnknownDocument_ReturnsNull()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = s.Request("textDocument/hover", new
        {
            textDocument = new { uri = "file:///Assets/Missing.cs" },
            position = new { line = 0, character = 0 }
        });

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
    }

    // ---------- completion ----------

    [Fact]
    public void Completion_ResultIsCompletionList()
    {
        using var s = OpenSession();

        var result = s.Request("textDocument/completion", Params(TextPos.At(Code, "player.TakeDamage")));

        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.True(result.GetProperty("isIncomplete").ValueKind is JsonValueKind.True or JsonValueKind.False);
        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
    }

    [Fact]
    public void Completion_Items_HaveLabelAndValidKind()
    {
        using var s = OpenSession();

        var items = s.Request("textDocument/completion", Params(TextPos.At(Code, "player.TakeDamage", withinOffset: "player.".Length)))
                     .GetProperty("items");

        Assert.True(items.GetArrayLength() > 0);
        foreach (var item in items.EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(item.GetProperty("label").GetString()));
            int kind = item.GetProperty("kind").GetInt32();
            Assert.InRange(kind, 1, 25);
        }
    }

    [Fact]
    public void Completion_AfterDot_OffersMembersOfVariableType()
    {
        // player は new Player() で得た型。TakeDamage / Health が候補に出るべき。
        using var s = OpenSession();

        var labels = CompletionLabels(s, TextPos.At(Code, "player.TakeDamage", withinOffset: "player.".Length));

        Assert.Contains("TakeDamage", labels);
        Assert.Contains("Health", labels);
    }

    [Fact]
    public void Completion_ItemLabels_AreUnique()
    {
        using var s = OpenSession();

        var labels = CompletionLabels(s, TextPos.At(Code, "player.TakeDamage", withinOffset: "player.".Length));

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Completion_OutOfRangePosition_StillReturnsValidList()
    {
        // 仕様: 範囲外の位置でもサーバーはエラーにせず、有効な CompletionList を返す。
        using var s = OpenSession();

        var result = s.Request("textDocument/completion", Params(new { line = 9999, character = 0 }));

        Assert.Equal(JsonValueKind.Array, result.GetProperty("items").ValueKind);
    }

    [Fact]
    public void Completion_OnUnknownDocument_ReturnsEmptyList()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = s.Request("textDocument/completion", new
        {
            textDocument = new { uri = "file:///Assets/Missing.cs" },
            position = new { line = 0, character = 0 }
        });

        Assert.Equal(0, result.GetProperty("items").GetArrayLength());
        Assert.False(result.GetProperty("isIncomplete").GetBoolean());
    }

    private static List<string> CompletionLabels(LspSession s, object position)
    {
        var items = s.Request("textDocument/completion", Params(position)).GetProperty("items");
        return items.EnumerateArray().Select(i => i.GetProperty("label").GetString()!).ToList();
    }
}
