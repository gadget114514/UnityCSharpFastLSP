using System.Collections.Concurrent;
using UnityCSharpFastLSP.Protocol;
using UnityCSharpFastLSP.Semantics;
using UnityCSharpFastLSP.Syntax;
using UnityCSharpFastLSP.Workspace;

namespace UnityCSharpFastLSP.Server;

public sealed class ServerState
{
    private readonly ConcurrentDictionary<string, DocumentSnapshot> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _diagnosticCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly SymbolIndex _symbolIndex = new();
    private readonly WorkspaceManager _workspaceManager;
    private readonly LspMessageWriter _writer;

    public ServerState(LspMessageWriter writer)
    {
        _writer = writer;
        _workspaceManager = new WorkspaceManager(_symbolIndex);
    }

    public SymbolIndex SymbolIndex => _symbolIndex;
    public WorkspaceManager WorkspaceManager => _workspaceManager;
    public LspMessageWriter Writer => _writer;

    public DocumentSnapshot? GetDocument(string uri) =>
        _documents.TryGetValue(uri, out var snapshot) ? snapshot : null;

    public DocumentSnapshot? GetOrLoadDocument(string uri)
    {
        if (_documents.TryGetValue(uri, out var snapshot))
        {
            return snapshot;
        }

        if (Uri.TryCreate(uri, UriKind.Absolute, out var uriObj) && uriObj.IsFile)
        {
            if (File.Exists(uriObj.LocalPath))
            {
                string text = File.ReadAllText(uriObj.LocalPath);
                var snap = CsffSyntaxAdapter.Parse(text);
                _documents[uri] = snap;
                return snap;
            }
        }

        return null;
    }

    public void OpenDocument(string uri, string text, int version)
    {
        var snapshot = CsffSyntaxAdapter.Parse(text, version);
        _documents[uri] = snapshot;

        var symbols = SymbolExtractor.ExtractSymbols(uri, snapshot);
        _symbolIndex.UpdateFileSymbols(uri, symbols);

        PublishDiagnosticsAsync(uri, snapshot);
    }

    public void ChangeDocument(string uri, int version, List<TextDocumentContentChangeEvent> changes)
    {
        if (!_documents.TryGetValue(uri, out var currentSnapshot))
        {
            return;
        }

        DocumentSnapshot newSnapshot = currentSnapshot;

        foreach (var change in changes)
        {
            if (change.Range.HasValue)
            {
                var r = change.Range.Value;
                int startOffset = newSnapshot.Lines.GetOffset(r.Start.Line, r.Start.Character);
                int endOffset = newSnapshot.Lines.GetOffset(r.End.Line, r.End.Character);
                int length = Math.Max(0, endOffset - startOffset);

                // インクリメンタルパース
                newSnapshot = IncrementalParser.ApplyChange(
                    newSnapshot,
                    startOffset,
                    length,
                    change.Text,
                    version
                );
            }
            else
            {
                // 全文更新
                newSnapshot = CsffSyntaxAdapter.Parse(change.Text, version);
            }
        }

        _documents[uri] = newSnapshot;

        // シンボル索引の部分更新
        var symbols = SymbolExtractor.ExtractSymbols(uri, newSnapshot);
        _symbolIndex.UpdateFileSymbols(uri, symbols);

        // 診断の非同期発行（最新バージョン勝ち・キャンセル処理）
        PublishDiagnosticsAsync(uri, newSnapshot);
    }

    public void CloseDocument(string uri)
    {
        _documents.TryRemove(uri, out _);
        if (_diagnosticCts.TryRemove(uri, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        // 診断クリア
        _writer.WriteNotification(
            "textDocument/publishDiagnostics",
            new PublishDiagnosticsParams(uri, null, Array.Empty<Diagnostic>()),
            LspJsonSerializerContext.Default.PublishDiagnosticsParams
        );
    }

    private void PublishDiagnosticsAsync(string uri, DocumentSnapshot snapshot)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = _diagnosticCts.AddOrUpdate(uri, newCts, (_, old) =>
        {
            old.Cancel();
            old.Dispose();
            return newCts;
        });

        var token = newCts.Token;

        Task.Run(async () =>
        {
            try
            {
                // 5ms のデバウンス
                await Task.Delay(5, token);
                if (token.IsCancellationRequested) return;

                var diagnostics = SyntaxDiagnosticsProvider.GetDiagnostics(snapshot);
                if (token.IsCancellationRequested) return;

                _writer.WriteNotification(
                    "textDocument/publishDiagnostics",
                    new PublishDiagnosticsParams(uri, (int)snapshot.Version, diagnostics),
                    LspJsonSerializerContext.Default.PublishDiagnosticsParams
                );
            }
            catch (OperationCanceledException)
            {
                // キャンセル時は正常終了
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Diagnostics error for {uri}: {ex}");
            }
        }, token);
    }
}
