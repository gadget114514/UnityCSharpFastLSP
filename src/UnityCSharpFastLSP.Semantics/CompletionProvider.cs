using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public static class CompletionProvider
{
    public static CompletionList GetCompletions(DocumentSemanticModel model, Position position)
    {
        var snapshot = model.Snapshot;
        int offset = snapshot.Lines.GetOffset(position.Line, position.Character);
        string source = snapshot.Source;

        // ドット直後のメンバーアクセス判定 ("target.")
        if (offset > 0 && offset <= source.Length)
        {
            int p = offset - 1;
            while (p >= 0 && char.IsWhiteSpace(source[p])) p--;
            if (p >= 0 && source[p] == '.')
            {
                // ドットの手前の識別子を取得
                int idEnd = p;
                int idStart = idEnd - 1;
                while (idStart >= 0 && (char.IsLetterOrDigit(source[idStart]) || source[idStart] == '_'))
                {
                    idStart--;
                }
                idStart++;

                if (idEnd > idStart)
                {
                    string target = source.Substring(idStart, idEnd - idStart);
                    var memberItems = ScopedResolver.ResolveMemberCompletions(model, target, position);
                    if (memberItems.Count > 0)
                    {
                        // 型メンバーに絞り込んで返却！(grep とは決定的に違う！)
                        return new CompletionList(IsIncomplete: false, Items: memberItems);
                    }
                }
            }
        }

        // 通常の補完（Unity メッセージ・属性・識別子）
        var items = new List<CompletionItem>();

        // 1. Unity ライフサイクルメソッド候補
        foreach (string method in UnityRules.LifecycleNames)
        {
            UnityRules.TryGetLifecycleDoc(method, out string? doc);
            items.Add(new CompletionItem(
                Label: method,
                Kind: CompletionItemKind.Method,
                Detail: "void () - Unity Message",
                Documentation: doc is not null ? new MarkupContent("markdown", doc) : null,
                InsertText: $"private void {method}()\n{{\n    $0\n}}"
            ));
        }

        // 2. Unity 属性候補
        foreach (string attr in UnityRules.CommonAttributes)
        {
            items.Add(new CompletionItem(
                Label: attr,
                Kind: CompletionItemKind.Class,
                Detail: $"Unity Attribute [{attr}]",
                Documentation: null,
                InsertText: attr
            ));
        }

        // 3. インデックス内のシンボル候補
        for (int i = 0; i < snapshot.Nodes.Length; i++)
        {
            var node = snapshot.Nodes[i];
            string id = Syntax.CsffSyntaxAdapter.FindIdentifier(snapshot, node, out _, out _);
            if (!string.IsNullOrEmpty(id) && items.All(x => x.Label != id))
            {
                items.Add(new CompletionItem(
                    Label: id,
                    Kind: CompletionItemKind.Variable,
                    Detail: $"Symbol ({id})"
                ));
            }

            if (items.Count >= 60)
            {
                break;
            }
        }

        return new CompletionList(IsIncomplete: false, Items: items);
    }
}
