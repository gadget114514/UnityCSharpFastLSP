using System.Collections.Concurrent;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public sealed class SymbolIndex
{
    private readonly SymbolStringTable _stringTable = new();
    private readonly ConcurrentDictionary<string, List<int>> _fileToSymbolIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Symbol> _symbolsById = new();
    private readonly ConcurrentDictionary<int, HashSet<int>> _nameIdToSymbolIds = new();
    private readonly ConcurrentDictionary<int, HashSet<int>> _containingTypeToSymbolIds = new();
    private readonly ConcurrentDictionary<string, List<string>> _typeToBaseTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _baseToDerivedTypes = new(StringComparer.OrdinalIgnoreCase);
    private int _nextSymbolId = 1;

    public SymbolStringTable StringTable => _stringTable;

    public void UpdateFileSymbols(string uri, IReadOnlyList<Symbol> symbols)
    {
        // 既存のファイル内シンボルを削除
        if (_fileToSymbolIds.TryGetValue(uri, out var oldSymbolIds))
        {
            foreach (int id in oldSymbolIds)
            {
                if (_symbolsById.TryRemove(id, out var oldSym))
                {
                    if (_nameIdToSymbolIds.TryGetValue(oldSym.NameId, out var idSet))
                    {
                        lock (idSet)
                        {
                            idSet.Remove(id);
                        }
                    }

                    if (!string.IsNullOrEmpty(oldSym.ContainingType))
                    {
                        int cId = _stringTable.GetOrAdd(oldSym.ContainingType);
                        if (_containingTypeToSymbolIds.TryGetValue(cId, out var cSet))
                        {
                            lock (cSet)
                            {
                                cSet.Remove(id);
                            }
                        }
                    }
                }
            }
        }

        var newIds = new List<int>(symbols.Count);
        foreach (var sym in symbols)
        {
            int id = Interlocked.Increment(ref _nextSymbolId);
            int nameId = _stringTable.GetOrAdd(sym.Name);

            var indexedSym = sym with { Id = id, NameId = nameId };
            _symbolsById[id] = indexedSym;
            newIds.Add(id);

            var idSet = _nameIdToSymbolIds.GetOrAdd(nameId, _ => new HashSet<int>());
            lock (idSet)
            {
                idSet.Add(id);
            }

            if (!string.IsNullOrEmpty(sym.ContainingType))
            {
                int cId = _stringTable.GetOrAdd(sym.ContainingType);
                var cSet = _containingTypeToSymbolIds.GetOrAdd(cId, _ => new HashSet<int>());
                lock (cSet)
                {
                    cSet.Add(id);
                }
            }

            if (sym.BaseTypes != null && sym.BaseTypes.Count > 0)
            {
                _typeToBaseTypes[sym.Name] = new List<string>(sym.BaseTypes);
                foreach (var b in sym.BaseTypes)
                {
                    var dSet = _baseToDerivedTypes.GetOrAdd(b, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    lock (dSet)
                    {
                        dSet.Add(sym.Name);
                    }
                }
            }
        }

        _fileToSymbolIds[uri] = newIds;
    }

    public void RemoveFile(string uri)
    {
        UpdateFileSymbols(uri, Array.Empty<Symbol>());
        _fileToSymbolIds.TryRemove(uri, out _);
    }

    public List<Location> FindDefinitions(string name)
    {
        var results = new List<Location>();
        int nameId = _stringTable.GetOrAdd(name);

        if (_nameIdToSymbolIds.TryGetValue(nameId, out var idSet))
        {
            List<int> ids;
            lock (idSet)
            {
                ids = new List<int>(idSet);
            }

            foreach (int id in ids)
            {
                if (_symbolsById.TryGetValue(id, out var sym))
                {
                    results.Add(new Location(sym.Uri, sym.Range));
                }
            }
        }

        return results;
    }

    public List<Location> FindReferences(string name)
    {
        // 定義箇所を含む参照候補を返却
        return FindDefinitions(name);
    }

    public List<Symbol> FindSymbolsByName(string name)
    {
        var results = new List<Symbol>();
        int nameId = _stringTable.GetOrAdd(name);

        if (_nameIdToSymbolIds.TryGetValue(nameId, out var idSet))
        {
            List<int> ids;
            lock (idSet)
            {
                ids = new List<int>(idSet);
            }

            foreach (int id in ids)
            {
                if (_symbolsById.TryGetValue(id, out var sym))
                {
                    results.Add(sym);
                }
            }
        }

        return results;
    }

    public List<Symbol> FindMembersByContainingType(string containingType)
    {
        var results = new List<Symbol>();
        int typeNameId = _stringTable.GetOrAdd(containingType);

        if (_containingTypeToSymbolIds.TryGetValue(typeNameId, out var idSet))
        {
            List<int> ids;
            lock (idSet)
            {
                ids = new List<int>(idSet);
            }

            foreach (int id in ids)
            {
                if (_symbolsById.TryGetValue(id, out var sym))
                {
                    results.Add(sym);
                }
            }
        }

        return results;
    }

    public List<string> GetAllBaseTypes(string typeName)
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { typeName };
        var queue = new Queue<string>();
        queue.Enqueue(typeName);

        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (_typeToBaseTypes.TryGetValue(cur, out var bases))
            {
                foreach (var b in bases)
                {
                    if (visited.Add(b))
                    {
                        result.Add(b);
                        queue.Enqueue(b);
                    }
                }
            }
        }

        return result;
    }

    public List<Location> FindImplementations(string symbolName)
    {
        var results = new List<Location>();

        // 1. symbolName がインターフェースまたは基底クラスの場合:
        // それを継承・実装しているクラスの定義位置を返す
        if (_baseToDerivedTypes.TryGetValue(symbolName, out var derivedNames))
        {
            List<string> dList;
            lock (derivedNames)
            {
                dList = new List<string>(derivedNames);
            }
            foreach (var dName in dList)
            {
                results.AddRange(FindDefinitions(dName));
            }
        }

        // 2. symbolName がインターフェースや基底クラス内のメンバーの場合:
        // その実装先クラスから同名メンバーを探す
        var matchingSymbols = FindSymbolsByName(symbolName);
        foreach (var sym in matchingSymbols)
        {
            if (!string.IsNullOrEmpty(sym.ContainingType) &&
                _baseToDerivedTypes.TryGetValue(sym.ContainingType, out var implementors))
            {
                List<string> impList;
                lock (implementors)
                {
                    impList = new List<string>(implementors);
                }
                foreach (var imp in impList)
                {
                    var impMembers = FindMembersByContainingType(imp);
                    foreach (var m in impMembers.Where(m => m.Name == symbolName))
                    {
                        results.Add(new Location(m.Uri, m.Range));
                    }
                }
            }
        }

        return results;
    }

    public List<Location> FindDeclarations(string symbolName)
    {
        var matchingSymbols = FindSymbolsByName(symbolName);
        foreach (var sym in matchingSymbols)
        {
            if (!string.IsNullOrEmpty(sym.ContainingType))
            {
                var baseTypes = GetAllBaseTypes(sym.ContainingType);
                foreach (var baseType in baseTypes)
                {
                    var baseMembers = FindMembersByContainingType(baseType);
                    var matched = baseMembers.Where(m => m.Name == symbolName).ToList();
                    if (matched.Count > 0)
                    {
                        return matched.Select(m => new Location(m.Uri, m.Range)).ToList();
                    }
                }
            }
        }

        return FindDefinitions(symbolName);
    }

    public int SymbolCount => _symbolsById.Count;
}
