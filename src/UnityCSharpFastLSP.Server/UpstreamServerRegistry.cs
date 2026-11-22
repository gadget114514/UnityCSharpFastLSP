using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace UnityCSharpFastLSP.Server;

/// <summary>プロキシがフォールバック先として起動する upstream LSP サーバーの種類。</summary>
public enum UpstreamKind
{
    /// <summary>パス／環境変数から自動判定し、起動できるものを順に試す。</summary>
    Auto,

    /// <summary>UnityCSharpLSP (Roslyn, --stdio)。</summary>
    UnityCSharp,

    /// <summary>csharp-ls (stdio が既定のため引数不要)。</summary>
    CSharpLs,

    /// <summary>OmniSharp (-lsp で LSP over stdio)。</summary>
    OmniSharp,
}

/// <summary>1 回の upstream 起動試行（種類と明示指定された実行ファイル）。</summary>
public sealed record UpstreamAttempt(UpstreamKind Kind, string? ExplicitPath);

/// <summary>
/// upstream LSP サーバーの種別ごとの探索候補・起動引数を提供する。
/// 既定 (Auto) では UnityCSharpLSP → csharp-ls → OmniSharp の順に起動を試みる。
/// </summary>
public static class UpstreamServerRegistry
{
    public const string UnityCSharpPathEnv = "UNITY_CSHARP_LSP_PATH";
    public const string CSharpLsPathEnv = "CSHARP_LS_PATH";
    public const string OmniSharpPathEnv = "OMNISHARP_PATH";

    private static readonly UpstreamKind[] DefaultOrder =
        [UpstreamKind.UnityCSharp, UpstreamKind.CSharpLs, UpstreamKind.OmniSharp];

    /// <summary>--upstream-kind の文字列を種別に変換する。未知の値は例外。</summary>
    public static UpstreamKind ParseKind(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return UpstreamKind.Auto;

        return text.Trim().ToLowerInvariant().Replace("_", "-") switch
        {
            "auto" => UpstreamKind.Auto,
            "unitycsharp" or "unitycsharplsp" or "unity-csharp" or "unity-csharp-lsp" => UpstreamKind.UnityCSharp,
            "csharp-ls" or "csharpls" or "csharp" or "dotnet-csharp-ls" => UpstreamKind.CSharpLs,
            "omnisharp" or "omni-sharp" => UpstreamKind.OmniSharp,
            _ => throw new ArgumentException(
                $"Unknown upstream kind '{text}'. Expected: auto, unitycsharp, csharp-ls, omnisharp."),
        };
    }

    /// <summary>ログ・エラー表示用の名前。</summary>
    public static string DisplayName(UpstreamKind kind) => kind switch
    {
        UpstreamKind.UnityCSharp => "UnityCSharpLSP",
        UpstreamKind.CSharpLs => "csharp-ls",
        UpstreamKind.OmniSharp => "OmniSharp",
        _ => "auto",
    };

    /// <summary>--upstream-kind に渡せる正規名。</summary>
    public static string CanonicalName(UpstreamKind kind) => kind switch
    {
        UpstreamKind.UnityCSharp => "unitycsharp",
        UpstreamKind.CSharpLs => "csharp-ls",
        UpstreamKind.OmniSharp => "omnisharp",
        _ => "auto",
    };

    /// <summary>種別に対応する実行ファイルパス環境変数。</summary>
    public static string? PathEnvironmentVariable(UpstreamKind kind) => kind switch
    {
        UpstreamKind.UnityCSharp => UnityCSharpPathEnv,
        UpstreamKind.CSharpLs => CSharpLsPathEnv,
        UpstreamKind.OmniSharp => OmniSharpPathEnv,
        _ => null,
    };

