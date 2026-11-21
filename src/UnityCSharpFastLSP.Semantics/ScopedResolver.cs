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
        var wordSpan = model.GetWordSpanAtPosition(position);
        if (!wordSpan.HasValue)
        {
            return [];
        }

        string word = wordSpan.Value.Word;
        int wordStart = wordSpan.Value.Start;
        int wordLength = wordSpan.Value.Length;
        int offset = snapshot.Lines.GetOffset(position.Line, position.Character);
        int enclosingNodeIdx = snapshot.FindNodeAt(offset >= 0 ? offset : wordStart);
        string normalizedUri = UriHelper.Normalize(uri);

        // 1. ローカル変数 / パラメータの宣言位置を探索 (メソッド・ラムダスコープ内)
        int current = enclosingNodeIdx;
        while (current >= 0)
        {
            var node = snapshot.Nodes[current];
            if (node.Kind is (int)CsffNodeKind.MethodDeclaration
                or (int)CsffNodeKind.ConstructorDeclaration
                or (int)CsffNodeKind.LocalFunctionStatement
                or (int)CsffNodeKind.AnonymousMethodExpression
                or (int)CsffNodeKind.ParenthesizedLambdaExpression
                or (int)CsffNodeKind.SimpleLambdaExpression)
            {
                for (int t = node.TokenStart; t < node.TokenStart + node.TokenCount && t < snapshot.Tokens.Length; t++)
                {
                    var token = snapshot.Tokens[t];
                    if (token.Kind == (int)CsffTokenKind.IdentifierToken &&
                        token.Start >= 0 && token.Start + token.Length <= snapshot.Source.Length &&
                        snapshot.Source.Substring(token.Start, token.Length) == word)
                    {
                        if (IsDeclarationToken(snapshot, t))
                        {
                            // カーソル自身がこの宣言位置の上にある場合は何もしない（自分自身へのジャンプ防止）
                            if (token.Start == wordStart)
                            {
                                return [];
                            }

                            var (line, col) = snapshot.Lines.GetPosition(token.Start);
                            return [new Location(normalizedUri, new Protocol.Range(new Position(line, col), new Position(line, col + token.Length)))];
                        }
                    }
                }
                break;
            }
            current = node.Parent;
        }

        // 2. ドット手前の型が推論可能な場合、その型のメンバーから検索 (target.Member / Class.StaticMember)
        string? targetType = FindTargetTypeOfMember(snapshot, wordStart, word);
        if (!string.IsNullOrEmpty(targetType))
        {
            var targetTypes = new List<string> { targetType };
            targetTypes.AddRange(model.Index.GetAllBaseTypes(targetType));

            foreach (var tName in targetTypes)
            {
                var matchingMembers = model.Index.FindMembersByContainingType(tName);
                var memberMatch = matchingMembers.FirstOrDefault(m => m.Name == word);
                if (!string.IsNullOrEmpty(memberMatch.Name))
                {
                    return [new Location(UriHelper.Normalize(memberMatch.Uri), memberMatch.Range)];
                }
            }
        }

        // 3. 自クラス名および自クラス・基底クラス内メンバーの宣言位置探索
        string? selfClassName = null;
        current = enclosingNodeIdx;
        while (current >= 0)
        {
            var node = snapshot.Nodes[current];
            if (node.Kind is (int)CsffNodeKind.ClassDeclaration
                or (int)CsffNodeKind.StructDeclaration
                or (int)CsffNodeKind.InterfaceDeclaration)
            {
                selfClassName = CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
                break;
            }
            current = node.Parent;
        }

        if (!string.IsNullOrEmpty(selfClassName))
        {
            // 自クラスのメンバー
            var selfMembers = model.Index.FindMembersByContainingType(selfClassName);
            var selfMatch = selfMembers.FirstOrDefault(m => m.Name == word);
            if (!string.IsNullOrEmpty(selfMatch.Name))
            {
                if (UriHelper.Normalize(selfMatch.Uri).Equals(normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                    selfMatch.Range.Start.Line == position.Line &&
                    selfMatch.Range.Start.Character <= position.Character &&
                    selfMatch.Range.End.Character >= position.Character)
                {
                    // 宣言そのものの上にある
                    return [];
                }
                return [new Location(UriHelper.Normalize(selfMatch.Uri), selfMatch.Range)];
            }

            // 基底クラスのメンバー
            var baseTypes = model.Index.GetAllBaseTypes(selfClassName);
            foreach (var bType in baseTypes)
            {
                var baseMembers = model.Index.FindMembersByContainingType(bType);
                var bMatch = baseMembers.FirstOrDefault(m => m.Name == word);
                if (!string.IsNullOrEmpty(bMatch.Name))
                {
                    return [new Location(UriHelper.Normalize(bMatch.Uri), bMatch.Range)];
                }
            }
        }

        // 4. 型名そのもの（クラス / 構造体 / インターフェース / 列挙型）へのジャンプ
        var typeSymbols = model.Index.FindSymbolsByName(word);
        var typeMatches = typeSymbols
            .Where(s => s.Kind is SymbolKind.Class or SymbolKind.Struct or SymbolKind.Interface or SymbolKind.Enum)
            .ToList();
        if (typeMatches.Count > 0)
        {
            var valid = typeMatches
                .Where(s => !(UriHelper.Normalize(s.Uri).Equals(normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                              s.Range.Start.Line == position.Line &&
                              s.Range.Start.Character <= position.Character &&
                              s.Range.End.Character >= position.Character))
                .ToList();
            if (valid.Count > 0)
            {
                return valid.Select(s => new Location(UriHelper.Normalize(s.Uri), s.Range)).ToList();
            }
            return [];
        }

        // 5. グローバルな SymbolIndex 検索 (ファイル・ワークスペース全体)
        var defs = model.Index.FindDefinitions(word);
        if (defs.Count > 0)
        {
            var filtered = defs.Where(d => !(UriHelper.Normalize(d.Uri).Equals(normalizedUri, StringComparison.OrdinalIgnoreCase) &&
                d.Range.Start.Line == position.Line &&
                d.Range.Start.Character <= position.Character &&
                d.Range.End.Character >= position.Character)).ToList();
            if (filtered.Count > 0)
            {
                return filtered.Select(d => new Location(UriHelper.Normalize(d.Uri), d.Range)).ToList();
            }
            return [];
        }

        return [];
    }

    public static List<Location> ResolveDeclaration(
        DocumentSemanticModel model,
        string uri,
        Position position)
    {
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word)) return [];
        return model.Index.FindDeclarations(word);
    }

    public static List<Location> ResolveImplementation(
        DocumentSemanticModel model,
        string uri,
        Position position)
    {
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word)) return [];
        return model.Index.FindImplementations(word);
    }

    public static List<Location> ResolveTypeDefinition(
        DocumentSemanticModel model,
        string uri,
        Position position)
    {
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word)) return [];

        int offset = model.Snapshot.Lines.GetOffset(position.Line, position.Character);
        string? typeName = ResolveVariableType(model.Snapshot, word, offset);
        if (string.IsNullOrEmpty(typeName)) typeName = word;

        return model.Index.FindDefinitions(typeName);
    }

    public static List<Location> ResolveReferences(
        DocumentSemanticModel model,
        string uri,
        Position position,
        bool includeDeclaration = true)
    {
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word)) return [];

        var defs = model.Index.FindDefinitions(word);
        var refs = new List<Location>();

        if (includeDeclaration)
        {
            refs.AddRange(defs);
        }

        // includeDeclaration=false のとき、宣言位置（索引上の宣言 + スコープ内の宣言）を除外する
        var declSites = new HashSet<(int Line, int Character)>();
        if (!includeDeclaration)
        {
            string normalizedUri = UriHelper.Normalize(uri);
            foreach (var d in defs.Concat(ResolveScopedDefinition(model, uri, position)))
            {
                if (UriHelper.Normalize(d.Uri).Equals(normalizedUri, StringComparison.OrdinalIgnoreCase))
                {
                    declSites.Add((d.Range.Start.Line, d.Range.Start.Character));
                }
            }
        }

        var snapshot = model.Snapshot;
        string source = snapshot.Source;
        int wordLen = word.Length;
        int idx = 0;

        while ((idx = source.IndexOf(word, idx, StringComparison.Ordinal)) >= 0)
        {
            bool left = idx == 0 || !(char.IsLetterOrDigit(source[idx - 1]) || source[idx - 1] == '_');
            bool right = idx + wordLen == source.Length || !(char.IsLetterOrDigit(source[idx + wordLen]) || source[idx + wordLen] == '_');

            if (left && right)
            {
                var (sLine, sCol) = snapshot.Lines.GetPosition(idx);
                var (eLine, eCol) = snapshot.Lines.GetPosition(idx + wordLen);
                var loc = new Location(uri, new Protocol.Range(new Position(sLine, sCol), new Position(eLine, eCol)));

                if (!declSites.Contains((sLine, sCol)) &&
                    !refs.Any(r => r.Uri.Equals(uri, StringComparison.OrdinalIgnoreCase) &&
                    r.Range.Start.Line == sLine && r.Range.Start.Character == sCol))
                {
                    refs.Add(loc);
                }
            }

            idx += wordLen;
        }

        return refs;
    }

    private static bool IsDeclarationToken(DocumentSnapshot snapshot, int tokenIndex)
    {
        if (tokenIndex <= 0 || tokenIndex >= snapshot.Tokens.Length) return false;

        // 次のトークンが ( や . や [ の場合は呼び出しやメンバーアクセス等なので宣言ではない
        if (tokenIndex + 1 < snapshot.Tokens.Length)
        {
            var nextTok = snapshot.Tokens[tokenIndex + 1];
            if (nextTok.Start >= 0 && nextTok.Start + nextTok.Length <= snapshot.Source.Length)
            {
                string nextStr = snapshot.Source.Substring(nextTok.Start, nextTok.Length);
                if (nextStr is "(" or "." or "[" or "->" or "++" or "--" or "+" or "-" or "*" or "/")
                {
                    return false;
                }
            }
        }

        var prevToken = snapshot.Tokens[tokenIndex - 1];
        if (prevToken.Start >= 0 && prevToken.Start + prevToken.Length <= snapshot.Source.Length)
        {
            string prev = snapshot.Source.Substring(prevToken.Start, prevToken.Length);

            // 記号（; { } ( ) = . , + - など）の直後は型ではないので宣言ではない
            if (prev is ";" or "{" or "}" or "(" or ")" or "=" or "." or "==" or "!=" or "+=" or "-=" or "*=" or "/=" or "!" or "&" or "|" or "~" or "^" or ":" or "??" or "=>")
            {
                return false;
            }

            // 予約語のうち型名になり得ないキーワードの直後は宣言ではない
            if (IsNonTypeKeyword(prev))
            {
                return false;
            }

            // var, プリミティブ型, ジェネリック型閉じ >, 配列型閉じ ], null許容 ?, または識別子
            if (prev == "var" || IsPrimitiveTypeKeyword(prev) || prev is ">" or "]" or "?" || prevToken.Kind == (int)CsffTokenKind.IdentifierToken)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsPrimitiveTypeKeyword(string word) =>
        word switch
        {
            "int" or "float" or "string" or "bool" or "double" or "long" or "char" or "object"
            or "byte" or "sbyte" or "short" or "ushort" or "uint" or "ulong" or "decimal"
            or "nint" or "nuint" => true,
            _ => false
        };

    private static bool IsNonTypeKeyword(string word) =>
        word switch
        {
            "return" or "new" or "public" or "private" or "protected" or "internal"
            or "static" or "readonly" or "class" or "struct" or "void" or "if" or "while"
            or "for" or "foreach" or "switch" or "case" or "throw" or "async" or "await"
            or "get" or "set" or "override" or "virtual" or "sealed" or "abstract" or "const"
            or "namespace" or "using" or "in" or "is" or "as" or "typeof" or "sizeof" or "default"
            or "true" or "false" or "null" or "this" or "base" => true,
            _ => false
        };

    private static string? FindTargetTypeOfMember(DocumentSnapshot snapshot, int wordStart, string memberName)
    {
        string source = snapshot.Source;
        if (wordStart <= 0 || wordStart > source.Length) return null;

        // ドットの探索 (wordStart の直前の空白をスキップ)
        int p = wordStart - 1;
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
                string? resolved = ResolveVariableType(snapshot, targetVar, p);
                // 変数型が解決できなければ、静的メンバーアクセス (e.g. Mathf.Clamp, Boss.StaticMethod) としてそのまま型名とみなす
                return !string.IsNullOrEmpty(resolved) ? resolved : targetVar;
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

        // パターン2: var variableName = ...GetComponent<Type>(...) または FindObjectOfType<Type>(...)
        var varGenericRegex = new Regex(
            $@"\bvar\s+{Regex.Escape(variableName)}\s*=\s*(?:[A-Za-z0-9_.]+\.)?(?:GetComponent|GetComponentInChildren|GetComponentInParent|FindObjectOfType|FindAnyObjectByType|FindObjectsByType)\s*<\s*([A-Za-z0-9_]+)\s*>",
            RegexOptions.Compiled);
        var matchGen = varGenericRegex.Match(text);
        if (matchGen.Success)
        {
            return matchGen.Groups[1].Value;
        }

        // パターン3: var variableName = expr as Type
        var varAsRegex = new Regex(
            $@"\bvar\s+{Regex.Escape(variableName)}\s*=[^;\n\r]+\bas\s+([A-Za-z0-9_]+)",
            RegexOptions.Compiled);
        var matchAs = varAsRegex.Match(text);
        if (matchAs.Success && !IsReservedKeyword(matchAs.Groups[1].Value))
        {
            return matchAs.Groups[1].Value;
        }

        // パターン4: var variableName = (Type)expr
        var varCastRegex = new Regex(
            $@"\bvar\s+{Regex.Escape(variableName)}\s*=\s*\(\s*([A-Za-z0-9_]+)\s*\)",
            RegexOptions.Compiled);
        var matchCast = varCastRegex.Match(text);
        if (matchCast.Success && !IsReservedKeyword(matchCast.Groups[1].Value))
        {
            return matchCast.Groups[1].Value;
        }

        // パターン5: Type variableName (宣言 / 引数 / フィールド)
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
