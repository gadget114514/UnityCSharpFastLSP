using System.Diagnostics;
using System.Text.Json;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;
using UnityCSharpFastLSP.Workspace;

namespace UnityCSharpFastLSP.Server;

public static class SelfTestRunner
{
    public static int RunSelfTest()
    {
        Console.WriteLine("===============================================================================");
        Console.WriteLine(" UnityCSharpFastLSP End-to-End Cross-File Definition Jump Verification Suite");
        Console.WriteLine("===============================================================================");

        string tempDir = Path.Combine(Path.GetTempPath(), "FastLspSelfTest_" + Guid.NewGuid().ToString("N"));
        string assetsDir = Path.Combine(tempDir, "Assets", "Scripts");
        Directory.CreateDirectory(assetsDir);

        try
        {
            string baseEntityPath = Path.Combine(assetsDir, "BaseEntity.cs");
            string iDamageablePath = Path.Combine(assetsDir, "IDamageable.cs");
            string commonTypesPath = Path.Combine(assetsDir, "CommonTypes.cs");
            string playerPath = Path.Combine(assetsDir, "Player.cs");
            string combatSystemPath = Path.Combine(assetsDir, "CombatSystem.cs");

            File.WriteAllText(baseEntityPath, """
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

            File.WriteAllText(iDamageablePath, """
                namespace Game.Core
                {
                    public interface IDamageable
                    {
                        void TakeDamage(int amount);
                    }
                }
                """);

            File.WriteAllText(commonTypesPath, """
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
                    }

                    public static class Calculator
                    {
                        public static int Add(int a, int b) => a + b;
                    }
                }
                """);

            File.WriteAllText(playerPath, """
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

            File.WriteAllText(combatSystemPath, """
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

            using var output = new MemoryStream();
            var server = new LspServer(output, isProxy: true);

            string rootUri = UriHelper.Normalize(new Uri(tempDir).AbsoluteUri);
            string initJson = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"workspaceFolders\":[{\"uri\":\"" + rootUri + "\",\"name\":\"SelfTest\"}]}}";
            server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(initJson));

            int retries = 0;
            while (server.State.SymbolIndex.SymbolCount < 8 && retries++ < 50)
            {
                Thread.Sleep(20);
            }

            string combatUri = UriHelper.Normalize(new Uri(combatSystemPath).AbsoluteUri);
            string combatText = File.ReadAllText(combatSystemPath);
            string escaped = JsonSerializer.Serialize(combatText);
            string didOpenJson = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + combatUri + "\",\"languageId\":\"csharp\",\"version\":1,\"text\":" + escaped + "}}}";
            server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(didOpenJson));

            var snap = CsffSyntaxAdapter.Parse(combatText);

            var testCases = new (string Name, string Needle, string ExpectedFile, int SubOffset)[]
            {
                ("CrossFile_InheritedMethod", "targetPlayer.Heal(10)", "BaseEntity.cs", "targetPlayer.".Length),
                ("CrossFile_DerivedMethod", "targetPlayer.TakeDamage(baseDmg)", "Player.cs", "targetPlayer.".Length),
                ("CrossFile_Property", "targetPlayer.CurrentHealth", "Player.cs", "targetPlayer.".Length),
                ("CrossFile_EnumMember", "GameState.Playing", "CommonTypes.cs", "GameState.".Length),
                ("CrossFile_StaticMethod", "Calculator.Add(3, 4)", "CommonTypes.cs", "Calculator.".Length),
                ("CrossFile_StructName", "new PlayerData()", "CommonTypes.cs", "new ".Length),
                ("CrossFile_GetComponentInference", "compPlayer.TakeDamage(10)", "Player.cs", "compPlayer.".Length),
                ("CrossFile_AsCastInference", "castPlayer.TakeDamage(15)", "Player.cs", "castPlayer.".Length),
                ("SameFile_LocalVariable", "targetPlayer.TakeDamage(baseDmg)", "CombatSystem.cs", "targetPlayer.TakeDamage(".Length),
            };

            int passed = 0;
            int failed = 0;

            foreach (var tc in testCases)
            {
                int offset = combatText.IndexOf(tc.Needle, StringComparison.Ordinal) + tc.SubOffset;
                var (line, col) = snap.Lines.GetPosition(offset);

                output.SetLength(0);
                string req = "{\"jsonrpc\":\"2.0\",\"id\":999,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"" + combatUri + "\"},\"position\":{\"line\":" + line + ",\"character\":" + col + "}}}";

                var sw = Stopwatch.StartNew();
                server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(req));
                sw.Stop();

                string resp = System.Text.Encoding.UTF8.GetString(output.ToArray());
                bool ok = resp.Contains(tc.ExpectedFile, StringComparison.OrdinalIgnoreCase);

                if (ok)
                {
                    passed++;
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("  [PASS] ");
                    Console.ResetColor();
                    Console.WriteLine($"{tc.Name,-35} -> {tc.ExpectedFile,-18} ({sw.Elapsed.TotalMilliseconds:F3} ms)");
                }
                else
                {
                    failed++;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("  [FAIL] ");
                    Console.ResetColor();
                    Console.WriteLine($"{tc.Name,-35} Expected {tc.ExpectedFile}, got: {resp}");
                }
            }

            // Self-loop prevention check
            int declOffset = combatText.IndexOf("int baseDmg = 25", StringComparison.Ordinal) + "int ".Length;
            var (dlLine, dlCol) = snap.Lines.GetPosition(declOffset);
            output.SetLength(0);
            string selfReq = "{\"jsonrpc\":\"2.0\",\"id\":998,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"" + combatUri + "\"},\"position\":{\"line\":" + dlLine + ",\"character\":" + dlCol + "}}}";
            var ssw = Stopwatch.StartNew();
            server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(selfReq));
            ssw.Stop();
            string selfResp = System.Text.Encoding.UTF8.GetString(output.ToArray());
            bool selfOk = selfResp.Contains("\"result\":[]") || selfResp.Contains("\"result\": []");
            if (selfOk)
            {
                passed++;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("  [PASS] ");
                Console.ResetColor();
                Console.WriteLine($"{"SelfLoop_DeclarationPrevention",-35} -> (Empty List)       ({ssw.Elapsed.TotalMilliseconds:F3} ms)");
            }
            else
            {
                failed++;
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  [FAIL] ");
                Console.ResetColor();
                Console.WriteLine($"{"SelfLoop_DeclarationPrevention",-35} Expected empty result, got: {selfResp}");
            }

            Console.WriteLine("-------------------------------------------------------------------------------");
            if (failed == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  RESULT: ALL {passed}/{passed} CROSS-FILE DEFINITION JUMP SCENARIOS PASSED!");
                Console.ResetColor();
                return 0;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  RESULT: {failed} SCENARIO(S) FAILED out of {passed + failed}.");
                Console.ResetColor();
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FATAL ERROR during self-test]: {ex}");
            Console.ResetColor();
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
            catch { }
        }
    }
}
