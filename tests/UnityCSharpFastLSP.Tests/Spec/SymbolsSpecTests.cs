using System.Text.Json;
using Xunit;

namespace UnityCSharpFastLSP.Tests.Spec;

/// <summary>
/// textDocument/documentSymbol（階層形式 DocumentSymbol[]）と
/// workspace/symbol（SymbolInformation[]）の仕様適合を確認する。
/// </summary>
public class SymbolsSpecTests
{
    private const string Uri = "file:///Assets/Scripts/Symbols.cs";

    private const string Code = """
        namespace Game.Combat
        {
            public class PlayerController : MonoBehaviour
            {
                public int Health;
                public PlayerController() {}
                public void TakeDamage(int amount) {}
                public int MaxHealth { get; set; }
            }

            public class EnemyCharacter
            {
                public int Damage;
                public void Attack() {}
            }
        }
        """;

    private static LspSession OpenSession(string uri = Uri, string code = Code)
    {
        var s = new LspSession();
        s.Initialize();
        s.OpenDocument(uri, code);
        return s;
    }

    private static JsonElement DocumentSymbols(LspSession s, string uri = Uri) =>
        s.Request("textDocument/documentSymbol", new { textDocument = new { uri } });

    private static JsonElement WorkspaceSymbols(LspSession s, string query) =>
        s.Request("workspace/symbol", new { query });

    private static JsonElement? FindByName(JsonElement symbols, string name)
    {
        foreach (var sym in symbols.EnumerateArray())
        {
            if (sym.GetProperty("name").GetString() == name) return sym;
            if (sym.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                var found = FindByName(children, name);
                if (found.HasValue) return found;
            }
        }
        return null;
    }

    private static bool Contains(Range outer, Range inner) =>
        (inner.StartLine > outer.StartLine || (inner.StartLine == outer.StartLine && inner.StartChar >= outer.StartChar)) &&
        (inner.EndLine < outer.EndLine || (inner.EndLine == outer.EndLine && inner.EndChar <= outer.EndChar));

    private readonly record struct Range(int StartLine, int StartChar, int EndLine, int EndChar);

    private static Range RangeOf(JsonElement element, string property)
    {
        var r = element.GetProperty(property);
        return new Range(
            r.GetProperty("start").GetProperty("line").GetInt32(),
            r.GetProperty("start").GetProperty("character").GetInt32(),
            r.GetProperty("end").GetProperty("line").GetInt32(),
            r.GetProperty("end").GetProperty("character").GetInt32());
    }

    // ---------- documentSymbol ----------

    [Fact]
    public void DocumentSymbol_ResultIsArray()
    {
        using var s = OpenSession();

        Assert.Equal(JsonValueKind.Array, DocumentSymbols(s).ValueKind);
    }

    [Fact]
    public void DocumentSymbol_IsHierarchical_MembersAreChildrenOfClass()
    {
        // 仕様: DocumentSymbol は階層構造。メンバーは所属クラスの children に入る。
        using var s = OpenSession();

        var player = FindByName(DocumentSymbols(s), "PlayerController");

        Assert.True(player.HasValue, "PlayerController must be a top-level or nested symbol.");
        Assert.True(player!.Value.TryGetProperty("children", out var children));
        var names = children.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("Health", names);
        Assert.Contains("TakeDamage", names);
    }

    [Fact]
    public void DocumentSymbol_EachSymbolHasNameKindRangeAndSelectionRange()
    {
        using var s = OpenSession();

        foreach (var sym in DocumentSymbols(s).EnumerateArray())
        {
            Assert.False(string.IsNullOrEmpty(sym.GetProperty("name").GetString()));
            Assert.InRange(sym.GetProperty("kind").GetInt32(), 1, 26);
            Assert.True(sym.TryGetProperty("range", out _));
            Assert.True(sym.TryGetProperty("selectionRange", out _));
        }
    }

