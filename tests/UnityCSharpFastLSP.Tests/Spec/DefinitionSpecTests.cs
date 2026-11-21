using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/definition, declaration, typeDefinition の仕様適合を確認する。
/// 仕様: 結果は Location[]（または null）。Location は uri と 0 始まりの range を持つ。
/// </summary>
public class DefinitionSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Game.cs";

    private const string Code = """
        namespace Game
        {
            public class BaseEntity
            {
                public int MaxHealth = 100;
                public void Heal(int amount) {}
            }

            public class Player : BaseEntity
            {
                private int _currentHealth;

                public void TakeDamage(int amount)
                {
                    _currentHealth -= amount;
                }

                public void Update()
                {
                    int localDmg = 15;
                    TakeDamage(localDmg);
                    Heal(20);
                }
            }

            public static class Calculator
            {
                public static int Add(int a, int b) => a + b;
            }

            public class GameRunner
            {
                public void Run()
                {
                    var player = new Player();
                    player.TakeDamage(10);
                    int sum = Calculator.Add(1, 2);
                }
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

    private static JsonElement Definition(LspSession s, (int Line, int Character) p) =>
        s.Request("textDocument/definition", new
        {
            textDocument = new { uri = Uri },
            position = new { line = p.Line, character = p.Character }
        });

    private static JsonElement Request(LspSession s, string method, (int Line, int Character) p) =>
        s.Request(method, new
        {
            textDocument = new { uri = Uri },
            position = new { line = p.Line, character = p.Character }
        });

    private static int StartLine(JsonElement locations, int index = 0) =>
        locations[index].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();

    private static int Line(string needle, int occurrence = 0) => TextPos.Of(Code, needle, occurrence).Line;

    // ---------- 結果の形式 ----------

    [Fact]
    public void Result_IsArrayOfLocations()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "localDmg);"));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0);
        Assert.True(result[0].TryGetProperty("uri", out var uri));
        Assert.Equal(Uri, uri.GetString());
        Assert.True(result[0].TryGetProperty("range", out _));
    }

    [Fact]
    public void Result_RangeCoversIdentifierExactly()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "TakeDamage(localDmg)", withinOffset: "TakeDamage(".Length));

        var range = result[0].GetProperty("range");
        int width = range.GetProperty("end").GetProperty("character").GetInt32()
                  - range.GetProperty("start").GetProperty("character").GetInt32();
        Assert.Equal("localDmg".Length, width);
        Assert.Equal(range.GetProperty("start").GetProperty("line").GetInt32(),
                     range.GetProperty("end").GetProperty("line").GetInt32());
    }

    [Fact]
    public void Result_NoMatch_IsEmptyArray_NotError()
    {
        // 仕様: 定義が見つからない場合は null か空配列。エラーにはしない。
        using var s = OpenSession();

        // 空行（先頭の改行の直後）にカーソルを置く
        var result = Definition(s, TextPos.Of(Code, "\n\n", withinOffset: 1));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public void Result_IsDeterministic_ForRepeatedRequests()
    {
        using var s = OpenSession();
        var p = TextPos.Of(Code, "localDmg);");

        var first = Definition(s, p).GetRawText();
        var second = Definition(s, p).GetRawText();

        Assert.Equal(first, second);
    }

    // ---------- ローカルスコープ ----------

    [Fact]
    public void LocalVariableUse_JumpsToDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "TakeDamage(localDmg)", withinOffset: "TakeDamage(".Length));

        Assert.Equal(Line("int localDmg"), StartLine(result));
    }

    [Fact]
    public void LocalVariableUse_CursorInMiddleOfWord_JumpsToDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "TakeDamage(localDmg)", withinOffset: "TakeDamage(local".Length));

        Assert.Equal(Line("int localDmg"), StartLine(result));
    }

    [Fact]
    public void ParameterUse_JumpsToParameterDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "_currentHealth -= amount", withinOffset: "_currentHealth -= ".Length));

        Assert.Equal(Line("TakeDamage(int amount)"), StartLine(result));
    }

    [Fact]
    public void DeclarationItself_ReturnsEmptyArray_ToAvoidSelfLoop()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "localDmg = 15"));

        Assert.Equal(0, result.GetArrayLength());
    }

    // ---------- クラスメンバー ----------

    [Fact]
    public void SelfClassFieldUse_JumpsToFieldDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "_currentHealth -= amount"));

        Assert.Equal(Line("private int _currentHealth;"), StartLine(result));
    }

    [Fact]
    public void SelfClassMethodCall_JumpsToMethodDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "TakeDamage(localDmg)"));

        Assert.Equal(Line("public void TakeDamage"), StartLine(result));
    }

    [Fact]
    public void BaseClassMethodCall_JumpsToBaseDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "Heal(20)"));

        Assert.Equal(Line("public void Heal"), StartLine(result));
    }

    [Fact]
    public void MemberAccessOnLocalVariable_JumpsToMemberDeclaration()
    {
        // player は `var player = new Player();` なので型は Player。
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "player.TakeDamage(10)", withinOffset: "player.".Length));

        Assert.Equal(Line("public void TakeDamage"), StartLine(result));
    }

    [Fact]
    public void StaticMemberAccess_JumpsToStaticMemberDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "Calculator.Add(1, 2)", withinOffset: "Calculator.".Length));

        Assert.Equal(Line("public static int Add"), StartLine(result));
    }

    // ---------- 型名 ----------

    [Fact]
    public void TypeNameInObjectCreation_JumpsToClassDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, "new Player()", withinOffset: "new ".Length));

        Assert.Equal(Line("public class Player"), StartLine(result));
    }

    [Fact]
    public void BaseTypeName_JumpsToBaseClassDeclaration()
    {
        using var s = OpenSession();

        var result = Definition(s, TextPos.Of(Code, ": BaseEntity", withinOffset: ": ".Length));

        Assert.Equal(Line("public class BaseEntity"), StartLine(result));
    }

    // ---------- declaration / typeDefinition ----------

    [Fact]
    public void TypeDefinition_OfVariable_JumpsToItsClass()
    {
        using var s = OpenSession();

        var result = Request(s, "textDocument/typeDefinition", TextPos.Of(Code, "player.TakeDamage(10)"));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(Line("public class Player"), StartLine(result));
    }

    [Fact]
    public void Declaration_OfClassName_ReturnsClassDeclaration()
    {
        using var s = OpenSession();

        var result = Request(s, "textDocument/declaration", TextPos.Of(Code, "new Player()", withinOffset: "new ".Length));

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0, "declaration must return at least one location for a declared class.");
        Assert.Equal(Line("public class Player"), StartLine(result));
    }
}
