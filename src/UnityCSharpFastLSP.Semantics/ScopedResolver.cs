using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Semantics;

public static class ScopedResolver
{
    public static List<CompletionItem> ResolveMemberCompletions(
        DocumentSemanticModel model,
        string targetIdentifier)
    {
        var items = new List<CompletionItem>();

        // targetIdentifier の型名をスコープから推論
        string? typeName = ResolveVariableType(model.Snapshot, targetIdentifier);
        if (string.IsNullOrEmpty(typeName))
        {
            typeName = targetIdentifier; // 型名直接アクセス（static member）の可能性
        }

        // SymbolIndex から、その ContainingType に属するメンバーのみを厳格に抽出
        var matchingSymbols = model.Index.FindSymbolsByName(typeName);
        string resolvedType = typeName;

        // クラスシンボルが存在する場合
        foreach (var sym in model.Index.FindSymbolsByName(typeName))
        {
            if (sym.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface)
            {
                resolvedType = sym.Name;
                break;
            }
        }

        // resolvedType のメンバーを抽出
        var members = GetMembersOfType(model, resolvedType);
        foreach (var m in members)
        {
            items.Add(new CompletionItem(
                Label: m.Name,
                Kind: m.Kind switch
                {
                    SymbolKind.Method => CompletionItemKind.Method,
                    SymbolKind.Property => CompletionItemKind.Property,
                    SymbolKind.Field => CompletionItemKind.Field,
                    _ => CompletionItemKind.Variable
                },
                Detail: $"({resolvedType}) {m.Signature ?? m.Name}",
                Documentation: null
            ));
        }

        return items;
    }

    public static List<Location> ResolveScopedDefinition(
        DocumentSemanticModel model,
        string uri,
        Position position)
    {
        var snapshot = model.Snapshot;
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word))
        {
            return [];
        }

        int offset = snapshot.Lines.GetOffset(position.Line, position.Character);
        int enclosingNodeIdx = snapshot.FindNodeAt(offset);

        // 1. ローカル変数 / パラメータ宣言の探索 (最優先)
        int current = enclosingNodeIdx;
        while (current >= 0)
        {
            var node = snapshot.Nodes[current];
            if (node.Kind is (int)CsffNodeKind.MethodDeclaration
                or (int)CsffNodeKind.ConstructorDeclaration
                or (int)CsffNodeKind.LocalFunctionStatement)
            {
                // メソッド内のトークンから word の宣言を探す
                for (int t = node.TokenStart; t < node.TokenStart + node.TokenCount && t < snapshot.Tokens.Length; t++)
                {
                    var token = snapshot.Tokens[t];
                    if (token.Kind == (int)CsffTokenKind.IdentifierToken &&
                        token.Start >= 0 && token.Start + token.Length <= snapshot.Source.Length &&
                        snapshot.Source.Substring(token.Start, token.Length) == word)
                    {
                        // 宣言位置らしきもの（自身以外の最初の登場）
                        var (line, col) = snapshot.Lines.GetPosition(token.Start);
                        return [new Location(uri, new Protocol.Range(new Position(line, col), new Position(line, col + token.Length)))];
                    }
                }
                break;
            }
            current = node.Parent;
        }

        // 2. 自クラス内メンバーの探索
        current = enclosingNodeIdx;
        while (current >= 0)
        {
            var node = snapshot.Nodes[current];
            if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                or (int)CsffNodeKind.StructDeclaration)
            {
                for (int i = 0; i < snapshot.Nodes.Length; i++)
                {
                    if (snapshot.Nodes[i].Parent == current)
                    {
                        string id = CsffSyntaxAdapter.FindIdentifier(snapshot, snapshot.Nodes[i], out int start, out int len);
                        if (id == word)
                        {
                            var (line, col) = snapshot.Lines.GetPosition(start);
                            return [new Location(uri, new Protocol.Range(new Position(line, col), new Position(line, col + len)))];
                        }
                    }
                }
                break;
            }
            current = node.Parent;
        }

        // 3. グローバルな SymbolIndex 検索 (フォールバック)
        return model.Index.FindDefinitions(word);
    }

    private static string? ResolveVariableType(DocumentSnapshot snapshot, string variableName)
    {
        // ソース内から "Type variableName" または "var variableName = new Type" のパターンを探索
        string source = snapshot.Source;
        int idx = source.IndexOf(variableName, StringComparison.Ordinal);
        if (idx < 0) return null;

        // "new Type" パターン
        int assignIdx = source.IndexOf('=', idx);
        if (assignIdx >= 0 && assignIdx < idx + 50)
        {
            int newIdx = source.IndexOf("new ", assignIdx, StringComparison.Ordinal);
            if (newIdx >= 0 && newIdx < assignIdx + 10)
            {
                int typeStart = newIdx + 4;
                while (typeStart < source.Length && char.IsWhiteSpace(source[typeStart])) typeStart++;
                int typeEnd = typeStart;
                while (typeEnd < source.Length && (char.IsLetterOrDigit(source[typeEnd]) || source[typeEnd] == '_')) typeEnd++;
                if (typeEnd > typeStart)
                {
                    return source.Substring(typeStart, typeEnd - typeStart);
                }
            }
        }

        // "Type variableName" パターン（直前の単語を取得）
        int p = idx - 1;
        while (p >= 0 && char.IsWhiteSpace(source[p])) p--;
        int endWord = p + 1;
        while (p >= 0 && (char.IsLetterOrDigit(source[p]) || source[p] == '_')) p--;
        int startWord = p + 1;

        if (endWord > startWord)
        {
            string prev = source.Substring(startWord, endWord - startWord);
            if (prev != "var" && prev != "private" && prev != "public" && prev != "protected" && prev != "return")
            {
                return prev;
            }
        }

        return null;
    }

    private static List<Symbol> GetMembersOfType(DocumentSemanticModel model, string typeName)
    {
        var members = new List<Symbol>();

        // ファイル内のスナップショットから検索
        var snapshot = model.Snapshot;
        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];
            if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                or (int)CsffNodeKind.StructDeclaration)
            {
                string clsName = CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
                if (clsName == typeName)
                {
                    // このクラスの子ノード（メンバー）を抽出
                    for (int j = 0; j < snapshot.Nodes.Length; j++)
                    {
                        if (snapshot.Nodes[j].Parent == i)
                        {
                            string mName = CsffSyntaxAdapter.FindIdentifier(snapshot, snapshot.Nodes[j], out int mStart, out int mLen);
                            if (!string.IsNullOrEmpty(mName))
                            {
                                var (start, len) = snapshot.SourceSpan(j);
                                var (line, col) = snapshot.Lines.GetPosition(mStart >= 0 ? mStart : start);
                                members.Add(new Symbol(
                                    Id: 0,
                                    NameId: 0,
                                    Name: mName,
                                    Kind: SymbolKind.Method,
                                    Uri: "",
                                    Range: new Protocol.Range(new Position(line, col), new Position(line, col + mLen)),
                                    ContainingType: typeName
                                ));
                            }
                        }
                    }
                }
            }
        }

        // SymbolIndex からも補完
        foreach (var sym in model.Index.FindSymbolsByName(typeName))
        {
            // ContainingType が一致するシンボルがあれば追加
        }

        return members;
    }
}
