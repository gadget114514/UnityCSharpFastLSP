using CsffNodeKind = Csff.Lexer.NodeKind;

namespace UnityCSharpFastLSP.Syntax;

public static class IncrementalParser
{
    private static readonly HashSet<int> MethodLevelKinds =
    [
        (int)CsffNodeKind.MethodDeclaration,
        (int)CsffNodeKind.ConstructorDeclaration,
        (int)CsffNodeKind.Block,
        (int)CsffNodeKind.LocalFunctionStatement,
    ];

    public static DocumentSnapshot ApplyChange(
        DocumentSnapshot previous,
        int changeStart,
        int changeLength,
        string newText,
        long newVersion,
        string[]? preprocessorSymbols = null)
    {
        string oldSource = previous.Source;
        int safeChangeStart = Math.Clamp(changeStart, 0, oldSource.Length);
        int safeChangeLength = Math.Clamp(changeLength, 0, oldSource.Length - safeChangeStart);

        string newSource = string.Concat(
            oldSource.AsSpan(0, safeChangeStart),
            newText,
            oldSource.AsSpan(safeChangeStart + safeChangeLength)
        );

        int delta = newText.Length - safeChangeLength;

        // 包含ノードの特定
        int enclosingNodeIndex = previous.FindSmallestEnclosingNode(safeChangeStart, safeChangeLength);
        if (enclosingNodeIndex >= 0)
        {
            var node = previous.Nodes[enclosingNodeIndex];
            // メソッドまたはブロック内の編集であれば、インクリメンタル高速オフセットシフト適用
            if (MethodLevelKinds.Contains(node.Kind) && !ContainsStructureBreakingKeywords(newText))
            {
                var shiftedTokens = new SyntaxTokenInfo[previous.Tokens.Length];
                for (int i = 0; i < previous.Tokens.Length; i++)
                {
                    var token = previous.Tokens[i];
                    if (token.Start >= safeChangeStart + safeChangeLength)
                    {
                        shiftedTokens[i] = new SyntaxTokenInfo(
                            token.Kind,
                            token.Start + delta,
                            token.Length,
                            token.TriviaStart,
                            token.LeadingCount,
                            token.TrailingCount);
                    }
                    else
                    {
                        shiftedTokens[i] = token;
                    }
                }

                // ノードはそのまま再利用し、LineMap を新ソースから再生成
                return new DocumentSnapshot(
                    newSource,
                    previous.Nodes,
                    shiftedTokens,
                    new LineMap(newSource),
                    newVersion,
                    previous.IsComplete,
                    previous.RecoveredTokens);
            }
        }

        // 構造変更またはトップレベル変更時は全文再パースにフォールバック
        return CsffSyntaxAdapter.Parse(newSource, newVersion, preprocessorSymbols);
    }

    private static bool ContainsStructureBreakingKeywords(string text)
    {
        // クラス定義、名前空間、インターフェース、構造体の追加や大幅な構造破壊の兆候
        return text.Contains("class ") ||
               text.Contains("struct ") ||
               text.Contains("namespace ") ||
               text.Contains("interface ") ||
               text.Contains("enum ") ||
               text.Contains("#if") ||
               text.Contains("#endif");
    }
}
