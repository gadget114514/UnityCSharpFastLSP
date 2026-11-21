using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Syntax;

namespace UnityCSharpFastLSP.Semantics;

public static class HoverProvider
{
    public static Hover? GetHover(DocumentSemanticModel model, Position position)
    {
        string? word = model.GetWordAtPosition(position);
        if (string.IsNullOrEmpty(word))
        {
            return null;
        }

        var symbols = model.Index.FindSymbolsByName(word);
        if (symbols.Count == 0)
        {
            // Unity ライフサイクルのホバーヒント
            if (UnityRules.TryGetLifecycleDoc(word, out string? doc))
            {
                return new Hover(new MarkupContent("markdown", $"```csharp\nvoid {word}()\n```\n\n*Unity MonoBehaviour Lifecycle*\n\n{doc}"));
            }
            return null;
        }

        var first = symbols[0];
        string typeInfo = first.ContainingType is not null ? $"({first.ContainingType}) " : "";
        string signature = first.Signature ?? $"{first.Kind} {first.Name}";

        string markdown = $"```csharp\n{typeInfo}{signature}\n```";
        return new Hover(new MarkupContent("markdown", markdown), first.Range);
    }
}
