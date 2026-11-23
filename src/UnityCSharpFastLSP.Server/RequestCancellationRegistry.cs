using System;
using System.Collections.Concurrent;
using System.Threading;

namespace UnityCSharpFastLSP.Server;

public sealed class RequestCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources = new(StringComparer.Ordinal);

    public CancellationToken Register(string id, CancellationToken ct)
    {
        CancellationTokenSource source = ct.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : new CancellationTokenSource();

        _sources.AddOrUpdate(id, source, (_, old) =>
        {
            old.Dispose();
            return source;
        });
        return source.Token;
    }

    public void Unregister(string id)
    {
        if (_sources.TryRemove(id, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }

    public CancellationToken GetToken(string id) =>
        _sources.TryGetValue(id, out CancellationTokenSource? source) ? source.Token : CancellationToken.None;

    public bool Cancel(string id)
    {
        if (!_sources.TryGetValue(id, out CancellationTokenSource? source)) return false;
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
