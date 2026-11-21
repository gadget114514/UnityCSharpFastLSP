using System.Text;
using CsffParser = Csff.Parser.Parser;
using CsffSyntaxTree = Csff.Parser.SyntaxTree;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Syntax;

public readonly record struct DeclarationInfo(
    string Kind,
    string Name,
    int Line,
    int Character,
    string SourceText);

public static class CsffSyntaxAdapter
{
    private static readonly HashSet<int> DeclarationKinds =
    [
        (int)CsffNodeKind.NamespaceDeclaration,
        (int)CsffNodeKind.FileScopedNamespaceDeclaration,
        (int)CsffNodeKind.ClassDeclaration,
        (int)CsffNodeKind.StructDeclaration,
        (int)CsffNodeKind.InterfaceDeclaration,
        (int)CsffNodeKind.EnumDeclaration,
        (int)CsffNodeKind.FieldDeclaration,
        (int)CsffNodeKind.MethodDeclaration,
        (int)CsffNodeKind.ConstructorDeclaration,
        (int)CsffNodeKind.PropertyDeclaration,
        (int)CsffNodeKind.EnumMemberDeclaration,
    ];

    public static DocumentSnapshot Parse(
        string source,
        long version = 0,
        string[]? preprocessorSymbols = null,
        CsffSyntaxTree? tree = null)
    {
        tree ??= new CsffSyntaxTree();

        if (preprocessorSymbols is null || preprocessorSymbols.Length == 0)
        {
            CsffParser.parse(source, tree);
        }
        else
        {
            CsffParser.parseWith(preprocessorSymbols, source, tree);
        }

        int nodeCount = tree.NodeCount;
        int tokenCount = tree.TokenCount;
        var nodes = new SyntaxNodeInfo[nodeCount];
        var tokens = new SyntaxTokenInfo[tokenCount];

        for (int i = 0; i < nodeCount; i++)
        {
            var node = tree.Node(i);
            nodes[i] = new SyntaxNodeInfo(
                (int)node.Kind,
                node.Parent,
                node.ChildStart,
                node.ChildCount,
                node.TokenStart,
                node.TokenCount);
        }

        for (int i = 0; i < tokenCount; i++)
        {
            var token = tree.Token(i);
            tokens[i] = new SyntaxTokenInfo(
                (int)token.Kind,
                token.Start,
                token.Length,
                token.TriviaStart,
                token.LeadingCount,
                token.TrailingCount);
        }

        return new DocumentSnapshot(
            source,
            nodes,
            tokens,
            new LineMap(source),
            version,
            tree.IsComplete,
            tree.RecoveredTokens);
    }

    public static string DumpCst(DocumentSnapshot snapshot, int maxNodes = 400)
    {
        var sb = new StringBuilder();
        string source = snapshot.Source;
        int limit = Math.Min(snapshot.Nodes.Length, maxNodes);

        for (int i = 0; i < limit; i++)
        {
            var node = snapshot.Nodes[i];
            int depth = 0;
            int parent = node.Parent;

            while (parent >= 0 && parent < snapshot.Nodes.Length && depth < 64)
            {
                depth++;
                parent = snapshot.Nodes[parent].Parent;
            }

            var (start, length) = snapshot.SourceSpan(i);
            start = Math.Clamp(start, 0, source.Length);
            int available = source.Length - start;
            int take = Math.Clamp(Math.Min(length, available), 0, 90);
            string text = take > 0
                ? source.Substring(start, take).Replace("\r", "\\r").Replace("\n", "\\n")
                : string.Empty;
            string kindName = ((CsffNodeKind)node.Kind).ToString();

            sb.Append(' ', depth * 2)
              .Append(kindName)
              .Append(" [")
              .Append(start)
              .Append(",+")
              .Append(take)
              .Append("] ")
              .AppendLine(text);
        }

        if (snapshot.Nodes.Length > limit)
        {
            sb.Append("... (").Append(snapshot.Nodes.Length - limit).AppendLine(" more nodes)");
        }

        return sb.ToString();
    }

    public static List<DeclarationInfo> GetDeclarations(DocumentSnapshot snapshot)
    {
        var result = new List<DeclarationInfo>();

        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];

            if (!DeclarationKinds.Contains(node.Kind))
            {
                continue;
            }

