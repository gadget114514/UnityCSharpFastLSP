using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public static class CompletionProvider
{
    public static CompletionList GetCompletions(DocumentSemanticModel model, Position position)
    {
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
        var snapshot = model.Snapshot;
        int offset = snapshot.Lines.GetOffset(position.Line, position.Character);
        int nodeIdx = snapshot.FindNodeAt(offset);

        // 同一ファイル内の宣言ノード等を追加
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

            if (items.Count >= 50)
            {
                break;
            }
        }

        return new CompletionList(IsIncomplete: false, Items: items);
    }
}
