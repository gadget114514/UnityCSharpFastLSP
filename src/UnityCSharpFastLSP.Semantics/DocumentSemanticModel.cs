using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;

namespace UnityCSharpFastLSP.Semantics;

public sealed class DocumentSemanticModel
{
    private readonly DocumentSnapshot _snapshot;
    private readonly SymbolIndex _index;

    public DocumentSemanticModel(DocumentSnapshot snapshot, SymbolIndex index)
    {
        _snapshot = snapshot;
        _index = index;
    }

    public DocumentSnapshot Snapshot => _snapshot;
    public SymbolIndex Index => _index;

    public string? GetWordAtPosition(Position position)
    {
        var span = GetWordSpanAtPosition(position);
        return span?.Word;
    }

    public (string Word, int Start, int Length)? GetWordSpanAtPosition(Position position)
    {
        int offset = _snapshot.Lines.GetOffset(position.Line, position.Character);
        string source = _snapshot.Source;
        if (offset < 0 || offset > source.Length)
        {
            return null;
        }

        // カーソルが単語の直後（末尾）にある場合は1文字戻して識別子を検出
        if (offset > 0 && (offset == source.Length || !IsIdentifierChar(source[offset])) && IsIdentifierChar(source[offset - 1]))
        {
            offset--;
        }

        if (offset < 0 || offset >= source.Length || !IsIdentifierChar(source[offset]))
        {
            return null;
        }

        int start = offset;
        while (start > 0 && IsIdentifierChar(source[start - 1]))
        {
            start--;
        }

        int end = offset;
        while (end < source.Length && IsIdentifierChar(source[end]))
        {
            end++;
        }

        if (end > start)
        {
            return (source.Substring(start, end - start), start, end - start);
        }

        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
