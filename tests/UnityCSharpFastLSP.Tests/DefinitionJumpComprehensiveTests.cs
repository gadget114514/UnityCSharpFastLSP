using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

public class DefinitionJumpComprehensiveTests
{
    private const string Code = """
        namespace Game.Tests
        {
            public class BaseEntity
            {
                public int MaxHealth = 100;
                public void Heal(int amount) {}
            }

            public class Player : BaseEntity
            {
                private int _currentHealth;
                public int CurrentHealth => _currentHealth;

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

    private (DocumentSemanticModel Model, DocumentSnapshot Snapshot, string Uri) SetupEnvironment()
    {
        string uri = "file:///Assets/Scripts/Game.cs";
        var snapshot = CsffSyntaxAdapter.Parse(Code);
        var index = new SymbolIndex();
        var symbols = SymbolExtractor.ExtractSymbols(uri, snapshot);
        index.UpdateFileSymbols(uri, symbols);
        var model = new DocumentSemanticModel(snapshot, index);
        return (model, snapshot, uri);
    }

    [Fact]
    public void JumpToLocalVariableDeclaration()
    {
        var (model, snap, uri) = SetupEnvironment();

        // TakeDamage(localDmg); の localDmg の使用位置
        int usageOffset = Code.IndexOf("TakeDamage(localDmg)", StringComparison.Ordinal) + "TakeDamage(".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        // 宣言 int localDmg = 15; の位置と一致すること
        int declOffset = Code.IndexOf("localDmg = 15", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToMethodParameterDeclaration()
    {
        var (model, snap, uri) = SetupEnvironment();

        // _currentHealth -= amount; の amount の使用位置
        int usageOffset = Code.IndexOf("_currentHealth -= amount;", StringComparison.Ordinal) + "_currentHealth -= ".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        // void TakeDamage(int amount) のパラメータ位置と一致すること
        int declOffset = Code.IndexOf("TakeDamage(int amount)", StringComparison.Ordinal) + "TakeDamage(int ".Length;
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToSelfClassFieldDeclaration()
    {
        var (model, snap, uri) = SetupEnvironment();

        // _currentHealth -= amount; の _currentHealth
        int usageOffset = Code.IndexOf("_currentHealth -= amount", StringComparison.Ordinal);
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        // private int _currentHealth; の行と一致すること
        int declOffset = Code.IndexOf("private int _currentHealth;", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToSelfClassMethodCall()
    {
        var (model, snap, uri) = SetupEnvironment();

        // TakeDamage(localDmg); の TakeDamage
        int usageOffset = Code.IndexOf("TakeDamage(localDmg)", StringComparison.Ordinal);
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        Assert.NotEmpty(locs);

        // public void TakeDamage(int amount) の行と一致すること
        int declOffset = Code.IndexOf("public void TakeDamage", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToBaseClassMethodCall()
    {
        var (model, snap, uri) = SetupEnvironment();

        // Heal(20); の Heal (基底クラス BaseEntity のメンバー)
        int usageOffset = Code.IndexOf("Heal(20)", StringComparison.Ordinal);
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        // public void Heal(int amount) の行と一致すること
        int declOffset = Code.IndexOf("public void Heal", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToDotMemberAccess()
    {
        var (model, snap, uri) = SetupEnvironment();

        // player.TakeDamage(10); の TakeDamage
        int usageOffset = Code.IndexOf("player.TakeDamage(10)", StringComparison.Ordinal) + "player.".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        int declOffset = Code.IndexOf("public void TakeDamage", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToStaticMethodCall()
    {
        var (model, snap, uri) = SetupEnvironment();

        // Calculator.Add(1, 2); の Add
        int usageOffset = Code.IndexOf("Calculator.Add", StringComparison.Ordinal) + "Calculator.".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        int declOffset = Code.IndexOf("public static int Add", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToClassNameDeclaration()
    {
        var (model, snap, uri) = SetupEnvironment();

        // var player = new Player(); の Player
        int usageOffset = Code.IndexOf("new Player()", StringComparison.Ordinal) + "new ".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        int declOffset = Code.IndexOf("public class Player", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpToBaseClassNameDeclaration()
    {
        var (model, snap, uri) = SetupEnvironment();

        // public class Player : BaseEntity の BaseEntity
        int usageOffset = Code.IndexOf(": BaseEntity", StringComparison.Ordinal) + ": ".Length;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        int declOffset = Code.IndexOf("public class BaseEntity", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpWithCursorInMiddleOfWord()
    {
        var (model, snap, uri) = SetupEnvironment();

        // TakeDamage の "Dam" 付近（中央）にカーソル
        int usageOffset = Code.IndexOf("TakeDamage(localDmg)", StringComparison.Ordinal) + 4;
        var (uLine, uCol) = snap.Lines.GetPosition(usageOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(uLine, uCol));
        var syms = model.Index.FindMembersByContainingType("Player");
        Assert.True(locs.Count > 0, $"word at ({uLine},{uCol}) is '{model.GetWordAtPosition(new Position(uLine, uCol))}'. Player members: [{string.Join(", ", syms.Select(s => s.Name))}], all symbols: [{string.Join(", ", model.Index.FindWorkspaceSymbols("").Select(s => $"{s.Name}(in {s.ContainerName})"))}]");

        int declOffset = Code.IndexOf("public void TakeDamage", StringComparison.Ordinal);
        var (dLine, _) = snap.Lines.GetPosition(declOffset);
        Assert.Equal(dLine, locs[0].Range.Start.Line);
    }

    [Fact]
    public void JumpOnDeclarationItselfReturnsEmptyToPreventSelfLoop()
    {
        var (model, snap, uri) = SetupEnvironment();

        // 宣言自身 int localDmg = 15; の localDmg の上
        int declOffset = Code.IndexOf("localDmg = 15", StringComparison.Ordinal);
        var (dLine, dCol) = snap.Lines.GetPosition(declOffset);

        var locs = ScopedResolver.ResolveScopedDefinition(model, uri, new Position(dLine, dCol));
        // 宣言位置そのものの上では自分自身を指すLocationは返さない（ループ防止）
        Assert.Empty(locs);
    }

    [Fact]
    public void DummyProjectRealWorldVerification()
    {
        string playerCode = """
            using UnityEngine;
            using Game.Data;

