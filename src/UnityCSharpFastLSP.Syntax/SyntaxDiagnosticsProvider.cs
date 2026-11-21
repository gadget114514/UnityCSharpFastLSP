using UnityCSharpFastLSP.Protocol;
using CsffNodeKind = Csff.Lexer.NodeKind;
using CsffTokenKind = Csff.Lexer.TokenKind;

namespace UnityCSharpFastLSP.Syntax;

public static class SyntaxDiagnosticsProvider
{
    public static List<Diagnostic> GetDiagnostics(DocumentSnapshot snapshot)
    {
        var diagnostics = new List<Diagnostic>();

        if (!snapshot.IsComplete)
        {
            // 構文不完全エラー
            int errOffset = Math.Max(0, snapshot.Source.Length - 1);
            if (snapshot.Tokens.Length > 0)
            {
                errOffset = snapshot.Tokens[^1].Start;
            }

            var (line, col) = snapshot.Lines.GetPosition(errOffset);
            var range = new Protocol.Range(
                new Position(line, col),
                new Position(line, Math.Max(col + 1, col + (snapshot.Tokens.Length > 0 ? snapshot.Tokens[^1].Length : 1)))
            );

            diagnostics.Add(new Diagnostic(
                Range: range,
                Severity: DiagnosticSeverity.Error,
                Code: "CSFF001",
                Source: "fastlsp-syntax",
                Message: "Syntax error: incomplete syntax element or missing closing delimiter"
            ));
        }

        // BadToken の検出
        for (int i = 0; i < snapshot.Tokens.Length; i++)
        {
            if (snapshot.Tokens[i].Kind == (int)CsffTokenKind.BadToken)
            {
                int start = snapshot.Tokens[i].Start;
                int len = Math.Max(1, snapshot.Tokens[i].Length);
                var (line, col) = snapshot.Lines.GetPosition(start);
                var range = new Protocol.Range(
                    new Position(line, col),
                    new Position(line, col + len)
                );

                diagnostics.Add(new Diagnostic(
                    Range: range,
                    Severity: DiagnosticSeverity.Error,
                    Code: "CSFF002",
                    Source: "fastlsp-syntax",
                    Message: "Syntax error: unexpected or bad token"
                ));
            }
        }

        return diagnostics;
    }
}
