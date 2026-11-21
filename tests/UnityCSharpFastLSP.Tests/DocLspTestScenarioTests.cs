using System.Text.Json;
using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>
/// doc/lsp-test.md に記載された C# 定義ジャンプの再現・検証テストスイート
/// 
/// 検証ケース:
/// 1. capabilityUI (same file): 21行、文字3 -> CapabilityUI.cs の 12行、文字51 (フィールド宣言)
/// 2. ShowCapabilitiesUI (other file): 21行、文字18 -> CurrentCapabilitiesUIController.cs の 25行、文字20 (メソッド宣言)
/// </summary>
public class DocLspTestScenarioTests : IDisposable
{
    private readonly string _tempWorkspaceDir;
    private readonly string _capabilityUiPath;
    private readonly string _controllerPath;
    private readonly string _capabilityUiUri;
    private readonly string _controllerUri;

    public DocLspTestScenarioTests()
    {
        _tempWorkspaceDir = Path.Combine(Path.GetTempPath(), "FastLspDocTest_" + Guid.NewGuid().ToString("N"));
        string uiDir = Path.Combine(_tempWorkspaceDir, "Assets", "App", "Scripts", "UI");
        Directory.CreateDirectory(uiDir);

        _capabilityUiPath = Path.Combine(uiDir, "CapabilityUI.cs");
        _controllerPath = Path.Combine(uiDir, "CurrentCapabilitiesUIController.cs");

        _capabilityUiUri = new Uri(Path.GetFullPath(_capabilityUiPath)).AbsoluteUri;
        _controllerUri = new Uri(Path.GetFullPath(_controllerPath)).AbsoluteUri;

        // CurrentCapabilitiesUIController.cs を構築
        // 25行目、文字20から ShowCapabilitiesUI が始まるように構成
        var controllerLines = new List<string>
        {
            "using UnityEngine;",                                // 0
            "using System;",                                     // 1
            "",                                                  // 2
            "namespace App.UI",                                  // 3
            "{",                                                 // 4
            "    public class CurrentCapabilitiesUIController",  // 5
            "    {",                                             // 6
            "        public int ControllerId = 1;",              // 7
            "        public bool IsActive = true;",              // 8
            "",                                                  // 9
            "        public void Initialize()",                  // 10
            "        {",                                         // 11
            "        }",                                         // 12
            "",                                                  // 13
            "        public void ResetUI()",                     // 14
            "        {",                                         // 15
            "        }",                                         // 16
            "",                                                  // 17
            "        public void UpdateUI()",                    // 18
            "        {",                                         // 19
            "        }",                                         // 20
            "",                                                  // 21
            "        public void Close()",                       // 22
            "        {",                                         // 23
            "        }",                                         // 24
            "        public void ShowCapabilitiesUI()",          // 25 (8+7+5 = 20文字目からShowCapabilitiesUI)
            "        {",                                         // 26
            "        }",                                         // 27
            "    }",                                             // 28
            "}"                                                  // 29
        };
        File.WriteAllText(_controllerPath, string.Join("\n", controllerLines));

        // CapabilityUI.cs を構築
        // 12行目、文字51から capabilityUI が始まる
        // 21行目: capabilityUI.ShowCapabilitiesUI(); (文字3: capabilityUI, 文字18: ShowCapabilitiesUI)
        string line12Prefix = "        private CurrentCapabilitiesUIController    "; // 8 + 8 + 32 + 3 = 51文字 (末尾4スペースで51文字)
        string line12 = line12Prefix + "capabilityUI;"; // capabilityUI starts at col 51

        var uiLines = new List<string>
        {
            "using UnityEngine;",                                // 0
            "using System;",                                     // 1
            "",                                                  // 2
            "namespace App.UI",                                  // 3
            "{",                                                 // 4
            "    public class CapabilityUI",                     // 5
            "    {",                                             // 6
            "        public int ViewIndex;",                     // 7
            "        public string ViewName;",                   // 8
            "        private bool _isDirty;",                    // 9
            "        private float _padding;",                   // 10
            "",                                                  // 11
            line12,                                              // 12 (文字51から capabilityUI)
            "",                                                  // 13
            "        public void Setup()",                       // 14
            "        {",                                         // 15
            "        }",                                         // 16
            "",                                                  // 17
            "        public void Render()",                      // 18
            "        {",                                         // 19
            "            int dummy = 0;",                        // 20
            "capabilityUI.ShowCapabilitiesUI();",                // 21 (文字3: capabilityUI, 文字18: ShowCapabilitiesUI)
            "        }",                                         // 22
            "    }",                                             // 23
            "}"                                                  // 24
        };
        File.WriteAllText(_capabilityUiPath, string.Join("\n", uiLines));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempWorkspaceDir))
            {
                Directory.Delete(_tempWorkspaceDir, true);
            }
        }
        catch
        {
            // ベストエフォートで削除
        }
    }

    [Fact]
    public void VerifyLineAndCharacterCoordinatesMatchDocSpec()
    {
        // doc/lsp-test.md の仕様通りの座標になっているかをまずアサート
        string[] uiLines = File.ReadAllLines(_capabilityUiPath);
        Assert.Equal("capabilityUI.ShowCapabilitiesUI();", uiLines[21]);

        int capUiDeclCol = uiLines[12].IndexOf("capabilityUI", StringComparison.Ordinal);
        Assert.Equal(51, capUiDeclCol);

        string[] ctrlLines = File.ReadAllLines(_controllerPath);
        int showMethodCol = ctrlLines[25].IndexOf("ShowCapabilitiesUI", StringComparison.Ordinal);
        Assert.Equal(20, showMethodCol);
    }

    [Fact]
    public void DocLspTest_Case1_SameFileFieldDefinitionJump()
    {
        using var outputStream = new MemoryStream();
        var server = new LspServer(outputStream);

        // 1. initialize
        string rootUri = new Uri(Path.GetFullPath(_tempWorkspaceDir)).AbsoluteUri;
        string initJson = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"processId\":null,\"rootUri\":\"{rootUri}\",\"capabilities\":{{}}}}}}";
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(initJson));

        // 2. initialized
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}"));

        // 3. didOpen (CapabilityUI.cs)
        string uiText = File.ReadAllText(_capabilityUiPath);
        string didOpen = MakeDidOpen(_capabilityUiUri, uiText);
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(didOpen));

        // 4. definition request (Line 21, Character 3: capabilityUI)
        outputStream.SetLength(0);
        string defReq = MakeDefinitionRequest(2, _capabilityUiUri, 21, 3);
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(defReq));

        var response = ParseLastResponse(outputStream);
        Assert.True(response.TryGetProperty("result", out var result), "Expected 'result' in response");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0, "Expected at least 1 definition location for capabilityUI");

        var loc = result[0];
        string uri = loc.GetProperty("uri").GetString()!;
        int startLine = loc.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        int startChar = loc.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32();

        Assert.Contains("CapabilityUI.cs", uri);
        Assert.Equal(12, startLine);
        Assert.Equal(51, startChar);
    }

    [Fact]
    public void DocLspTest_Case2_CrossFileMethodDefinitionJump()
    {
        using var outputStream = new MemoryStream();
        var server = new LspServer(outputStream);

        // 1. initialize
        string rootUri = new Uri(Path.GetFullPath(_tempWorkspaceDir)).AbsoluteUri;
        string initJson = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{{\"processId\":null,\"rootUri\":\"{rootUri}\",\"capabilities\":{{}}}}}}";
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(initJson));

        // 2. initialized
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}"));

        // 3. didOpen (CapabilityUI.cs)
        string uiText = File.ReadAllText(_capabilityUiPath);
        string didOpen = MakeDidOpen(_capabilityUiUri, uiText);
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(didOpen));

        // 4. definition request (Line 21, Character 18: ShowCapabilitiesUI)
        outputStream.SetLength(0);
        string defReq = MakeDefinitionRequest(3, _capabilityUiUri, 21, 18);
        server.HandleMessage(System.Text.Encoding.UTF8.GetBytes(defReq));

        var response = ParseLastResponse(outputStream);
        Assert.True(response.TryGetProperty("result", out var result), "Expected 'result' in response");
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.True(result.GetArrayLength() > 0, "Expected definition location for cross-file method ShowCapabilitiesUI");

        var loc = result[0];
        string uri = loc.GetProperty("uri").GetString()!;
        int startLine = loc.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        int startChar = loc.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32();

        Assert.Contains("CurrentCapabilitiesUIController.cs", uri);
        Assert.Equal(25, startLine);
        Assert.Equal(20, startChar);
    }

    [Fact]
    public void DocLspTest_RunPythonLspDefTestAgainstPublishedServer()
    {
        // artifacts/publish/UnityCSharpFastLSP.Server.exe を使って lsp_def_test.py が PASS することをE2E検証
        string serverExe = Path.GetFullPath("artifacts/publish/UnityCSharpFastLSP.Server.exe");
        if (!File.Exists(serverExe))
        {
            return; // publishバイナリが無い環境ではスキップ
        }

        string pythonScript = Path.GetFullPath("lsp_def_test.py");
        Assert.True(File.Exists(pythonScript), "lsp_def_test.py should exist");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "python",
            Arguments = $"\"{pythonScript}\" \"{serverExe}\" \"{_tempWorkspaceDir}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(proc);

        string stdOut = proc.StandardOutput.ReadToEnd();
        string stdErr = proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);

        Assert.Equal(0, proc.ExitCode);
        // lsp_def_test.py は "  [PASS] <名前>: ..." の形式で出力する
        Assert.Contains("[PASS] capabilityUI", stdOut);
        Assert.Contains("[PASS] ShowCapabilitiesUI", stdOut);
    }

    private static string MakeDidOpen(string uri, string text)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "textDocument/didOpen");
            writer.WriteStartObject("params");
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri);
            writer.WriteString("languageId", "csharp");
            writer.WriteNumber("version", 1);
            writer.WriteString("text", text);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MakeDefinitionRequest(int id, string uri, int line, int character)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteNumber("id", id);
            writer.WriteString("method", "textDocument/definition");
            writer.WriteStartObject("params");
            writer.WriteStartObject("textDocument");
            writer.WriteString("uri", uri);
            writer.WriteEndObject();
            writer.WriteStartObject("position");
            writer.WriteNumber("line", line);
            writer.WriteNumber("character", character);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static JsonElement ParseLastResponse(MemoryStream stream)
    {
        byte[] bytes = stream.ToArray();
        string raw = System.Text.Encoding.UTF8.GetString(bytes);

        string[] parts = raw.Split(new[] { "\r\n\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string part = parts[i].Trim();
            if (part.StartsWith("{") && part.EndsWith("}"))
            {
                using var doc = JsonDocument.Parse(part);
                return doc.RootElement.Clone();
            }
        }

        throw new InvalidOperationException("No JSON-RPC response found in output stream: " + raw);
    }
}
