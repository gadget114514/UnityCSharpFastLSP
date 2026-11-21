using ProtocolRange = UnityCSharpFastLSP.Protocol.Range;
using UnityCSharpFastLSP.Protocol;

namespace UnityCSharpFastLSP.Semantics;

public readonly record struct Symbol(
    int Id,
    int NameId,
    string Name,
    SymbolKind Kind,
    string Uri,
    ProtocolRange Range,
    string? Signature = null,
    string? ContainingType = null,
    bool IsStatic = false
);
