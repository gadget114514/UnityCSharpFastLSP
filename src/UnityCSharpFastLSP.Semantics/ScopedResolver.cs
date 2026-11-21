using System.Text.RegularExpressions;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Semantics;

public static class ScopedResolver
{
    public static List<CompletionItem> ResolveMemberCompletions(
        DocumentSemanticModel model,
        string targetIdentifier,
        Position? position = null)
    {
        var items = new List<CompletionItem>();

        int cursorOffset = position.HasValue
            ? model.Snapshot.Lines.GetOffset(position.Value.Line, position.Value.Character)
            : -1;

        // targetIdentifier の型名をスコープや構文から推論
        string? typeName = ResolveVariableType(model.Snapshot, targetIdentifier, cursorOffset);
        if (string.IsNullOrEmpty(typeName))
        {
            typeName = targetIdentifier; // 型名直接アクセス（static member または未解決）
        }

        // SymbolIndex から、その型名（クラス/構造体/インターフェース）の宣言を探索
        string resolvedType = typeName;
        foreach (var sym in model.Index.FindSymbolsByName(typeName))
        {
            if (sym.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum)
            {
                resolvedType = sym.Name;
                break;
            }
        }

        // resolvedType のメンバーを抽出
        var members = GetMembersOfType(model, resolvedType);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var m in members)
        {
            if (!seen.Add(m.Name))
            {
                continue;
            }

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

    public static string? ResolveVariableType(DocumentSnapshot snapshot, string variableName, int cursorOffset = -1)
    {
        if (variableName == "this")
        {
            if (cursorOffset >= 0)
            {
                int nodeIdx = snapshot.FindNodeAt(cursorOffset);
                while (nodeIdx >= 0)
                {
                    var node = snapshot.Nodes[nodeIdx];
                    if (node.Kind is (int)CsffNodeKind.ClassDeclaration or (int)CsffNodeKind.StructDeclaration)
                    {
                        return CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
                    }
                    nodeIdx = node.Parent;
                }
            }
            return null;
        }

        string source = snapshot.Source;

        // 1. カーソル位置がメソッド内なら、そのメソッドのローカルスコープを優先探索
        if (cursorOffset >= 0)
        {
            int enclosingNode = snapshot.FindNodeAt(cursorOffset);
            while (enclosingNode >= 0)
            {
                var node = snapshot.Nodes[enclosingNode];
                if (node.Kind is (int)CsffNodeKind.MethodDeclaration
                    or (int)CsffNodeKind.ConstructorDeclaration
                    or (int)CsffNodeKind.LocalFunctionStatement
                    or (int)CsffNodeKind.PropertyDeclaration)
                {
                    var (mStart, mLen) = snapshot.SourceSpan(enclosingNode);
                    if (mStart >= 0 && mStart + mLen <= source.Length)
                    {
                        string methodText = source.Substring(mStart, mLen);
                        string? localType = FindTypeInText(methodText, variableName);
                        if (!string.IsNullOrEmpty(localType))
                        {
                            return localType;
                        }
                    }
                    break;
                }
                enclosingNode = node.Parent;
            }

            // 2. 所属クラスのフィールド・プロパティから探索
            enclosingNode = snapshot.FindNodeAt(cursorOffset);
            while (enclosingNode >= 0)
            {
                var node = snapshot.Nodes[enclosingNode];
                if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                    or (int)CsffNodeKind.StructDeclaration)
                {
                    var (cStart, cLen) = snapshot.SourceSpan(enclosingNode);
                    if (cStart >= 0 && cStart + cLen <= source.Length)
                    {
                        string classText = source.Substring(cStart, cLen);
                        string? fieldType = FindTypeInText(classText, variableName);
                        if (!string.IsNullOrEmpty(fieldType))
                        {
                            return fieldType;
                        }
                    }
                    break;
                }
                enclosingNode = node.Parent;
            }
        }

        // 3. ドキュメント全体からのパターン探索
        return FindTypeInText(source, variableName);
    }

    private static string? FindTypeInText(string text, string variableName)
    {
        // パターン1: var variableName = new Type(...)
        var varNewRegex = new Regex(
            $@"\bvar\s+{Regex.Escape(variableName)}\s*=\s*new\s+([A-Za-z0-9_]+)",
            RegexOptions.Compiled);
        var matchNew = varNewRegex.Match(text);
        if (matchNew.Success)
        {
            return matchNew.Groups[1].Value;
        }

        // パターン2: Type variableName (宣言 / 引数 / フィールド)
        var typedVarRegex = new Regex(
            $@"\b([A-Za-z0-9_]+)\s+{Regex.Escape(variableName)}\b",
            RegexOptions.Compiled);
        foreach (Match match in typedVarRegex.Matches(text))
        {
            string t = match.Groups[1].Value;
            if (!IsReservedKeyword(t))
            {
                return t;
            }
        }

        return null;
    }

    private static bool IsReservedKeyword(string word) =>
        word switch
        {
            "var" or "return" or "new" or "public" or "private" or "protected" or "internal"
            or "static" or "readonly" or "class" or "struct" or "void" or "if" or "while"
            or "for" or "foreach" or "switch" or "case" or "throw" or "async" or "await"
            or "get" or "set" or "override" or "virtual" or "sealed" or "abstract" or "const" => true,
            _ => false
        };

    private static List<Symbol> GetMembersOfType(DocumentSemanticModel model, string typeName)
    {
        var members = new List<Symbol>();

        // 1. ファイル内のスナップショットから検索
        var snapshot = model.Snapshot;
        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];
            if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                or (int)CsffNodeKind.StructDeclaration
                or (int)CsffNodeKind.InterfaceDeclaration)
            {
                string clsName = CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
                if (clsName == typeName)
                {
                    for (int j = 0; j < snapshot.Nodes.Length; j++)
                    {
                        if (snapshot.Nodes[j].Parent == i)
                        {
                            string mName = CsffSyntaxAdapter.FindIdentifier(snapshot, snapshot.Nodes[j], out int mStart, out int mLen);
                            if (!string.IsNullOrEmpty(mName))
                            {
                                var (start, len) = snapshot.SourceSpan(j);
                                var (line, col) = snapshot.Lines.GetPosition(mStart >= 0 ? mStart : start);
                                SymbolKind kind = (CsffNodeKind)snapshot.Nodes[j].Kind switch
                                {
                                    CsffNodeKind.MethodDeclaration => SymbolKind.Method,
                                    CsffNodeKind.PropertyDeclaration => SymbolKind.Property,
                                    CsffNodeKind.FieldDeclaration => SymbolKind.Field,
                                    _ => SymbolKind.Method
                                };
                                members.Add(new Symbol(
                                    Id: 0,
                                    NameId: 0,
                                    Name: mName,
                                    Kind: kind,
                                    Uri: "",
                                    Range: new Protocol.Range(new Position(line, col), new Position(line, col + mLen)),
                                    Signature: $"{kind} {mName}",
                                    ContainingType: typeName
                                ));
                            }
                        }
                    }
                }
            }
        }

        // 2. SymbolIndex（他ファイル・ワークスペース全体）からも補完
        foreach (var sym in model.Index.FindMembersByContainingType(typeName))
        {
            members.Add(sym);
        }

        return members;
    }
}
