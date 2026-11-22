using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace UnityCSharpFastLSP.Workspace;

/// <summary>
/// シェル風 glob でファイルパスを照合する（UnityCSharpLSP の GlobPatternMatcher と同じ仕様）。
/// <list type="bullet">
/// <item><c>/</c> を含まないパターンはファイル名に対して照合する（<c>*.cs</c> は ripgrep の <c>-g '*.cs'</c> と同じ）。</item>
/// <item><c>*</c> は <c>/</c> 以外の任意文字列、<c>**</c> は任意の階層、<c>**/</c> は 0 階層以上のディレクトリ、<c>?</c> は 1 文字。</item>
/// <item>先頭 <c>!</c> は否定（後に書いたパターンが優先）、先頭 <c>~</c> はパスの部分一致。</item>
/// <item>大文字小文字は区別しない。パスは <c>\</c> を <c>/</c> に正規化して扱う。</item>
/// </list>
/// </summary>
public static class GlobPatternMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// いずれかのパターンに一致するか。パスやパターンが空なら true。
    /// 否定パターンを含む場合は、一致した最後のパターンで決まる。
    /// </summary>
    public static bool MatchesAny(string? path, IReadOnlyList<string>? patterns)
    {
        if (string.IsNullOrEmpty(path) || patterns is null || patterns.Count == 0)
        {
            return true;
        }

        bool included = false;
        foreach (string pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            bool negated = pattern[0] == '!';
            string glob = negated ? pattern[1..] : pattern;
            if (!Matches(path, glob))
            {
                continue;
            }

            included = !negated;
        }

        return included;
    }

    public static bool Matches(string path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        string normalizedPath = path.Replace('\\', '/');

        if (pattern[0] == '~')
        {
            string needle = pattern[1..].Replace('\\', '/').TrimStart('/');
            return needle.Length > 0 && normalizedPath.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }

        string normalizedPattern = pattern.Replace('\\', '/').TrimStart('/');
        string candidate = normalizedPattern.Contains('/')
            ? normalizedPath
            : normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..];

        return Cache.GetOrAdd(normalizedPattern, BuildRegex).IsMatch(candidate);
    }

    private static Regex BuildRegex(string normalizedPattern)
    {
        var regex = new StringBuilder("^");
        for (int i = 0; i < normalizedPattern.Length; i++)
        {
            char c = normalizedPattern[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '*')
                    {
                        i++;
                        if (i + 1 < normalizedPattern.Length && normalizedPattern[i + 1] == '/')
                        {
                            i++;
                            regex.Append("(?:.*/)?");
                        }
                        else
                        {
                            regex.Append(".*");
                        }
                    }
                    else
                    {
                        regex.Append("[^/]*");
                    }
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        regex.Append('$');

        return new Regex(regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