            namespace Game.Runtime
            {
                public sealed class PlayerController : MonoBehaviour
                {
                    [SerializeField] private GameConfig config = null!;

                    public int GetConfiguredHealth()
                    {
                        return config.MaxHealth;
                    }
                }

                public interface IDamageable
                {
                    void ApplyDamage(int amount);
                }

                public sealed class Enemy : IDamageable
                {
                    public void ApplyDamage(int amount)
                    {
                        Report(amount);
                    }

                    private void Report(int amount)
                    {
                    }
                }

                public static class EnemyFactory
                {
                    public static Enemy Create()
                    {
                        var enemy = new Enemy();
                        enemy.ApplyDamage(1);
                        return enemy;
                    }
                }
            }
            """;

        string configCode = """
            using UnityEngine;

            namespace Game.Data
            {
                public sealed class GameConfig : ScriptableObject
                {
                    [SerializeField] private int maxHealth = 100;
                    public int MaxHealth => maxHealth;
                }
            }
            """;

        string playerUri = "file:///Assets/Scripts/PlayerController.cs";
        string configUri = "file:///Assets/Scripts/Data/GameConfig.cs";

        var playerSnap = CsffSyntaxAdapter.Parse(playerCode);
        var configSnap = CsffSyntaxAdapter.Parse(configCode);

        var index = new SymbolIndex();
        index.UpdateFileSymbols(playerUri, SymbolExtractor.ExtractSymbols(playerUri, playerSnap));
        index.UpdateFileSymbols(configUri, SymbolExtractor.ExtractSymbols(configUri, configSnap));

        var model = new DocumentSemanticModel(playerSnap, index);

        // 1. Cross-file member access: config.MaxHealth -> jumps to GameConfig.cs MaxHealth
        int maxHealthUsage = playerCode.IndexOf("config.MaxHealth", StringComparison.Ordinal) + "config.".Length;
        var (hLine, hCol) = playerSnap.Lines.GetPosition(maxHealthUsage);
        var hDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(hLine, hCol));
        Assert.NotEmpty(hDefs);
        Assert.Equal(configUri, hDefs[0].Uri);
        int maxHealthDecl = configCode.IndexOf("public int MaxHealth", StringComparison.Ordinal);
        var (expHLine, _) = configSnap.Lines.GetPosition(maxHealthDecl);
        Assert.Equal(expHLine, hDefs[0].Range.Start.Line);

        // 2. Local var method call: enemy.ApplyDamage(1) -> jumps to Enemy.ApplyDamage
        int applyDmgUsage = playerCode.IndexOf("enemy.ApplyDamage(1)", StringComparison.Ordinal) + "enemy.".Length;
        var (aLine, aCol) = playerSnap.Lines.GetPosition(applyDmgUsage);
        var aDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(aLine, aCol));
        Assert.NotEmpty(aDefs);
        int applyDmgDecl = playerCode.IndexOf("public void ApplyDamage(int amount)", StringComparison.Ordinal);
        var (expALine, _) = playerSnap.Lines.GetPosition(applyDmgDecl);
        Assert.Equal(expALine, aDefs[0].Range.Start.Line);

        // 3. Self-class private method: Report(amount) inside Enemy -> jumps to Report
        int reportUsage = playerCode.IndexOf("Report(amount)", StringComparison.Ordinal);
        var (rLine, rCol) = playerSnap.Lines.GetPosition(reportUsage);
        var rDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(rLine, rCol));
        Assert.NotEmpty(rDefs);
        int reportDecl = playerCode.IndexOf("private void Report(int amount)", StringComparison.Ordinal);
        var (expRLine, _) = playerSnap.Lines.GetPosition(reportDecl);
        Assert.Equal(expRLine, rDefs[0].Range.Start.Line);

        // 4. Parameter reference: amount in Report(amount) -> jumps to ApplyDamage(int amount) parameter
        int paramUsage = playerCode.IndexOf("Report(amount)", StringComparison.Ordinal) + "Report(".Length;
        var (pLine, pCol) = playerSnap.Lines.GetPosition(paramUsage);
        var pDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(pLine, pCol));
        Assert.NotEmpty(pDefs);
        int enemyBlock = playerCode.IndexOf("public sealed class Enemy", StringComparison.Ordinal);
        int paramDecl = playerCode.IndexOf("ApplyDamage(int amount)", enemyBlock, StringComparison.Ordinal) + "ApplyDamage(int ".Length;
        var (expPLine, _) = playerSnap.Lines.GetPosition(paramDecl);
        Assert.Equal(expPLine, pDefs[0].Range.Start.Line);

        // 5. Interface inheritance: class Enemy : IDamageable -> jumps to interface IDamageable
        int ifaceUsage = playerCode.IndexOf(": IDamageable", StringComparison.Ordinal) + ": ".Length;
        var (iLine, iCol) = playerSnap.Lines.GetPosition(ifaceUsage);
        var iDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(iLine, iCol));
        Assert.NotEmpty(iDefs);
        int ifaceDecl = playerCode.IndexOf("public interface IDamageable", StringComparison.Ordinal);
        var (expILine, _) = playerSnap.Lines.GetPosition(ifaceDecl);
        Assert.True(expILine == iDefs[0].Range.Start.Line, $"Expected line {expILine}, but got line {iDefs[0].Range.Start.Line}, col {iDefs[0].Range.Start.Character}, Uri={iDefs[0].Uri}. All defs: [{string.Join(", ", iDefs.Select(d => $"{d.Uri}:{d.Range.Start.Line}:{d.Range.Start.Character}"))}]");

        // 6. Self-class field access: config in config.MaxHealth -> jumps to private GameConfig config
        int configUsage = playerCode.IndexOf("config.MaxHealth", StringComparison.Ordinal);
        var (cLine, cCol) = playerSnap.Lines.GetPosition(configUsage);
        var cDefs = ScopedResolver.ResolveScopedDefinition(model, playerUri, new Position(cLine, cCol));
        Assert.NotEmpty(cDefs);
        int configFieldDecl = playerCode.IndexOf("private GameConfig config", StringComparison.Ordinal);
        var (expCLine, _) = playerSnap.Lines.GetPosition(configFieldDecl);
        Assert.Equal(expCLine, cDefs[0].Range.Start.Line);
    }
}
