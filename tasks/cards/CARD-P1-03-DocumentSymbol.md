# [CARD-P1-03] DocumentSymbol Provider

- **対象プロジェクト**: `src/UnityCSharpFastLSP.Syntax/` / `src/UnityCSharpFastLSP.Protocol/`
- **依存**: CARD-P1-01, CARD-P1-02
- **優先度**: 中〜高（Phase 1 必須LSP機能）

## 1. 目的と背景
VS Code 等のエディタのアウトラインやパンくずリスト（Breadcrumbs）に表示する `textDocument/documentSymbol` を実装する。
csff の CST 宣言ノードから、階層構造（Namespace -> Class -> Method/Field/Property）を持つ `DocumentSymbol` ツリーをアロケーションを抑えて構築する。

## 2. 参照設計書
- `doc/design.md` セクション 4（NodeKind/TokenKind は Roslyn SyntaxKind と同一）、セクション 8（documentSymbol / workspaceSymbol）
- `doc/20261121-phase0.md` セクション 2（CST ダンプ検証: `CsffSyntaxAdapter.FindIdentifier`）

## 3. 実装要件

### 3.1 宣言ノードの抽出と識別子取得
- 以下の宣言ノード（Roslyn と同一の `SyntaxKind` 数値）を走査する:
  - `NamespaceDeclaration`
  - `ClassDeclaration`, `StructDeclaration`, `InterfaceDeclaration`, `EnumDeclaration`, `RecordDeclaration`
  - `MethodDeclaration`, `ConstructorDeclaration`
  - `PropertyDeclaration`, `FieldDeclaration`, `EnumMemberDeclaration`
- 各宣言ノードの名称（Identifier）を正しく抽出する:
  - 属性名（`[SerializeField]` 等）や戻り値型（`void`, `int` 等）を誤って拾わないよう、`FindIdentifier` ロジックで種別に応じたシグネチャ・名前を特定する。

### 3.2 階層構造（Hierarchical DocumentSymbol）の構築
- 親子関係（包含関係）をスパンの入れ子（Parent range contains Child range）に基づいてツリー化する。
- `DocumentSymbol` オブジェクトの構成:
  - `name`: 宣言の識別子名
  - `detail`: 型シグネチャまたは修飾子（例: `void ()`, `int`, etc.）
  - `kind`: `SymbolKind`（Namespace=3, Class=5, Method=6, Property=7, Field=8, Interface=11, etc.）
  - `range`: 属性やドキュメントコメントを含む全体の `Range`
  - `selectionRange`: 識別子（Identifier トークン）のみの `Range`
  - `children`: 内包するシンボルのリスト（なければ null または空リスト）

### 3.3 パフォーマンスとアロケーション
- 1 ファイルあたりの抽出時間を 100us 以下に収める。
- LINQ や不要な一時コレクションの生成を排除する。

## 4. 完了条件 (Definition of Done)
1. `DocumentSnapshot` または専用 Provider から階層型 `DocumentSymbol[]` を取得できる。
2. Unity スクリプト（例: `PlayerController.cs`）を対象に、クラスとメンバーが正しい階層・名前・Kind で返される。
3. `UnityCSharpFastLSP.Tests` に DocumentSymbol の構造検証テストが追加され、パスする。
