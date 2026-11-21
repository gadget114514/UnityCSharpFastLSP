using System.Text;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Syntax;

public static class FastFormatter
{
    public static List<TextEdit> FormatDocument(DocumentSnapshot snapshot, FormattingOptions options)
    {
        string source = snapshot.Source;
        string indentStr = options.InsertSpaces ? new string(' ', Math.Max(1, options.TabSize)) : "\t";

        var sb = new StringBuilder(source.Length + 64);
        var lines = source.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        int currentIndent = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                sb.AppendLine();
                continue;
            }

            // 閉じ括弧で始まる行はインデントを1段下げる
            if (line.StartsWith('}') || line.StartsWith(')'))
            {
                currentIndent = Math.Max(0, currentIndent - 1);
            }

            // インデント付与
            for (int k = 0; k < currentIndent; k++)
            {
                sb.Append(indentStr);
            }
            sb.AppendLine(line);

            // 開き括弧で終わる行はインデントを1段上げる
            int openCount = 0;
            int closeCount = 0;
            bool inString = false;
            for (int c = 0; c < line.Length; c++)
            {
                if (line[c] == '"' && (c == 0 || line[c - 1] != '\\'))
                {
                    inString = !inString;
                }
                if (!inString)
                {
                    if (line[c] == '{') openCount++;
                    else if (line[c] == '}') closeCount++;
                }
            }

            int netChange = openCount - closeCount;
            if (!line.StartsWith('}') && !line.StartsWith(')'))
            {
                currentIndent = Math.Max(0, currentIndent + netChange);
            }
            else
            {
                // すでに1段下げているので差分を反映
                currentIndent = Math.Max(0, currentIndent + openCount);
            }
        }

        string newText = sb.ToString().TrimEnd() + Environment.NewLine;

        // 全体を1つの TextEdit で置換
        int lastLine = snapshot.Lines.LineCount;
        int lastChar = 0;
        if (lines.Length > 0)
        {
            lastLine = lines.Length;
            lastChar = lines[^1].Length;
        }

        var fullRange = new Protocol.Range(
            new Position(0, 0),
            new Position(lastLine, lastChar)
        );

        return [new TextEdit(fullRange, newText)];
    }
}
