using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public static class SignatureHelpProvider
{
    public static SignatureHelp? GetSignatureHelp(DocumentSemanticModel model, Position position)
    {
        var snapshot = model.Snapshot;
        int offset = snapshot.Lines.GetOffset(position.Line, position.Character);
        string source = snapshot.Source;
        if (offset <= 0 || offset > source.Length) return null;

        // カーソル位置から手前に向かって開き括弧 '(' を探索し、カンマ数を数える
        int depth = 0;
        int commaCount = 0;
        int openParenPos = -1;

        for (int p = offset - 1; p >= 0; p--)
        {
            char c = source[p];
            if (c == ')') depth++;
            else if (c == '(')
            {
                if (depth > 0) depth--;
                else
                {
                    openParenPos = p;
                    break;
                }
            }
            else if (c == ',' && depth == 0)
            {
                commaCount++;
            }
            else if (c == ';' || c == '{' || c == '}')
            {
                break;
            }
        }

        if (openParenPos < 0) return null;

        // '(' の手前の識別子（メソッド名）を取得
        int idEnd = openParenPos;
        while (idEnd > 0 && char.IsWhiteSpace(source[idEnd - 1])) idEnd--;
        int idStart = idEnd;
        while (idStart > 0 && (char.IsLetterOrDigit(source[idStart - 1]) || source[idStart - 1] == '_')) idStart--;

        if (idEnd <= idStart) return null;
        string methodName = source.Substring(idStart, idEnd - idStart);

        var signatures = new List<SignatureInformation>();

        // 1. SymbolIndex からメソッドのシグネチャを探索
        foreach (var sym in model.Index.FindSymbolsByName(methodName))
        {
            if (sym.Kind is SymbolKind.Method or SymbolKind.Constructor && !string.IsNullOrEmpty(sym.Signature))
            {
                signatures.Add(CreateSignatureInfo(sym.Signature));
            }
        }

        // 2. Unity API 辞書からも探索
        // ドット手前の型がわかる場合はその型のメソッドに絞る
        int dotPos = idStart - 1;
        while (dotPos >= 0 && char.IsWhiteSpace(source[dotPos])) dotPos--;
        if (dotPos >= 0 && source[dotPos] == '.')
        {
            int tEnd = dotPos;
            int tStart = tEnd - 1;
            while (tStart >= 0 && (char.IsLetterOrDigit(source[tStart]) || source[tStart] == '_')) tStart--;
            tStart++;
            if (tEnd > tStart)
            {
                string targetVar = source.Substring(tStart, tEnd - tStart);
                string? typeName = ScopedResolver.ResolveVariableType(snapshot, targetVar, dotPos);
                if (string.IsNullOrEmpty(typeName)) typeName = targetVar;

                if (UnityApiDictionary.TryGetMembers(typeName, out var unityMembers))
                {
                    foreach (var m in unityMembers.Where(m => m.Name == methodName && !string.IsNullOrEmpty(m.Signature)))
                    {
                        signatures.Add(CreateSignatureInfo(m.Signature!));
                    }
                }
            }
        }

        if (signatures.Count == 0)
        {
            // フォールバック: 単純な空シグネチャ
            signatures.Add(new SignatureInformation(
                Label: $"{methodName}()",
                Documentation: null,
                Parameters: []
            ));
        }

        int activeParam = 0;
        var firstParams = signatures.Count > 0 ? signatures[0].Parameters : null;
        if (firstParams != null && firstParams.Count > 0)
        {
            activeParam = Math.Clamp(commaCount, 0, firstParams.Count - 1);
        }

        return new SignatureHelp(
            Signatures: signatures,
            ActiveSignature: 0,
            ActiveParameter: activeParam
        );
    }

    private static SignatureInformation CreateSignatureInfo(string signature)
    {
        var parameters = new List<ParameterInformation>();
        int openParen = signature.IndexOf('(');
        int closeParen = signature.LastIndexOf(')');

        if (openParen >= 0 && closeParen > openParen)
        {
            string paramText = signature.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            if (!string.IsNullOrEmpty(paramText))
            {
                string[] parts = paramText.Split(',');
                foreach (var p in parts)
                {
                    parameters.Add(new ParameterInformation(p.Trim()));
                }
            }
        }

        return new SignatureInformation(
            Label: signature,
            Documentation: null,
            Parameters: parameters
        );
    }
}