    /// <summary>実行ファイル名から種別を推測する。判別不能なら Auto。</summary>
    public static UpstreamKind DetectKindFromPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return UpstreamKind.Auto;
        string name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("unitycsharp", StringComparison.Ordinal)) return UpstreamKind.UnityCSharp;
        if (name is "csharp-ls" or "csharpls" or "csharp_ls") return UpstreamKind.CSharpLs;
        if (name.Contains("omnisharp", StringComparison.Ordinal)) return UpstreamKind.OmniSharp;
        return UpstreamKind.Auto;
    }

    /// <summary>
    /// 起動試行の順序を返す。明示指定 (--upstream) と環境変数で指定されたサーバーを優先し、
    /// その後 UnityCSharpLSP → csharp-ls → OmniSharp の順にフォールバックする。
    /// </summary>
    public static IReadOnlyList<UpstreamAttempt> ResolveAttempts(UpstreamKind requested, string? explicitPath)
    {
        if (requested != UpstreamKind.Auto)
        {
            return [new UpstreamAttempt(requested, explicitPath)];
        }

        var attempts = new List<UpstreamAttempt>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            UpstreamKind detected = DetectKindFromPath(explicitPath);
            attempts.Add(new UpstreamAttempt(detected == UpstreamKind.Auto ? UpstreamKind.UnityCSharp : detected, explicitPath));
        }

        foreach (UpstreamKind kind in DefaultOrder)
        {
            string? env = Environment.GetEnvironmentVariable(PathEnvironmentVariable(kind)!);
            if (!string.IsNullOrWhiteSpace(env))
            {
                attempts.Add(new UpstreamAttempt(kind, null));
            }
        }

        foreach (UpstreamKind kind in DefaultOrder)
        {
            attempts.Add(new UpstreamAttempt(kind, null));
        }

        return attempts.DistinctBy(a => a.Kind).ToArray();
    }

    /// <summary>指定された試行で使用する実行ファイル候補（優先順）。</summary>
    public static IReadOnlyList<string> GetCandidateCommands(UpstreamAttempt attempt)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(attempt.ExplicitPath))
        {
            candidates.Add(attempt.ExplicitPath);
        }

        string? env = Environment.GetEnvironmentVariable(PathEnvironmentVariable(attempt.Kind)!);
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        switch (attempt.Kind)
        {
            case UpstreamKind.UnityCSharp:
                candidates.Add("UnityCSharpLSP.Server.exe");
                candidates.Add("UnityCSharpLSP.exe");
                candidates.Add("UnityCSharpLSP.Server");
                candidates.Add("UnityCSharpLSP");
                candidates.Add(Path.Combine(localAppData, "Programs", "UnityCSharpLSP", "UnityCSharpLSP.Server.exe"));
                break;

            case UpstreamKind.CSharpLs:
                candidates.Add("csharp-ls");
                candidates.Add("csharp-ls.exe");
                candidates.Add(Path.Combine(userProfile, ".dotnet", "tools", "csharp-ls"));
                candidates.Add(Path.Combine(userProfile, ".dotnet", "tools", "csharp-ls.exe"));
                break;

            case UpstreamKind.OmniSharp:
                candidates.Add("omnisharp");
                candidates.Add("omnisharp.exe");
                candidates.Add("OmniSharp.exe");
                candidates.Add(Path.Combine(userProfile, ".omnisharp", "omnisharp", "OmniSharp.exe"));
                candidates.Add(Path.Combine(localAppData, "Programs", "OmniSharp", "OmniSharp.exe"));
                break;
        }

        return candidates.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// サーバーごとの起動引数。csharp-ls は stdio が既定で引数不要、
    /// OmniSharp は -lsp (--languageserver) が必要で、workspace が分かっていれば -s も渡す。
    /// </summary>
    public static string GetStartArguments(UpstreamKind kind, string? workspacePath) => kind switch
    {
        UpstreamKind.CSharpLs => string.Empty,
        UpstreamKind.OmniSharp => "-lsp" + (string.IsNullOrWhiteSpace(workspacePath)
            ? string.Empty
            : $" -s {Quote(workspacePath!)}"),
        _ => "--stdio",
    };

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
