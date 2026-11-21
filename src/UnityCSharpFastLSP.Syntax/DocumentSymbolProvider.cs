using UnityCSharpFastLSP.Protocol;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Syntax;

public static class DocumentSymbolProvider
{
    public static List<DocumentSymbol> GetDocumentSymbols(DocumentSnapshot snapshot)
    {
        var roots = new List<DocumentSymbol>();
        var declarations = new List<(int NodeIndex, DocumentSymbol Symbol)>();

        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];
            SymbolKind? kind = MapToSymbolKind(node.Kind);
            if (!kind.HasValue)
            {
                continue;
            }

            string name = CsffSyntaxAdapter.FindIdentifier(snapshot, node, out int nameStart, out int nameLength);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var (nodeStart, nodeLength) = snapshot.SourceSpan(i);
            var (startLine, startCol) = snapshot.Lines.GetPosition(nodeStart);
            var (endLine, endCol) = snapshot.Lines.GetPosition(nodeStart + nodeLength);

            var (selStartLine, selStartCol) = snapshot.Lines.GetPosition(nameStart >= 0 ? nameStart : nodeStart);
            var (selEndLine, selEndCol) = snapshot.Lines.GetPosition(nameStart >= 0 ? nameStart + nameLength : nodeStart + nodeLength);

            var range = new Protocol.Range(
                new Position(startLine, startCol),
                new Position(endLine, endCol)
            );
            var selectionRange = new Protocol.Range(
                new Position(selStartLine, selStartCol),
                new Position(selEndLine, selEndCol)
            );

            var docSymbol = new DocumentSymbol(
                Name: name,
                Detail: null,
                Kind: kind.Value,
                Range: range,
                SelectionRange: selectionRange,
                Children: new List<DocumentSymbol>()
            );

            declarations.Add((i, docSymbol));
        }

        // 包含関係に基づきツリーを構築
        for (int i = 0; i < declarations.Count; i++)
        {
            int parentNodeIdx = snapshot.Nodes[declarations[i].NodeIndex].Parent;
            int parentSymbolIdx = -1;

            int p = parentNodeIdx;
            while (p >= 0)
            {
                for (int j = 0; j < declarations.Count; j++)
                {
                    if (declarations[j].NodeIndex == p)
                    {
                        parentSymbolIdx = j;
                        break;
                    }
                }
                if (parentSymbolIdx >= 0)
                {
                    break;
                }
                p = snapshot.Nodes[p].Parent;
            }

            if (parentSymbolIdx >= 0)
            {
                declarations[parentSymbolIdx].Symbol.Children?.Add(declarations[i].Symbol);
            }
            else
            {
                roots.Add(declarations[i].Symbol);
            }
        }

        return roots;
    }

    private static SymbolKind? MapToSymbolKind(int kind) =>
        (CsffNodeKind)kind switch
        {
            CsffNodeKind.NamespaceDeclaration => SymbolKind.Namespace,
            CsffNodeKind.FileScopedNamespaceDeclaration => SymbolKind.Namespace,
            CsffNodeKind.ClassDeclaration => SymbolKind.Class,
            CsffNodeKind.StructDeclaration => SymbolKind.Struct,
            CsffNodeKind.InterfaceDeclaration => SymbolKind.Interface,
            CsffNodeKind.EnumDeclaration => SymbolKind.Enum,
            CsffNodeKind.MethodDeclaration => SymbolKind.Method,
            CsffNodeKind.ConstructorDeclaration => SymbolKind.Constructor,
            CsffNodeKind.PropertyDeclaration => SymbolKind.Property,
            CsffNodeKind.FieldDeclaration => SymbolKind.Field,
            _ => null,
        };
}
