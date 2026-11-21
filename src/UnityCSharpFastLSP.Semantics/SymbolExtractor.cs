using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;
using CsffNodeKind = Csff.Lexer.NodeKind;

namespace UnityCSharpFastLSP.Semantics;

public static class SymbolExtractor
{
    public static List<Symbol> ExtractSymbols(string uri, DocumentSnapshot snapshot)
    {
        var symbols = new List<Symbol>();
        string normalizedUri = UriHelper.Normalize(uri);

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

            // 親クラス・インターフェース・列挙型名の取得
            string? containingType = null;
            int parent = node.Parent;
            while (parent >= 0)
            {
                var pNode = snapshot.Nodes[parent];
                if (pNode.Kind is (int)CsffNodeKind.ClassDeclaration
                    or (int)CsffNodeKind.StructDeclaration
                    or (int)CsffNodeKind.InterfaceDeclaration
                    or (int)CsffNodeKind.EnumDeclaration)
                {
                    containingType = CsffSyntaxAdapter.FindIdentifier(snapshot, pNode, out _, out _);
                    break;
                }
                parent = pNode.Parent;
            }

            // 基底クラス・インターフェースの抽出
            List<string>? baseTypes = null;
            if (kind.Value is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface)
            {
                baseTypes = ExtractBaseTypes(snapshot.Source, nameStart + nameLength, nodeStart + nodeLength);
            }

            string signature = $"{kind.Value} {name}";
            if (kind.Value is SymbolKind.Method or SymbolKind.Constructor)
            {
                int parenStart = snapshot.Source.IndexOf('(', nameStart);
                if (parenStart > nameStart && parenStart < nodeStart + nodeLength)
                {
                    int parenEnd = snapshot.Source.IndexOf(')', parenStart);
                    if (parenEnd > parenStart && parenEnd < nodeStart + nodeLength)
                    {
                        signature = snapshot.Source.Substring(nodeStart, parenEnd - nodeStart + 1).Trim();
                    }
                }
            }

            symbols.Add(new Symbol(
                Id: 0,
                NameId: 0,
                Name: name,
                Kind: kind.Value,
                Uri: normalizedUri,
                Range: range,
                Signature: signature,
                ContainingType: containingType,
                IsStatic: false,
                BaseTypes: baseTypes
            ));
        }

        return symbols;
    }

    private static List<string>? ExtractBaseTypes(string source, int startSearch, int maxSearch)
    {
        int colonIdx = -1;
        int limit = Math.Min(source.Length, maxSearch);
        for (int p = startSearch; p < limit; p++)
        {
            char c = source[p];
            if (c == ':')
            {
                colonIdx = p;
                break;
            }
            if (c == '{' || c == ';') break;
        }

        if (colonIdx < 0) return null;

        var baseTypes = new List<string>();
        int end = colonIdx + 1;
        while (end < limit && source[end] != '{' && source[end] != ';') end++;

        string baseListText = source.Substring(colonIdx + 1, end - colonIdx - 1);
        string[] parts = baseListText.Split(',');
        foreach (var raw in parts)
        {
            string trimmed = raw.Trim();
            // 型引数 <T> を除去
            int genericIdx = trimmed.IndexOf('<');
            if (genericIdx > 0)
            {
                trimmed = trimmed.Substring(0, genericIdx).Trim();
            }
            if (!string.IsNullOrEmpty(trimmed) && char.IsLetter(trimmed[0]))
            {
                baseTypes.Add(trimmed);
            }
        }

        return baseTypes.Count > 0 ? baseTypes : null;
    }

    private static SymbolKind? MapToSymbolKind(int kind) =>
        (CsffNodeKind)kind switch
        {
            CsffNodeKind.ClassDeclaration => SymbolKind.Class,
            CsffNodeKind.StructDeclaration => SymbolKind.Struct,
            CsffNodeKind.InterfaceDeclaration => SymbolKind.Interface,
            CsffNodeKind.EnumDeclaration => SymbolKind.Enum,
            CsffNodeKind.EnumMemberDeclaration => SymbolKind.EnumMember,
            CsffNodeKind.MethodDeclaration => SymbolKind.Method,
            CsffNodeKind.ConstructorDeclaration => SymbolKind.Constructor,
            CsffNodeKind.PropertyDeclaration => SymbolKind.Property,
            CsffNodeKind.FieldDeclaration => SymbolKind.Field,
            _ => null,
        };
}
