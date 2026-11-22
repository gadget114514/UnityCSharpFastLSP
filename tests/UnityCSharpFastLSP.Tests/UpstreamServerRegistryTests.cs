using UnityCSharpFastLSP.Server;
using Xunit;

namespace UnityCSharpFastLSP.Tests;

/// <summary>--proxy のフォールバック先（UnityCSharpLSP / csharp-ls / OmniSharp）の選択・探索ロジック。</summary>
public class UpstreamServerRegistryTests
{
    [Theory]
    [InlineData(null, UpstreamKind.Auto)]
    [InlineData("", UpstreamKind.Auto)]
    [InlineData("auto", UpstreamKind.Auto)]
    [InlineData("unitycsharp", UpstreamKind.UnityCSharp)]
    [InlineData("UnityCSharpLSP", UpstreamKind.UnityCSharp)]
    [InlineData("csharp-ls", UpstreamKind.CSharpLs)]
    [InlineData("csharpls", UpstreamKind.CSharpLs)]
    [InlineData("OmniSharp", UpstreamKind.OmniSharp)]
    public void ParseKind_AcceptsKnownNames(string? text, UpstreamKind expected)
    {
        Assert.Equal(expected, UpstreamServerRegistry.ParseKind(text));
    }

    [Fact]
    public void ParseKind_ThrowsForUnknownName()
    {
        Assert.Throws<ArgumentException>(() => UpstreamServerRegistry.ParseKind("clangd"));
    }

    [Theory]
    [InlineData(@"C:\tools\csharp-ls.exe", UpstreamKind.CSharpLs)]
    [InlineData("/home/me/.dotnet/tools/csharp-ls", UpstreamKind.CSharpLs)]
    [InlineData(@"D:\omnisharp\OmniSharp.exe", UpstreamKind.OmniSharp)]
    [InlineData("UnityCSharpLSP.Server.exe", UpstreamKind.UnityCSharp)]
    [InlineData(@"Z:\unknown\server.exe", UpstreamKind.Auto)]
    public void DetectKindFromPath_UsesExecutableName(string path, UpstreamKind expected)
    {
        Assert.Equal(expected, UpstreamServerRegistry.DetectKindFromPath(path));
    }

    [Fact]
    public void ResolveAttempts_ExplicitKind_ReturnsSingleAttempt()
    {
        var attempts = UpstreamServerRegistry.ResolveAttempts(UpstreamKind.CSharpLs, @"Z:\tool\csharp-ls.exe");

        Assert.Single(attempts);
        Assert.Equal(UpstreamKind.CSharpLs, attempts[0].Kind);
        Assert.Equal(@"Z:\tool\csharp-ls.exe", attempts[0].ExplicitPath);
    }

    [Fact]
    public void ResolveAttempts_Auto_IncludesAllThreeProviders()
    {
        var attempts = UpstreamServerRegistry.ResolveAttempts(UpstreamKind.Auto, null);

        Assert.Contains(attempts, a => a.Kind == UpstreamKind.UnityCSharp);
        Assert.Contains(attempts, a => a.Kind == UpstreamKind.CSharpLs);
        Assert.Contains(attempts, a => a.Kind == UpstreamKind.OmniSharp);
        Assert.Equal(attempts.Count, attempts.Select(a => a.Kind).Distinct().Count());
    }

    [Fact]
    public void ResolveAttempts_Auto_DetectedExplicitPathComesFirst()
    {
        var attempts = UpstreamServerRegistry.ResolveAttempts(UpstreamKind.Auto, @"Z:\tool\csharp-ls.exe");

        Assert.Equal(UpstreamKind.CSharpLs, attempts[0].Kind);
        Assert.Equal(@"Z:\tool\csharp-ls.exe", attempts[0].ExplicitPath);
    }

    [Fact]
    public void StartArguments_AreProviderSpecific()
    {
        Assert.Equal("--stdio", UpstreamServerRegistry.GetStartArguments(UpstreamKind.UnityCSharp, null));
        Assert.Equal(string.Empty, UpstreamServerRegistry.GetStartArguments(UpstreamKind.CSharpLs, null));

        string omnisharp = UpstreamServerRegistry.GetStartArguments(UpstreamKind.OmniSharp, @"D:\MyUnityProject");
        Assert.Contains("-lsp", omnisharp);
        Assert.Contains("-s \"D:\\\\MyUnityProject\"", omnisharp);
        Assert.DoesNotContain("-s", UpstreamServerRegistry.GetStartArguments(UpstreamKind.OmniSharp, null));
    }

    [Fact]
    public void CandidateCommands_StartWithExplicitPathAndIncludeProviderCommand()
    {
        var csharpLs = UpstreamServerRegistry.GetCandidateCommands(
            new UpstreamAttempt(UpstreamKind.CSharpLs, @"Z:\tool\custom-csharp-ls.exe"));
        Assert.Equal(@"Z:\tool\custom-csharp-ls.exe", csharpLs[0]);
        Assert.Contains("csharp-ls", csharpLs);

        var omnisharp = UpstreamServerRegistry.GetCandidateCommands(new UpstreamAttempt(UpstreamKind.OmniSharp, null));
        Assert.Contains("omnisharp", omnisharp);
        Assert.Contains(omnisharp, c => string.Equals(c, "OmniSharp.exe", StringComparison.OrdinalIgnoreCase));

        var unity = UpstreamServerRegistry.GetCandidateCommands(new UpstreamAttempt(UpstreamKind.UnityCSharp, null));
        Assert.Contains("UnityCSharpLSP.Server.exe", unity);
    }
}
