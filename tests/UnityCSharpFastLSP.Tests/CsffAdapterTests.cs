using UnityCSharpFastLSP.Syntax;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

public class CsffAdapterTests
{
    private const string UnityScript = """
        using UnityEngine;

        // 日本語 😀 comment
        public class PlayerController : MonoBehaviour
        {
            [SerializeField] private int _hp;

        #if UNITY_EDITOR
            void OnEditorTick() { _hp++; }
        #endif

            void Start() { _hp = 10; }
            public int Hp => _hp;
        }
        """;

    [Fact]
    public void ParseProducesSnapshotWithNodesAndTokens()
    {
        var snapshot = CsffSyntaxAdapter.Parse(UnityScript);

        Assert.True(snapshot.Nodes.Length > 0);
        Assert.True(snapshot.Tokens.Length > 0);
        Assert.True(snapshot.IsComplete);
        Assert.Equal(UnityScript, snapshot.Source);
    }

    [Fact]
    public void DeclarationsIncludeClassMethodsAndProperty()
    {
        var snapshot = CsffSyntaxAdapter.Parse(UnityScript);
        var declarations = CsffSyntaxAdapter.GetDeclarations(snapshot);

        Assert.Contains(declarations, d => d.Kind == "ClassDeclaration" && d.Name == "PlayerController");
        Assert.Contains(declarations, d => d.Kind == "MethodDeclaration" && d.Name == "Start");
        Assert.Contains(declarations, d => d.Kind == "PropertyDeclaration" && d.Name == "Hp");
        Assert.Contains(declarations, d => d.Kind == "FieldDeclaration" && d.Name == "_hp");
    }

    [Fact]
    public void Utf16OffsetsSurviveJapaneseAndEmoji()
    {
        var snapshot = CsffSyntaxAdapter.Parse(UnityScript);
        int expected = UnityScript.IndexOf("public class PlayerController", StringComparison.Ordinal);
        int classNode = Array.FindIndex(snapshot.Nodes, n => n.Kind == 8855);
        var (start, _) = snapshot.SourceSpan(classNode);
        var (line, character) = snapshot.Lines.GetPosition(expected);

        Assert.Equal(expected, start);
        Assert.Equal(expected, snapshot.Lines.GetOffset(line, character));
        Assert.Equal("public class PlayerController", UnityScript.Substring(start, 29));
    }

    [Fact]
    public void PreprocessorSymbolChangesCst()
    {
        var without = CsffSyntaxAdapter.Parse(UnityScript);
        var with = CsffSyntaxAdapter.Parse(UnityScript, preprocessorSymbols: ["UNITY_EDITOR"]);

        Assert.True(CsffSyntaxAdapter.CountMethodDeclarations(with) > CsffSyntaxAdapter.CountMethodDeclarations(without));
        Assert.Contains(CsffSyntaxAdapter.GetDeclarations(with), d => d.Name == "OnEditorTick");
    }

    [Fact]
    public void IncompleteSourceProducesErrorSignals()
    {
        var snapshot = CsffSyntaxAdapter.Parse("public class Broken { void M(");

        Assert.False(snapshot.IsComplete);
        Assert.NotEmpty(snapshot.Nodes);
        Assert.NotEmpty(snapshot.Tokens);
    }
}
