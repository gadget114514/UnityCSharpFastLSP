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
        int offset = _snapshot.Lines.GetOffset(position.Line, position.Character);
        if (offset < 0 || offset >= _snapshot.Source.Length)
        {
            return null;
        }

        string source = _snapshot.Source;
        int start = offset;
        while (start > 0 && (char.IsLetterOrDigit(source[start - 1]) || source[start - 1] == '_'))
        {
            start--;
        }

        int end = offset;
        while (end < source.Length && (char.IsLetterOrDigit(source[end]) || source[end] == '_'))
        {
            end++;
        }

        if (end > start)
        {
            return source.Substring(start, end - start);
        }

        return null;
    }
}
