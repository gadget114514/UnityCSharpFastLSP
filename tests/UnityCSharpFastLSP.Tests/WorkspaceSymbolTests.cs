using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

public class WorkspaceSymbolTests
{
    private const string Code = """
        namespace Game.Combat
        {
            public class PlayerController : MonoBehaviour
            {
                public int Health;
                public void TakeDamage(int amount) {}
                public void Heal(int amount) {}
            }

            public class EnemyCharacter
            {
                public int Damage;
                public void Attack() {}
            }
        }
        """;

    private SymbolIndex BuildIndex()
    {
        var index = new SymbolIndex();
        string uri = "file:///Assets/Scripts/PlayerController.cs";
        var snapshot = CsffSyntaxAdapter.Parse(Code);
        var symbols = SymbolExtractor.ExtractSymbols(uri, snapshot);
        index.UpdateFileSymbols(uri, symbols);
        return index;
    }

    [Fact]
    public void ExactMatchFindsTargetSymbol()
    {
        var index = BuildIndex();
        var results = index.FindWorkspaceSymbols("PlayerController");

        Assert.NotEmpty(results);
        Assert.Equal("PlayerController", results[0].Name);
        Assert.Equal(SymbolKind.Class, results[0].Kind);
    }

    [Fact]
    public void SubstringMatchFindsSymbols()
    {
        var index = BuildIndex();
        var results = index.FindWorkspaceSymbols("damage");

        Assert.Contains(results, r => r.Name == "TakeDamage");
        Assert.Contains(results, r => r.Name == "Damage");
    }

    [Fact]
    public void WildcardMatchWorksWithStarAndQuestion()
    {
        var index = BuildIndex();

        // *Controller
        var controllers = index.FindWorkspaceSymbols("*Controller");
        Assert.NotEmpty(controllers);
        Assert.Equal("PlayerController", controllers[0].Name);

        // Take?amage
        var takeDamage = index.FindWorkspaceSymbols("Take?amage");
        Assert.NotEmpty(takeDamage);
        Assert.Equal("TakeDamage", takeDamage[0].Name);
    }

    [Fact]
    public void CamelHumpMatchResolvesAbbreviation()
    {
        var index = BuildIndex();

        // PC -> PlayerController
        var pcResults = index.FindWorkspaceSymbols("PC");
        Assert.NotEmpty(pcResults);
        Assert.Contains(pcResults, r => r.Name == "PlayerController");

        // TD -> TakeDamage
        var tdResults = index.FindWorkspaceSymbols("TD");
        Assert.NotEmpty(tdResults);
        Assert.Contains(tdResults, r => r.Name == "TakeDamage");

        // EC -> EnemyCharacter
        var ecResults = index.FindWorkspaceSymbols("EC");
        Assert.NotEmpty(ecResults);
        Assert.Contains(ecResults, r => r.Name == "EnemyCharacter");
    }

    [Fact]
    public void ContainerQualifiedPatternMatchesMemberInClass()
    {
        var index = BuildIndex();

        // PlayerController.TakeDamage
        var results = index.FindWorkspaceSymbols("PlayerController.TakeDamage");
        Assert.NotEmpty(results);
        Assert.Equal("TakeDamage", results[0].Name);
        Assert.Equal("PlayerController", results[0].ContainerName);

        // Player.Heal (partial container match)
        var healResults = index.FindWorkspaceSymbols("Player.Heal");
        Assert.NotEmpty(healResults);
        Assert.Equal("Heal", healResults[0].Name);

        // Enemy.Attack
        var attackResults = index.FindWorkspaceSymbols("Enemy.Attack");
        Assert.NotEmpty(attackResults);
        Assert.Equal("Attack", attackResults[0].Name);
    }

    [Fact]
    public void EmptyQueryReturnsAllSymbols()
    {
        var index = BuildIndex();
        var results = index.FindWorkspaceSymbols("");

        Assert.True(results.Count >= 7); // Game, Combat, PlayerController, Health, TakeDamage, Heal, EnemyCharacter, Damage, Attack
    }
}
