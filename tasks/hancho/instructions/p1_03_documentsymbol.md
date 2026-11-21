# Worker Instruction: CARD-P1-03 DocumentSymbol 実装

## 目的
`src/UnityCSharpFastLSP.Syntax/` に、CST 宣言ノードから階層型 `DocumentSymbol` ツリーを生成する機能を実装する。

## 要件
1. 対象宣言ノード:
   - Namespace, Class, Struct, Interface, Enum, Record, Method, Constructor, Property, Field, EnumMember
2. 識別子取得ロジック（`FindIdentifier`）を用いて正確に名前を抽出。属性名や戻り値型と混同しないこと。
3. スパンの包含関係に基づき、階層ツリー（Namespace → Class → Member）を構築する。
4. LINQ や不要な一時コレクションのヒープ割り当てを避ける。
5. `dotnet build src/UnityCSharpFastLSP.Syntax/ -c Release` がパスすること。
