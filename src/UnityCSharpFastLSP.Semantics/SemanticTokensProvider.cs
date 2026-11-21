using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Semantics;

public static class SemanticTokensProvider
{
    public static readonly string[] TokenTypes =
    [
        "class",
        "struct",
        "interface",
        "enum",
        "method",
        "property",
        "variable",
        "parameter",
        "keyword",
        "string",
        "number",
        "comment"
    ];

    public static readonly string[] TokenModifiers =
    [
        "declaration",
        "static",
        "readonly"
    ];

    public static readonly SemanticTokensLegend Legend = new(TokenTypes, TokenModifiers);

    public static SemanticTokens GetSemanticTokens(DocumentSnapshot snapshot)
    {
        var data = new List<int>();
        int prevLine = 0;
        int prevChar = 0;
        string source = snapshot.Source;

        for (int i = 0; i < snapshot.Tokens.Length; i++)
        {
            var token = snapshot.Tokens[i];
            if (token.Start < 0 || token.Length <= 0 || token.Start + token.Length > source.Length)
            {
                continue;
            }

            int? tokenType = DetermineTokenType(snapshot, i, out int modifiers);
            if (!tokenType.HasValue)
            {
                continue;
            }

            var (line, col) = snapshot.Lines.GetPosition(token.Start);
            int deltaLine = line - prevLine;
            int deltaChar = deltaLine == 0 ? col - prevChar : col;

            data.Add(deltaLine);
            data.Add(deltaChar);
            data.Add(token.Length);
            data.Add(tokenType.Value);
            data.Add(modifiers);

            prevLine = line;
            prevChar = col;
        }

        return new SemanticTokens(ResultId: null, Data: data);
    }

    private static int? DetermineTokenType(DocumentSnapshot snapshot, int tokenIdx, out int modifiers)
    {
        modifiers = 0;
        var token = snapshot.Tokens[tokenIdx];
        string text = snapshot.Source.Substring(token.Start, token.Length);

        // キーワード判定
        if (IsKeyword(text))
        {
            return 8; // keyword
        }

        // 文字列・数値判定
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\''))
        {
            return 9; // string
        }
        if (char.IsDigit(text[0]))
        {
            return 10; // number
        }

        // コメント判定
        if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("/*", StringComparison.Ordinal))
        {
            return 11; // comment
        }

        // 識別子判定
        if (token.Kind == (int)CsffTokenKind.IdentifierToken)
        {
            int nodeIdx = snapshot.FindNodeAt(token.Start);
            if (nodeIdx >= 0)
            {
                var node = snapshot.Nodes[nodeIdx];
                return (CsffNodeKind)node.Kind switch
                {
                    CsffNodeKind.ClassDeclaration => 0, // class
                    CsffNodeKind.StructDeclaration => 1, // struct
                    CsffNodeKind.InterfaceDeclaration => 2, // interface
                    CsffNodeKind.EnumDeclaration => 3, // enum
                    CsffNodeKind.MethodDeclaration or CsffNodeKind.ConstructorDeclaration => 4, // method
                    CsffNodeKind.PropertyDeclaration => 5, // property
                    CsffNodeKind.FieldDeclaration => 6, // variable
                    _ => 6 // variable
                };
            }

            return 6; // variable
        }

        return null;
    }

    private static bool IsKeyword(string word) =>
        word switch
        {
            "public" or "private" or "protected" or "internal" or "static" or "readonly"
            or "class" or "struct" or "interface" or "enum" or "namespace" or "using"
            or "void" or "int" or "float" or "bool" or "string" or "double" or "var"
            or "return" or "if" or "else" or "while" or "for" or "foreach" or "in"
            or "switch" or "case" or "default" or "break" or "continue" or "new"
            or "null" or "true" or "false" or "this" or "base" or "override" or "virtual"
            or "async" or "await" or "try" or "catch" or "finally" or "throw" => true,
            _ => false
        };
}
