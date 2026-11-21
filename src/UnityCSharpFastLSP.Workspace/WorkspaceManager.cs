using System.Collections.Concurrent;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;

namespace UnityCSharpFastLSP.Workspace;

public sealed class WorkspaceManager
{
    private readonly SymbolIndex _symbolIndex;
    private readonly ConcurrentDictionary<string, string> _filePaths = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceManager(SymbolIndex symbolIndex)
    {
        _symbolIndex = symbolIndex;
    }

    public int IndexWorkspace(string rootPath)
    {
        if (string.IsNullOrEmpty(rootPath) || !Directory.Exists(rootPath))
        {
            return 0;
        }

        var files = new List<string>();
        string assetsDir = Path.Combine(rootPath, "Assets");
        string packagesDir = Path.Combine(rootPath, "Packages");
        string packageCacheDir = Path.Combine(rootPath, "Library", "PackageCache");

        if (Directory.Exists(assetsDir))
        {
            files.AddRange(Directory.EnumerateFiles(assetsDir, "*.cs", SearchOption.AllDirectories));
        }

        if (Directory.Exists(packagesDir))
        {
            files.AddRange(Directory.EnumerateFiles(packagesDir, "*.cs", SearchOption.AllDirectories));
        }

        if (Directory.Exists(packageCacheDir))
        {
            files.AddRange(Directory.EnumerateFiles(packageCacheDir, "*.cs", SearchOption.AllDirectories));
        }

        if (files.Count == 0)
        {
            // Unity 構成が見つからない場合はルート直下から検索
            files.AddRange(Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories));
        }

        // 並列でパース & シンボル抽出
        Parallel.ForEach(files, filePath =>
        {
            try
            {
                string source = File.ReadAllText(filePath);
                string uri = PathToFileUri(filePath);
                var snapshot = CsffSyntaxAdapter.Parse(source);
                var symbols = SymbolExtractor.ExtractSymbols(uri, snapshot);
                _symbolIndex.UpdateFileSymbols(uri, symbols);
                _filePaths[filePath] = uri;
            }
            catch
            {
                // エラー時はスキップ
            }
        });

        return files.Count;
    }

    public void UpdateFile(string filePath, string content)
    {
        string uri = PathToFileUri(filePath);
        var snapshot = CsffSyntaxAdapter.Parse(content);
        var symbols = SymbolExtractor.ExtractSymbols(uri, snapshot);
        _symbolIndex.UpdateFileSymbols(uri, symbols);
        _filePaths[filePath] = uri;
    }

    public void RemoveFile(string filePath)
    {
        string uri = PathToFileUri(filePath);
        _symbolIndex.RemoveFile(uri);
        _filePaths.TryRemove(filePath, out _);
    }

    public static string PathToFileUri(string path)
    {
        return UriHelper.Normalize(new Uri(Path.GetFullPath(path)).AbsoluteUri);
    }
}
