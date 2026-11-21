using System.Text.RegularExpressions;

namespace UnityCSharpFastLSP.Semantics;

/// <summary>
/// 高速かつ柔軟なシンボルパターンマッチャー。
/// 以下のパターン拡張をサポート:
/// 1. 完全一致 (Exact Match)
/// 2. プレフィックス一致 (Prefix Match)
/// 3. キャメルケース・ハンプロゴ一致 (CamelCase / CamelHump Match, e.g. "PC" -> "PlayerController")
/// 4. ワイルドカード一致 (Wildcard Glob Match with '*' and '?', e.g. "*Controller", "Take?amage")
/// 5. コンテナ修飾パターン (Container-qualified, e.g. "Player.Update", "Game/Player", "Class::Member")
/// 6. 部分一致 (Substring Match, case-insensitive)
/// 7. あいまいシーケンス一致 (Subsequence Fuzzy Match)
/// </summary>
public static class SymbolPatternMatcher
{
    public static bool Matches(string query, string name, string? containerName, out int score)
    {
        score = 0;
        if (string.IsNullOrEmpty(query))
        {
            // 空クエリはすべてのシンボルにマッチ（スコア低）
            score = 10;
            return true;
        }

        // 1. コンテナ修飾パターン (Container.Member or Container::Member or Container/Member)
        int sepIdx = query.IndexOfAny(['.', ':', '/']);
        if (sepIdx >= 0)
        {
            string containerPart = query[..sepIdx];
            string memberPart = query[(sepIdx + 1)..].TrimStart(':', '/');

            if (!string.IsNullOrEmpty(containerPart))
            {
                if (string.IsNullOrEmpty(containerName) ||
                    !MatchesPart(containerPart, containerName, out int containerScore))
                {
                    return false;
                }

                if (string.IsNullOrEmpty(memberPart))
                {
                    // "Container." の形式: コンテナ内の全メンバーにマッチ
                    score = 50 + containerScore;
                    return true;
                }

                if (MatchesPart(memberPart, name, out int memberScore))
                {
                    score = 100 + containerScore + memberScore;
                    return true;
                }

                return false;
            }
        }

        // 2. 単体パターンマッチ (シンボル名に対する評価)
        if (MatchesPart(query, name, out score))
        {
            return true;
        }

        // 3. コンテナ名が存在し、クエリがコンテナ名に前方一致する場合のボーナス
        if (!string.IsNullOrEmpty(containerName) && MatchesPart(query, containerName, out int cScore))
        {
            score = cScore / 2;
            return true;
        }

        return false;
    }

    private static bool MatchesPart(string pattern, string target, out int score)
    {
        score = 0;
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(target))
        {
            return false;
        }

        // 1. 完全一致 (大文字小文字区別)
        if (string.Equals(pattern, target, StringComparison.Ordinal))
        {
            score = 1000;
            return true;
        }

        // 2. 完全一致 (大文字小文字無視)
        if (string.Equals(pattern, target, StringComparison.OrdinalIgnoreCase))
        {
            score = 900;
            return true;
        }

        // 3. ワイルドカードパターン ('*' または '?')
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            if (WildcardMatch(pattern, target))
            {
                score = 600;
                return true;
            }
        }

        // 4. 前方一致 (Prefix match)
        if (target.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
        {
            score = 500 + (target.StartsWith(pattern, StringComparison.Ordinal) ? 50 : 0);
            return true;
        }

        // 5. キャメルケース・ハンプロゴ一致 (CamelCase / CamelHump match, e.g. "PC" -> "PlayerController")
        if (IsCamelHumpMatch(pattern, target))
        {
            score = 400 + (pattern.Length * 10);
            return true;
        }

        // 6. 部分一致 (Substring match)
        int idx = target.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            // 出現位置が先頭に近いほど高スコア
            score = 300 - Math.Min(idx * 10, 100);
            return true;
        }

        // 7. あいまいシーケンス一致 (Subsequence match)
        if (IsSubsequenceMatch(pattern, target, out int seqScore))
        {
            score = 100 + seqScore;
            return true;
        }

        return false;
    }

    private static bool WildcardMatch(string pattern, string target)
    {
        // 高速なインライン・ワイルドカード比較 (正規表現コンパイル不要)
        int p = 0, t = 0;
        int starIdx = -1, matchIdx = -1;

        while (t < target.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(target[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starIdx = p;
                matchIdx = t;
                p++;
            }
            else if (starIdx != -1)
            {
                p = starIdx + 1;
                matchIdx++;
                t = matchIdx;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    private static bool IsCamelHumpMatch(string pattern, string target)
    {
        if (pattern.Length == 0 || target.Length == 0) return false;

        // パターンの大文字・文字境界を抽出
        int pIdx = 0;
        for (int tIdx = 0; tIdx < target.Length && pIdx < pattern.Length; tIdx++)
        {
            char tc = target[tIdx];
            char pc = pattern[pIdx];

            // 単語境界の判定: 先頭文字、大文字、または '_' 直後の文字
            bool isWordBoundary = (tIdx == 0) || char.IsUpper(tc) || (tIdx > 0 && target[tIdx - 1] == '_');

            if (isWordBoundary && char.ToLowerInvariant(tc) == char.ToLowerInvariant(pc))
            {
                pIdx++;
            }
            else if (char.IsUpper(pc) && isWordBoundary && tc != pc)
            {
                // パターンが大文字指定の場合は厳密チェック
                continue;
            }
        }

        return pIdx == pattern.Length;
    }

    private static bool IsSubsequenceMatch(string pattern, string target, out int score)
    {
        score = 0;
        if (pattern.Length > target.Length) return false;

        int pIdx = 0;
        int consecutive = 0;
        int bonus = 0;

        for (int tIdx = 0; tIdx < target.Length && pIdx < pattern.Length; tIdx++)
        {
            if (char.ToLowerInvariant(pattern[pIdx]) == char.ToLowerInvariant(target[tIdx]))
            {
                pIdx++;
                consecutive++;
                bonus += consecutive * 5;
            }
            else
            {
                consecutive = 0;
            }
        }

        if (pIdx == pattern.Length)
        {
            score = Math.Min(bonus, 100);
            return true;
        }

        return false;
    }
}
