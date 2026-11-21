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

        // targetIdentifier の型名を推論
        string? typeName = ResolveVariableType(model.Snapshot, targetIdentifier, cursorOffset);
        if (string.IsNullOrEmpty(typeName))
        {
            typeName = targetIdentifier; // 型名直接アクセス（static member または未解決）
        }

        // SymbolIndex から型宣言を探索
        string resolvedType = typeName;
        foreach (var sym in model.Index.FindSymbolsByName(typeName))
        {
            if (sym.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum)
            {
                resolvedType = sym.Name;
                break;
            }
        }

        // resolvedType および基底クラス、Unity API 辞書のメンバーを抽出
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
                Detail: $"({m.ContainingType ?? resolvedType}) {m.Signature ?? m.Name}",
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

        // 1. ローカル変数 / パラメータの宣言位置を探索
        int current = enclosingNodeIdx;
        while (current >= 0)
        {
            var node = snapshot.Nodes[current];
            if (node.Kind is (int)CsffNodeKind.MethodDeclaration
                or (int)CsffNodeKind.ConstructorDeclaration
                or (int)CsffNodeKind.LocalFunctionStatement)
            {
                for (int t = node.TokenStart; t < node.TokenStart + node.TokenCount && t < snapshot.Tokens.Length; t++)
                {
                    var token = snapshot.Tokens[t];
                    if (token.Kind == (int)CsffTokenKind.IdentifierToken &&
                        token.Start >= 0 && token.Start + token.Length <= snapshot.Source.Length &&
                        snapshot.Source.Substring(token.Start, token.Length) == word)
                    {
                        // 宣言判定: カーソル自身と異なる位置で、直前が型名や var、あるいは引数定義
                        if (IsDeclarationToken(snapshot, t))
                        {
                            var (line, col) = snapshot.Lines.GetPosition(token.Start);
                            if (line != position.Line || col != position.Character)
                            {
                                return [new Location(uri, new Protocol.Range(new Position(line, col), new Position(line, col + token.Length)))];
                            }
                        }
                    }
                }
                break;
            }
            current = node.Parent;
        }

        // 2. 自クラス内メンバーの宣言位置探索
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
                        if (id == word && (offset < start || offset > start + len))
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

        // 3. ドット手前の型が推論可能な場合、その型のメンバーから検索
        string? targetType = FindTargetTypeOfMember(snapshot, offset, word);
        if (!string.IsNullOrEmpty(targetType))
        {
            var targetTypes = new List<string> { targetType };
            targetTypes.AddRange(model.Index.GetAllBaseTypes(targetType));

            foreach (var tName in targetTypes)
            {
                var matchingMembers = model.Index.FindMembersByContainingType(tName);
                var memberMatches = matchingMembers.Where(m => m.Name == word).ToList();
                if (memberMatches.Count > 0)
                {
                    return memberMatches.Select(m => new Location(m.Uri, m.Range)).ToList();
                }
            }
        }

        // 4. グローバルな SymbolIndex 検索 (ファイル・ワークスペース全体)
        var defs = model.Index.FindDefinitions(word);
        if (defs.Count > 0)
        {
            // カーソル自身と同一位置のものは除外（自分自身へのジャンプを防ぐ）
            var filtered = defs.Where(d => !(d.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase) &&
                d.Range.Start.Line == position.Line &&
                d.Range.Start.Character <= position.Character &&
                d.Range.End.Character >= position.Character)).ToList();
            if (filtered.Count > 0)
            {
                return filtered;
            }
            return defs;
        }

        return [];
    }

    private static bool IsDeclarationToken(DocumentSnapshot snapshot, int tokenIndex)
    {
        if (tokenIndex <= 0) return false;
        var prevToken = snapshot.Tokens[tokenIndex - 1];
        if (prevToken.Start >= 0 && prevToken.Start + prevToken.Length <= snapshot.Source.Length)
        {
            string prev = snapshot.Source.Substring(prevToken.Start, prevToken.Length);
            if (prev == "var" || !IsReservedKeyword(prev))
            {
                return true;
            }
        }
        return false;
    }

    private static string? FindTargetTypeOfMember(DocumentSnapshot snapshot, int offset, string memberName)
    {
        string source = snapshot.Source;
        int wordPos = source.LastIndexOf(memberName, Math.Min(source.Length, offset + memberName.Length), StringComparison.Ordinal);
        if (wordPos <= 0) return null;

        // ドットの探索
        int p = wordPos - 1;
        while (p >= 0 && char.IsWhiteSpace(source[p])) p--;
        if (p >= 0 && source[p] == '.')
        {
            int idEnd = p;
            int idStart = idEnd - 1;
            while (idStart >= 0 && (char.IsLetterOrDigit(source[idStart]) || source[idStart] == '_')) idStart--;
            idStart++;
            if (idEnd > idStart)
            {
                string targetVar = source.Substring(idStart, idEnd - idStart);
                return ResolveVariableType(snapshot, targetVar, p);
            }
        }

        return null;
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

        // Unity組み込みプロパティの推論
        if (variableName == "transform") return "Transform";
        if (variableName == "gameObject") return "GameObject";

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
        var typesToSearch = new List<string> { typeName };

        // 継承階層の基底型を展開！
        var baseTypes = model.Index.GetAllBaseTypes(typeName);
        typesToSearch.AddRange(baseTypes);

        // MonoBehaviour の場合は自動的に Component も追加
        if (typesToSearch.Any(t => t.Equals("MonoBehaviour", StringComparison.OrdinalIgnoreCase)) &&
            !typesToSearch.Any(t => t.Equals("Component", StringComparison.OrdinalIgnoreCase)))
        {
            typesToSearch.Add("Component");
        }

        var snapshot = model.Snapshot;

        foreach (var tName in typesToSearch)
        {
            // 1. ファイル内のスナップショットから検索
            for (int i = 0; i < snapshot.Nodes.Length; i++)
            {
                var node = snapshot.Nodes[i];
                if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                    or (int)CsffNodeKind.StructDeclaration
                    or (int)CsffNodeKind.InterfaceDeclaration)
                {
                    string clsName = CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
                    if (clsName == tName)
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
                                        ContainingType: tName
                                    ));
                                }
                            }
                        }
                    }
                }
            }

            // 2. SymbolIndex（ワークスペース全体）から検索
            foreach (var sym in model.Index.FindMembersByContainingType(tName))
            {
                members.Add(sym);
            }

            // 3. UnityApiDictionary（事前定義インメモリ辞書）から検索！
            if (UnityApiDictionary.TryGetMembers(tName, out var unityMembers))
            {
                members.AddRange(unityMembers);
            }
        }

        return members;
    }
}
