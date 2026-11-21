using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;
using CsffNodeKind = Csff.Lexer.NodeKind;

namespace UnityCSharpFastLSP.Semantics;

public static class SymbolExtractor
{
    public static List<Symbol> ExtractSymbols(string uri, DocumentSnapshot snapshot)
    {
        var symbols = new List<Symbol>();

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
            var (startLine, startCol) = snapshot.Lines.GetPosition(nameStart >= 0 ? nameStart : nodeStart);
            var (endLine, endCol) = snapshot.Lines.GetPosition(nameStart >= 0 ? nameStart + nameLength : nodeStart + nodeLength);

            var range = new Protocol.Range(
                new Position(startLine, startCol),
                new Position(endLine, endCol)
            );

            // 親クラス名の取得
            string? containingType = null;
            int parent = node.Parent;
            while (parent >= 0)
            {
                var pNode = snapshot.Nodes[parent];
                if (pNode.Kind is (int)CsffNodeKind.ClassDeclaration
                    or (int)CsffNodeKind.StructDeclaration
                    or (int)CsffNodeKind.InterfaceDeclaration)
                {
                    containingType = CsffSyntaxAdapter.FindIdentifier(snapshot, pNode, out _, out _);
                    break;
                }
                parent = pNode.Parent;
            }

            symbols.Add(new Symbol(
                Id: 0,
                NameId: 0,
                Name: name,
                Kind: kind.Value,
                Uri: uri,
                Range: range,
                Signature: $"{kind.Value} {name}",
                ContainingType: containingType,
                IsStatic: false
            ));
        }

        return symbols;
    }

    private static SymbolKind? MapToSymbolKind(int kind) =>
        (CsffNodeKind)kind switch
        {
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