    [Fact]
    public void DocumentSymbol_SelectionRange_IsContainedInRange()
    {
        // 仕様: selectionRange は range に含まれていなければならない。
        using var s = OpenSession();
        var symbols = DocumentSymbols(s);

        void Check(JsonElement list)
        {
            foreach (var sym in list.EnumerateArray())
            {
                Assert.True(Contains(RangeOf(sym, "range"), RangeOf(sym, "selectionRange")),
                    $"selectionRange of '{sym.GetProperty("name").GetString()}' is outside its range.");
                if (sym.TryGetProperty("children", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    Check(children);
                }
            }
        }

        Check(symbols);
    }

    [Fact]
    public void DocumentSymbol_ChildRange_IsContainedInParentRange()
    {
        using var s = OpenSession();
        var player = FindByName(DocumentSymbols(s), "PlayerController")!.Value;

        foreach (var child in player.GetProperty("children").EnumerateArray())
        {
            Assert.True(Contains(RangeOf(player, "range"), RangeOf(child, "range")),
                $"Child '{child.GetProperty("name").GetString()}' lies outside its parent.");
        }
    }

    [Fact]
    public void DocumentSymbol_KindMapping_ClassIsClassKind()
    {
        // 仕様: SymbolKind.Class = 5
        using var s = OpenSession();

        var enemy = FindByName(DocumentSymbols(s), "EnemyCharacter");

        Assert.True(enemy.HasValue);
        Assert.Equal(5, enemy!.Value.GetProperty("kind").GetInt32());
    }

    [Fact]
    public void DocumentSymbol_KindMapping_MethodIsMethodKind()
    {
        // 仕様: SymbolKind.Method = 6
        using var s = OpenSession();

        var attack = FindByName(DocumentSymbols(s), "Attack");

        Assert.True(attack.HasValue);
        Assert.Equal(6, attack!.Value.GetProperty("kind").GetInt32());
    }

    [Fact]
    public void DocumentSymbol_KindMapping_FieldIsFieldKind()
    {
        // 仕様: SymbolKind.Field = 8
        using var s = OpenSession();

        var damage = FindByName(DocumentSymbols(s), "Damage");

        Assert.True(damage.HasValue);
        Assert.Equal(8, damage!.Value.GetProperty("kind").GetInt32());
    }

    [Fact]
    public void DocumentSymbol_KindMapping_PropertyIsPropertyKind()
    {
        // 仕様: SymbolKind.Property = 7
        using var s = OpenSession();

        var maxHealth = FindByName(DocumentSymbols(s), "MaxHealth");

        Assert.True(maxHealth.HasValue);
        Assert.Equal(7, maxHealth!.Value.GetProperty("kind").GetInt32());
    }

    [Fact]
    public void DocumentSymbol_KindMapping_ConstructorIsConstructorKind()
    {
        // 仕様: SymbolKind.Constructor = 9
        using var s = OpenSession();

        var ctor = FindByName(DocumentSymbols(s), "PlayerController");
        Assert.True(ctor.HasValue);

        // children の中の、同名で kind=9 のものを探す
        bool hasCtor = ctor!.Value.GetProperty("children").EnumerateArray()
            .Any(c => c.GetProperty("kind").GetInt32() == 9);
        Assert.True(hasCtor, "Constructor must be reported with SymbolKind.Constructor (9).");
    }

    [Fact]
    public void DocumentSymbol_EmptyDocument_ReturnsEmptyArray()
    {
        using var s = OpenSession(uri: "file:///Assets/Empty.cs", code: "");

        var result = DocumentSymbols(s, "file:///Assets/Empty.cs");

        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public void DocumentSymbol_UnknownDocument_ReturnsEmptyArray()
    {
        using var s = new LspSession();
        s.Initialize();

        var result = DocumentSymbols(s, "file:///Assets/Unknown.cs");

        Assert.Equal(0, result.GetArrayLength());
    }

    // ---------- workspace/symbol ----------

    [Fact]
    public void WorkspaceSymbol_ResultIsSymbolInformationArray()
    {
        using var s = OpenSession();

        var result = WorkspaceSymbols(s, "Player");

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0);
        var first = result[0];
        Assert.True(first.TryGetProperty("name", out _));
        Assert.True(first.TryGetProperty("kind", out _));
        Assert.True(first.TryGetProperty("location", out var loc));
        Assert.True(loc.TryGetProperty("uri", out _));
        Assert.True(loc.TryGetProperty("range", out _));
    }

    [Fact]
    public void WorkspaceSymbol_ExactName_IsFound()
    {
        using var s = OpenSession();

        var names = WorkspaceSymbols(s, "EnemyCharacter").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();

        Assert.Contains("EnemyCharacter", names);
    }

    [Fact]
    public void WorkspaceSymbol_IsCaseInsensitive()
    {
        using var s = OpenSession();

        var names = WorkspaceSymbols(s, "enemycharacter").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();

        Assert.Contains("EnemyCharacter", names);
    }

    [Fact]
    public void WorkspaceSymbol_MemberHasContainerName()
    {
        // 仕様: containerName は所属するコンテナ名（ここではクラス名）。
        using var s = OpenSession();

        var attack = WorkspaceSymbols(s, "Attack").EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == "Attack");

        Assert.Equal("EnemyCharacter", attack.GetProperty("containerName").GetString());
    }

    [Fact]
    public void WorkspaceSymbol_LocationUriPointsToOpenDocument()
    {
        using var s = OpenSession();

        var result = WorkspaceSymbols(s, "EnemyCharacter");
        var loc = result[0].GetProperty("location");

        Assert.Equal(Uri, loc.GetProperty("uri").GetString());
    }

    [Fact]
    public void WorkspaceSymbol_NoMatch_ReturnsEmptyArray()
    {
        using var s = OpenSession();

        var result = WorkspaceSymbols(s, "ZzzNoSuchSymbolZzz");

        Assert.Equal(0, result.GetArrayLength());
    }

    [Fact]
    public void WorkspaceSymbol_EmptyQuery_ReturnsSymbols()
    {
        // 仕様: 空のクエリは全シンボルを返してよい（少なくとも空配列ではない）。
        using var s = OpenSession();

        var result = WorkspaceSymbols(s, "");

        Assert.True(result.GetArrayLength() > 0);
    }

    [Fact]
    public void WorkspaceSymbol_Wildcard_IsSupported()
    {
        // 拡張仕様: '*' によるワイルドカード検索。
        using var s = OpenSession();

        var names = WorkspaceSymbols(s, "*Damage*").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();

        Assert.Contains("TakeDamage", names);
        Assert.Contains("Damage", names);
    }
}
