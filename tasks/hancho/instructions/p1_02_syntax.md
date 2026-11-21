# Worker Instruction: CARD-P1-02 Syntax 層・構文診断実装

## 目的
`src/UnityCSharpFastLSP.Syntax/` に、CST 不変スナップショットおよび構文診断生成機能を実装する。

## 要件
1. `CsffSyntaxAdapter.cs` および `DocumentSnapshot.cs`:
   - `SyntaxTree`（csff）から必要な情報（ノード、トークン、LineMap、IsComplete、RecoveredTokens）を不変スナップショットへコピーする。
   - `UNITY_EDITOR` などの seeded symbols を渡せるようにする。
2. 構文診断の抽出:
   - `DocumentSnapshot.GetDiagnostics()`: CST の `IsComplete == false` や `RecoveredTokens` を検出し、`Diagnostic` オブジェクトを即座に生成する（セマンティック解析を通さない）。
3. 包含ノードの探索:
   - 指定オフセットの最小包含ノードを二分探索で高速に特定するヘルパーを用意する。
4. `dotnet build src/UnityCSharpFastLSP.Syntax/ -c Release` がエラー・警告 0 で成功すること。