            string name = FindIdentifier(snapshot, node, out int nameStart, out _);
            var (start, _) = snapshot.SourceSpan(i);
            var (line, character) = snapshot.Lines.GetPosition(start);
            string sourceText = nameStart >= 0 && nameStart < snapshot.Source.Length
                ? snapshot.Source.Substring(nameStart, Math.Min(40, snapshot.Source.Length - nameStart))
                    .Replace("\r", " ").Replace("\n", " ")
                : string.Empty;

            result.Add(new DeclarationInfo(
                ((CsffNodeKind)node.Kind).ToString(),
                name,
                line,
                character,
                sourceText));
        }

        return result;
    }

    public static int CountKind(DocumentSnapshot snapshot, int kind)
    {
        int count = 0;

        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            if (snapshot.Nodes[i].Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    public static int CountClassDeclarations(DocumentSnapshot snapshot) =>
        CountKind(snapshot, (int)CsffNodeKind.ClassDeclaration);

    public static int CountMethodDeclarations(DocumentSnapshot snapshot) =>
        CountKind(snapshot, (int)CsffNodeKind.MethodDeclaration);

    public static int CountIncompleteMembers(DocumentSnapshot snapshot) =>
        CountKind(snapshot, (int)CsffNodeKind.IncompleteMember);

    public static int CountBadTokens(DocumentSnapshot snapshot)
    {
        int target = (int)CsffTokenKind.BadToken;
        int count = 0;

        for (int i = 0; i < snapshot.Tokens.Length; i++)
        {
            if (snapshot.Tokens[i].Kind == target)
            {
                count++;
            }
        }

        return count;
    }

    public static string FindIdentifier(DocumentSnapshot snapshot, SyntaxNodeInfo node, out int start) =>
        FindIdentifier(snapshot, node, out start, out _);

    public static string FindIdentifier(DocumentSnapshot snapshot, SyntaxNodeInfo node, out int start, out int length)
    {
        start = -1;
        length = 0;
        int end = Math.Min(node.TokenStart + node.TokenCount, snapshot.Tokens.Length);
        bool typeLike = node.Kind is (int)CsffNodeKind.NamespaceDeclaration
            or (int)CsffNodeKind.FileScopedNamespaceDeclaration
            or (int)CsffNodeKind.ClassDeclaration
            or (int)CsffNodeKind.StructDeclaration
            or (int)CsffNodeKind.InterfaceDeclaration
            or (int)CsffNodeKind.EnumDeclaration;
        bool methodLike = node.Kind is (int)CsffNodeKind.MethodDeclaration
            or (int)CsffNodeKind.ConstructorDeclaration;
        bool memberLike = node.Kind is (int)CsffNodeKind.FieldDeclaration
            or (int)CsffNodeKind.PropertyDeclaration
            or (int)CsffNodeKind.EnumMemberDeclaration;
        bool enumMemberLike = node.Kind is (int)CsffNodeKind.EnumMemberDeclaration;
        bool delimiterFound = false;
        int selected = -1;

        for (int t = node.TokenStart; t < end; t++)
        {
            int kind = snapshot.Tokens[t].Kind;

            if (kind == (int)CsffTokenKind.IdentifierToken)
            {
                if ((methodLike || memberLike) && delimiterFound)
                {
                    break;
                }

                selected = t;

                if (typeLike || enumMemberLike)
                {
                    break;
                }

                continue;
            }

            if (!delimiterFound && IsNameDelimiter(kind, methodLike, memberLike))
            {
                delimiterFound = true;
            }
        }

        if (selected < 0)
        {
            return string.Empty;
        }

        var token = snapshot.Tokens[selected];
        start = token.Start;
        length = token.Length;

        if (start < 0 || start >= snapshot.Source.Length)
        {
            return string.Empty;
        }

        int available = snapshot.Source.Length - start;
        int take = Math.Min(token.Length, available);
        return take > 0 ? snapshot.Source.Substring(start, take) : string.Empty;
    }

    private static bool IsNameDelimiter(int kind, bool methodLike, bool memberLike)
    {
        if (methodLike)
        {
            return kind == (int)CsffTokenKind.OpenParenToken;
        }

        if (memberLike)
        {
            return kind is (int)CsffTokenKind.EqualsToken
                or (int)CsffTokenKind.SemicolonToken
                or (int)CsffTokenKind.OpenBraceToken
                or (int)CsffTokenKind.EqualsGreaterThanToken;
        }

        return false;
    }
}
