using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Server;
using UnityCSharpFastLSP.Syntax;
using UnityCSharpFastLSP.Workspace;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

public class CrossFileDefinitionJumpTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _baseEntityPath;
    private readonly string _iDamageablePath;
    private readonly string _commonTypesPath;
    private readonly string _playerPath;
    private readonly string _combatSystemPath;

    public CrossFileDefinitionJumpTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FastLspCrossFileTest_" + Guid.NewGuid().ToString("N"));
        string assetsDir = Path.Combine(_tempDir, "Assets", "Scripts");
        Directory.CreateDirectory(assetsDir);

        _baseEntityPath = Path.Combine(assetsDir, "BaseEntity.cs");
        _iDamageablePath = Path.Combine(assetsDir, "IDamageable.cs");
        _commonTypesPath = Path.Combine(assetsDir, "CommonTypes.cs");
        _playerPath = Path.Combine(assetsDir, "Player.cs");
        _combatSystemPath = Path.Combine(assetsDir, "CombatSystem.cs");

        File.WriteAllText(_baseEntityPath, """
            namespace Game.Core
            {
                public class BaseEntity
                {
                    public int MaxHealth = 100;
                    public virtual void Heal(int amount)
                    {
                        MaxHealth += amount;
                    }
                }
            }
            """);

        File.WriteAllText(_iDamageablePath, """
            namespace Game.Core
            {
                public interface IDamageable
                {
                    void TakeDamage(int amount);
                }
            }
            """);

        File.WriteAllText(_commonTypesPath, """
            namespace Game.Core
            {
                public enum GameState
                {
                    Lobby,
                    Playing,
                    GameOver
                }

                public struct PlayerData
                {
                    public int PlayerId;
                    public string PlayerName;
                }

                public static class Calculator
                {
                    public static int Add(int a, int b) => a + b;
                }
            }
            """);

        File.WriteAllText(_playerPath, """
            using UnityEngine;

            namespace Game.Core
            {
                public class Player : BaseEntity, IDamageable
                {
                    private int _currentHealth = 80;
                    public int CurrentHealth => _currentHealth;

                    public void TakeDamage(int amount)
                    {
                        _currentHealth -= amount;
                    }
                }
            }
            """);

        File.WriteAllText(_combatSystemPath, """
            using UnityEngine;

            namespace Game.Core
            {
                public class CombatSystem : MonoBehaviour
                {
                    [SerializeField] private Player targetPlayer = null!;

                    public void RunCombat()
                    {
                        int baseDmg = 25;
                        targetPlayer.TakeDamage(baseDmg);
                        targetPlayer.Heal(10);
                        int hp = targetPlayer.CurrentHealth;

                        var spawnedPlayer = new Player();
                        spawnedPlayer.TakeDamage(5);

                        var compPlayer = GetComponent<Player>();
                        compPlayer.TakeDamage(10);

                        object unknown = targetPlayer;
                        var castPlayer = unknown as Player;
                        castPlayer.TakeDamage(15);

                        GameState state = GameState.Playing;
                        int sum = Calculator.Add(3, 4);
                        PlayerData data = new PlayerData();
                    }
                }
            }
            """);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch { }
    }

    private (LspServer Server, MemoryStream Output, SymbolIndex Index) CreateServerWithWorkspace()
    {
        var output = new MemoryStream();
        var server = new LspServer(output, isProxy: true);

        // 1. LSP Initialize with modern workspaceFolders format
        string rootUri = UriHelper.Normalize(new Uri(_tempDir).AbsoluteUri);
        string initRequest = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"workspaceFolders\":[{\"uri\":\"" + rootUri + "\",\"name\":\"TestWorkspace\"}]}}";
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(initRequest));

        // Workspace indexing runs in background Task; wait until indexed
        int retries = 0;
        while (server.State.SymbolIndex.SymbolCount < 8 && retries++ < 50)
        {
            Thread.Sleep(30);
        }

        return (server, output, server.State.SymbolIndex);
    }

    private static byte[] MakeDidOpen(string uri, string text)
    {
        string escaped = JsonSerializer.Serialize(text);
        string json = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":" + escaped + "}}}";
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private static byte[] MakeDefinitionRequest(int id, string uri, int line, int character)
    {
        string json = "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\"},\"position\":{\"line\":" + line + ",\"character\":" + character + "}}}";
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    [Fact]
    public void WorkspaceIndexingFindsAllFilesAndSymbols()
    {
        var (server, _, index) = CreateServerWithWorkspace();
        Assert.True(index.SymbolCount >= 10, $"Expected >= 10 symbols across all 5 files, but got {index.SymbolCount}");

        // Verify enum and enum members are indexed
        var enums = index.FindSymbolsByName("GameState");
        Assert.NotEmpty(enums);
        Assert.Equal(SymbolKind.Enum, enums[0].Kind);

        var enumMembers = index.FindMembersByContainingType("GameState");
        Assert.True(enumMembers.Count >= 3, $"Expected >= 3 enum members in GameState, but got {enumMembers.Count}");
        Assert.Contains(enumMembers, m => m.Name == "Playing");
    }

    [Fact]
    public void CrossFileJumpToInheritedMethodInBaseClass()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // targetPlayer.Heal(10); の Heal -> BaseEntity.cs の Heal にジャンプすること
        int healOffset = combatText.IndexOf("targetPlayer.Heal(10)", StringComparison.Ordinal) + "targetPlayer.".Length;
        var (hLine, hCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(healOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(100, combatUri, hLine, hCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("BaseEntity.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossFileJumpToDerivedClassMethod()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // targetPlayer.TakeDamage(baseDmg); -> Player.cs の TakeDamage
        int dmgOffset = combatText.IndexOf("targetPlayer.TakeDamage", StringComparison.Ordinal) + "targetPlayer.".Length;
        var (dLine, dCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(dmgOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(101, combatUri, dLine, dCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Player.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossFileJumpToEnumMember()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // GameState.Playing の Playing -> CommonTypes.cs の Playing
        int playOffset = combatText.IndexOf("GameState.Playing", StringComparison.Ordinal) + "GameState.".Length;
        var (pLine, pCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(playOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(102, combatUri, pLine, pCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("CommonTypes.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossFileJumpToStaticMethod()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // Calculator.Add(3, 4) の Add -> CommonTypes.cs の Add
        int addOffset = combatText.IndexOf("Calculator.Add", StringComparison.Ordinal) + "Calculator.".Length;
        var (aLine, aCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(addOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(103, combatUri, aLine, aCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("CommonTypes.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossFileJumpViaGetComponent()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // compPlayer.TakeDamage(10) の TakeDamage -> Player.cs の TakeDamage
        int compOffset = combatText.IndexOf("compPlayer.TakeDamage(10)", StringComparison.Ordinal) + "compPlayer.".Length;
        var (cLine, cCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(compOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(104, combatUri, cLine, cCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Player.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CrossFileJumpViaAsCast()
    {
        var (server, output, index) = CreateServerWithWorkspace();
        string combatUri = UriHelper.Normalize(new Uri(_combatSystemPath).AbsoluteUri);
        string combatText = File.ReadAllText(_combatSystemPath);
        server.HandleMessage(MakeDidOpen(combatUri, combatText));

        // castPlayer.TakeDamage(15) の TakeDamage -> Player.cs の TakeDamage
        int castOffset = combatText.IndexOf("castPlayer.TakeDamage(15)", StringComparison.Ordinal) + "castPlayer.".Length;
        var (cLine, cCol) = CsffSyntaxAdapter.Parse(combatText).Lines.GetPosition(castOffset);

        output.SetLength(0);
        server.HandleMessage(MakeDefinitionRequest(105, combatUri, cLine, cCol));

        string responseJson = System.Text.Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Player.cs", responseJson, StringComparison.OrdinalIgnoreCase);
    }
}
