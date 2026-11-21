using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public static class DocumentHighlightProvider
{
    public static List<DocumentHighlight> GetHighlights(DocumentSemanticModel model, Position position)
    {
        var highlights = new List<DocumentHighlight>();
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word)) return highlights;

        var snapshot = model.Snapshot;
        string source = snapshot.Source;
        int wordLen = word.Length;
        int idx = 0;

        while ((idx = source.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            // 単語境界の確認
            bool leftBoundary = idx == 0 || !(char.IsLetterOrDigit(source[idx - 1]) || source[idx - 1] == '_');
            bool rightBoundary = (idx + wordLen == source.Length) || !(char.IsLetterOrDigit(source[idx + wordLen]) || source[idx + wordLen] == '_');

            if (leftBoundary && rightBoundary)
            {
                var (startLine, startCol) = snapshot.Lines.GetPosition(idx);
                var (endLine, endCol) = snapshot.Lines.GetPosition(idx + wordLen);

                highlights.Add(new DocumentHighlight(
                    Range: new Protocol.Range(
                        new Position(startLine, startCol),
                        new Position(endLine, endCol)
                    ),
                    Kind: DocumentHighlightKind.Text
                ));
            }

            idx += wordLen;
        }

        return highlights;
    }
}
