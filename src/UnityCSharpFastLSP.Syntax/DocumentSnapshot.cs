namespace UnityCSharpFastLSP.Syntax;

public readonly record struct SyntaxNodeInfo(
    int Kind,
    int Parent,
    int ChildStart,
    int ChildCount,
    int TokenStart,
    int TokenCount);

public readonly record struct SyntaxTokenInfo(
    int Kind,
    int Start,
    int Length,
    int TriviaStart,
    int LeadingCount,
    int TrailingCount);

public sealed class DocumentSnapshot
{
    public DocumentSnapshot(
        string source,
        SyntaxNodeInfo[] nodes,
        SyntaxTokenInfo[] tokens,
        LineMap lines,
        long version,
        bool isComplete,
        int recoveredTokens)
    {
        Source = source;
        Nodes = nodes;
        Tokens = tokens;
        Lines = lines;
        Version = version;
        IsComplete = isComplete;
        RecoveredTokens = recoveredTokens;
    }

    public string Source { get; }

    public SyntaxNodeInfo[] Nodes { get; }

    public SyntaxTokenInfo[] Tokens { get; }

    public LineMap Lines { get; }

    public long Version { get; }

    public bool IsComplete { get; }

    public int RecoveredTokens { get; }

    public (int Start, int Length) SourceSpan(int nodeIndex)
    {
        var node = Nodes[nodeIndex];

        if (node.TokenCount > 0 && node.TokenStart < Tokens.Length)
        {
            int first = Tokens[node.TokenStart].Start;
            int lastIndex = Math.Min(node.TokenStart + node.TokenCount - 1, Tokens.Length - 1);
            int last = Tokens[lastIndex].Start + Tokens[lastIndex].Length;
            return (first, last - first);
        }

        if (node.TokenStart < Tokens.Length)
        {
            return (Tokens[node.TokenStart].Start, 0);
        }

        return Tokens.Length > 0 ? (Tokens[^1].Start + Tokens[^1].Length, 0) : (0, 0);
    }

    public int FindNodeAt(int offset)
    {
        int best = -1;
        int bestLength = int.MaxValue;

        for (int i = 0; i < Nodes.Length; i++)
        {
            var (start, len) = SourceSpan(i);
            if (offset >= start && offset <= start + len)
            {
                if (len < bestLength)
                {
                    best = i;
                    bestLength = len;
                }
            }
        }

        return best;
    }

    public int FindSmallestEnclosingNode(int startOffset, int length)
    {
        int endOffset = startOffset + length;
        int best = -1;
        int bestLength = int.MaxValue;

        for (int i = 0; i < Nodes.Length; i++)
        {
            var (start, len) = SourceSpan(i);
            if (start <= startOffset && start + len >= endOffset)
            {
                if (len < bestLength)
                {
                    best = i;
                    bestLength = len;
                }
            }
        }

        return best;
    }
}
