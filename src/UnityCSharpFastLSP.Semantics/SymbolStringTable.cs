using System.Collections.Concurrent;

namespace UnityCSharpFastLSP.Semantics;

public sealed class SymbolStringTable
{
    private readonly ConcurrentDictionary<string, int> _stringToId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, string> _idToString = new();
    private int _nextId = 1;

    public int GetOrAdd(string text)
    {
        if (_stringToId.TryGetValue(text, out int id))
        {
            return id;
        }

        int newId = Interlocked.Increment(ref _nextId);
        if (_stringToId.TryAdd(text, newId))
        {
            _idToString[newId] = text;
            return newId;
        }

        return _stringToId[text];
    }

    public string? GetString(int id) =>
        _idToString.TryGetValue(id, out string? text) ? text : null;
}
