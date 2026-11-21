namespace UnityCSharpFastLSP.Syntax;

public sealed class ParseSession
{
    private readonly Csff.Parser.SyntaxTree _tree = new();

    public DocumentSnapshot Parse(string source, long version = 0, string[]? preprocessorSymbols = null) =>
        CsffSyntaxAdapter.Parse(source, version, preprocessorSymbols, _tree);
}
