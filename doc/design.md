Unity専用LSPサーバー 統合設計書
基盤技術: tree-sitter-c-sharp（同一リポジトリ配置・LLM非閲覧隔離） / Red-Green Tree / C++20 / LSP (JSON-RPC 2.0)

文書バージョン: 1.3

対象読者: コア開発者、言語解析担当、Unity 連携担当

1. 概要・目的
1.1 背景と課題
従来の Roslyn ベースの C# 言語サーバーは、Unity プロジェクトの大規模な .csproj、多数のアセンブリ定義（*.asmdef）、Library/PackageCache 内のパッケージ群の解決に時間がかかり、起動遅延やメモリ消費が大きくなりがちです。また、編集中で構文が不完全な状態であっても、Unity 固有の文脈（MonoBehaviour ライフサイクル、[SerializeField] / Inspector 紐付け、GetComponent<T>() 型推論、.unity / .prefab 参照）を低遅延で提示するニーズがあります。

1.2 目的
tree-sitter-c-sharp による超高速・耐障害性のある構文解析と、Red-Green Tree による不変・構造共有型 AST を組み合わせ、さらに Unity プロジェクト構造（.meta, .asmdef, シーン/プレハブ）を軽量にインデックス化することで、低遅延・低メモリ・高耐障害性な Unity 専用 LSP サーバーを実現します。

1.3 前提条件（tree-sitter-c-sharp の配置と LLM 非閲覧隔離）
tree-sitter-c-sharp のソースは同一リポジトリ内に配置する（例: vendor/tree-sitter-c-sharp/、vendor/tree-sitter/）。

LLM には tree-sitter-c-sharp（および tree-sitter 本体・.scm クエリ）のソースコードを読ませない。

同一リポジトリ内にソースが存在しても、LLM の読み取り・コンテキスト投入・検索対象から除外する（.aiignore / .cursorignore / エージェント設定・プロンプト制約で隔離）。

LLM の閲覧・編集対象は include/unity_lsp/、src/（vendor/ と src/backend/ts_bridge.* を除く）、tests/、docs/ に限定する。

vendor/ 配下のソースや tree-sitter C API（TSParser, TSTree, TSNode, TSQuery, TSInputEdit）はブリッジ層（src/backend/ts_bridge.*）内に閉じ込め、LLM からは include/unity_lsp/syntax_backend.hpp の抽象インターフェースのみを扱う。

1.4 スコープ
対象言語: C#（Unity 向けスクリプト）

通信プロトコル: Language Server Protocol (JSON-RPC 2.0 over stdio / TCP)

主要機能:

リアルタイム診断（構文エラー + Unity 固有静的解析）

定義ジャンプ / 参照検索（C# シンボル + .unity / .prefab 参照連携）

補完（C# 構文 + MonoBehaviour/ScriptableObject ライフサイクル・属性・GetComponent 補助）

ホバー情報（型情報・属性・シリアライズ状態・ドキュメントコメント）

ドキュメントシンボル / ワークスペースシンボル

2. システムアーキテクチャ
LSP クライアント（VS Code, Neovim, Zed 等）と StdioTransport を介して JSON-RPC 2.0 で通信し、内部を「LLM閲覧・編集対象」と「LLM非閲覧領域」に明確に分離します。

LSP Transport & Server Core (C++20) [LLM閲覧・編集対象]:

StdioTransport（Content-Length フレーミング、stdout は LSP 通信専用に厳格分離）

リクエスト / 通知ディスパッチャおよびキャンセレーション（std::stop_token）

Syntax & Red-Green Layer [LLM閲覧・編集対象]:

ISyntaxBackend（抽象インターフェース）からフラットな ParseEvent 列を受領

GreenBuilder & GreenInterner（不変 GreenNode の shared_ptr 共有・インターン化）

RedNode（親参照 + 絶対 UTF-8 / UTF-16 オフセットを持つ短寿命ビュー）

Unity Project Indexer & Semantic Engine [LLM閲覧・編集対象]:

Assets/、Packages/、ProjectSettings/ 走査（.meta GUID、.asmdef、.unity/.prefab、SymbolTable）

スコープ・型解決器および Unity 固有ルール（LifecycleRule, SerializeRule, ComponentRule）

LLM Non-Read Boundary [同一リポジトリ内に存在するが LLM には読ませない領域]:

vendor/tree-sitter/

vendor/tree-sitter-c-sharp/（parser.c, scanner.c, queries）

src/backend/ts_bridge.*（TSParser / TSTree C API ラッパー）

2.1 技術選定・依存関係
言語標準: C++20（std::filesystem, std::jthread, std::stop_token, std::shared_mutex, std::span, std::expected）

ビルドシステム: CMake 3.25+

コア依存ライブラリ:

tree-sitter（vendor/tree-sitter として同一リポジトリ配置・静的リンク）

tree-sitter-c-sharp（vendor/tree-sitter-c-sharp として同一リポジトリ配置・静的リンク）

nlohmann/json（JSON-RPC 2.0 シリアライズ/デシリアライズ）

spdlog（stderr またはファイルへのログ出力）

GoogleTest（単体・結合テスト）

重要制約:

stdio トランスポート利用時、std::cout / printf への出力は JSON-RPC ストリームを破損させます。ログはすべて spdlog 経由で stderr または専用ログファイルへ出力します。

vendor/ および src/backend/ts_bridge.* は同一リポジトリ内に置きますが、LLM のプロンプト・ツール読み取り・コンテキスト検索対象から除外します。

3. Red-Green Tree 設計
3.1 導入目的と役割分担
Roslyn や rust-analyzer と同様の Red-Green Tree を採用し、イミュータブルなスナップショット管理と差分更新時のアロケーション削減を行います。

Green Tree（不変・構造共有・相対位置）:

テキスト内容と構文構造を表すイミュータブルなノード群。

子ノードは std::shared_ptr<const GreenNode> で共有され、親ポインタや絶対座標を持たないため、編集時に変更パス上のノードのみを再構築し、未変更サブツリーを再利用します。

Green Interner により、同一種別・同一テキストの葉ノード（キーワード、演算子、識別子、空白・改行など）を重複排除します。

Red Tree（短寿命・絶対位置・親参照）:

GreenNode をラップし、親ノードへの参照とルートからの絶対 UTF-8 / UTF-16 オフセットを提供します。

LSP リクエスト処理ごとに DocumentSnapshot からオンデマンド生成して走査します。

3.2 tree-sitter-c-sharp とのハイブリッド連携（ブラックボックス境界経由）
vendor/tree-sitter-c-sharp の内部構造は src/backend/ts_bridge.* に閉じ込め、LLM には ISyntaxBackend 経由で深さ優先のフラットなイベント列（ParseEvent）として渡します。

一次差分パース: didChange 受信時、ISyntaxBackend::parse_incremental() が内部の TSTree に差分編集を適用し、変更範囲のイベント列を返却します。

Green Tree 差分構築 (GreenBuilder): ParseEvent 列をスタックマシンで処理し、変更されていないサブツリーは既存 DocumentSnapshot の GreenNodePtr を再利用しながら新しい GreenNodePtr ツリーを構築します。

スナップショット差し替え: std::shared_ptr<const DocumentSnapshot> をアトミックに更新し、進行中のワーカーは旧スナップショットを安全に完了させます。

3.3 ブラックボックス境界インターフェース（include/unity_lsp/syntax_backend.hpp）
LLM が参照・利用する唯一の構文バックエンド契約として、以下の型とインターフェースを定義します。

SyntaxKind 列挙型: Unknown, Error, Missing, Token, Trivia, CompilationUnit, NamespaceDeclaration, ClassDeclaration, StructDeclaration, InterfaceDeclaration, EnumDeclaration, MethodDeclaration, FieldDeclaration, PropertyDeclaration, AttributeList, InvocationExpression, IdentifierName, Block

ParseEventKind 列挙型: EnterNode, LeafNode, ExitNode

TextEdit 構造体: start_utf8, old_end_utf8, new_end_utf8, start_utf16, old_end_utf16, new_end_utf16

ParseEvent 構造体: event, kind, start_utf8, end_utf8, is_error, is_missing

ParseResult 構造体: reused_previous, events

ISyntaxBackend 抽象クラス:

virtual ParseResult parse_initial(std::string_view source) = 0;

virtual ParseResult parse_incremental(std::string_view new_source, std::span<const TextEdit> edits) = 0;

ファクトリ関数: std::unique_ptr<ISyntaxBackend> create_csharp_syntax_backend();（src/backend/ts_bridge.cpp 側で実装）

3.4 Red-Green Tree コアデータ構造（C++20 仕様）
TextLen: utf8_bytes, utf16_units, newlines, last_line_utf16_units を保持し、from_utf8() および加算演算子を提供。

GreenChild: GreenNodePtr node, uint32_t utf8_offset, uint32_t utf16_offset。

GreenNode:

葉ノードコンストラクタ: (SyntaxKind kind, std::string text, bool is_error = false, bool is_missing = false)

内部ノードコンストラクタ: (SyntaxKind kind, std::vector<GreenChild> children, bool has_error = false)

アクセサ: kind(), is_token(), has_error(), is_missing(), text_len(), text(), children()。

3.5 UTF-8 バイト位置 ↔ LSP UTF-16 位置変換
LSP の Position（line, character）は UTF-16 コード単位、ISyntaxBackend / GreenNode のオフセットは UTF-8 バイト を基準とします。TextLen と GreenChild の utf8_offset / utf16_offset を同時に保持することで、日本語コメントや絵文字を含む Unity プロジェクトでも座標ズレを防ぎます。

4. ディレクトリ構成（LLM閲覧境界の明示）
CMakeLists.txt

CMakePresets.json

.aiignore（vendor/ および src/backend/ts_bridge.* を除外）

include/unity_lsp/syntax_backend.hpp — [LLM閲覧可] 唯一の構文バックエンド契約

vendor/tree-sitter/ — [LLM閲覧禁止] 同一リポジトリ配置の外部ソース

vendor/tree-sitter-c-sharp/ — [LLM閲覧禁止] 同一リポジトリ配置の外部ソース

src/main.cpp — [LLM閲覧可]

src/backend/ts_bridge.hpp, src/backend/ts_bridge.cpp — [LLM閲覧禁止] tree-sitter C API ブリッジ

src/transport/stdio_transport.hpp, src/transport/stdio_transport.cpp — [LLM閲覧可]

src/lsp/protocol.hpp, src/lsp/json_codec.*, src/lsp/server.* — [LLM閲覧可]

src/syntax/green_node.*, src/syntax/green_interner.*, src/syntax/green_builder.*, src/syntax/red_node.*, src/syntax/document_snapshot.hpp, src/syntax/syntax_engine.* — [LLM閲覧可]

src/unity/project_indexer.*, src/unity/symbol_table.*, src/unity/rules/unity_rule.hpp, lifecycle_rule.cpp, serialize_rule.cpp, component_rule.cpp — [LLM閲覧可]

src/providers/diagnostics_provider.cpp, definition_provider.cpp, references_provider.cpp, completion_provider.cpp, hover_provider.cpp — [LLM閲覧可]

tests/green_tree_test.cpp, red_node_test.cpp, syntax_engine_test.cpp, lsp_integration_test.cpp — [LLM閲覧可]

5. Unity プロジェクトインデクサ & 意味解析設計
5.1 ProjectIndexer
ワークスペースルート配下の Assets/、Packages/、ProjectSettings/ を走査し、Unity 固有のメタデータをインデックス化します。

*.meta: guid, MonoImporter / AssemblyDefinitionImporter を抽出し、GUID と .cs / .asmdef を紐付けます。

*.asmdef: name, references, includePlatforms, defineConstraints を抽出し、型可視性・名前空間解決スコープを制御します。

*.cs: 型定義（class, struct, interface, enum）、基底型、メンバシグネチャを抽出し、ワークスペース全体のシンボル索引を構築します。

*.unity, *.prefab: MonoBehaviour の m_Script: {guid: ...} と行番号を抽出し、シーン/プレハブ内参照検索・未使用スクリプト検出に利用します。

ProjectSettings/ProjectVersion.txt: Unity エディタバージョンを抽出し、API 互換性ヒントに利用します。

変更通知（workspace/didChangeWatchedFiles）を受信した際は、該当ファイルのみ差分再インデックスを行います。

5.2 IUnityRule プラグイン機構
Unity 固有の静的解析・補完拡張を IUnityRule 実装クラスとして追加します。

LifecycleRule: MonoBehaviour / ScriptableObject / EditorWindow 派生クラスにおける Awake, Start, Update, FixedUpdate, LateUpdate, OnEnable, OnDisable, OnDestroy, OnValidate, Reset のシグネチャ検証と補完候補提供。

SerializeRule: [SerializeField] 付与 private フィールドに対する未使用警告の抑制、[SerializeReference] の型制約チェックおよび Unity 非シリアライズ型（ジェネリック直下、辞書等）へのヒント表示。

ComponentRule: GetComponent<T>() / TryGetComponent<T>(out var x) / AddComponent<T>() の戻り値型推論、[RequireComponent(typeof(T))] が付与されたクラスにおける GetComponent<T>() の null 安全性ヒント。

6. スレッドモデルと並行制御
Main / IO Thread: StdioTransport::read_loop() で Content-Length フレーミングを読み、有界 MPSC キューへ投入します（stdout は LSP 出力専用）。

Worker Pool (std::jthread x N): DocumentSnapshot の差分更新と LSP リクエスト処理を実行します。SymbolTable / ProjectIndexer は std::shared_mutex で読み取り共有し、DocumentSnapshot は std::shared_ptr でアトミックに差し替えます。

キャンセレーション ($/cancelRequest): リクエストごとに std::stop_source を発行し、AST 走査やファイル検索時に stop_token.stop_requested() を確認して早期中断します。

診断デバウンス: didChange 時はスナップショットのみ即時更新し、診断再計算は 30ms 程度のデバウンスタイマー経由でワーカーへ投入します。

高速パス: GreenNode::has_error() == false の場合、構文エラーノード走査をスキップします。

7. LSP 機能仕様
initialize / initialized: ケーパビリティ宣言、initializationOptions（unityProjectPath, enableUnityRules）受領、バックグラウンド初期スキャン開始。

textDocument/didOpen / didChange: ISyntaxBackend 差分パース + GreenBuilder 差分構築（構文エラーがあっても周辺の解析を継続）。

textDocument/publishDiagnostics: Error / Missing ノード + IUnityRule 診断を返却（ライフサイクル誤記、シリアライズ警告、アセット未参照通知）。

textDocument/definition: 同一ファイル・ワークスペース内の型/メンバ定義へジャンプ（基底クラス・インターフェース実装候補の提示）。

textDocument/references: シンボル参照箇所を検索（C# 参照 + .unity / .prefab 内の m_Script GUID 参照）。

textDocument/completion: カーソル位置の構文コンテキストに応じた候補返却（MonoBehaviour ライフサイクルメソッド補完、属性補完）。

textDocument/hover: 型情報・属性・ドキュメントコメント表示（基底クラス階層や Inspector シリアライズ状態の概要）。

8. 非機能要件・テスト戦略
8.1 非機能要件
LLM 非閲覧隔離要件: vendor/ と src/backend/ts_bridge.* は同一リポジトリ内に存在するが、LLM の読み取り・参照対象外とする。

応答性: didChange → publishDiagnostics / completion 応答まで 10ms〜30ms 以内。

メモリ使用量: 常時 200MB 以下（不変 GreenNode 共有とシンボルメタデータ保持）。

8.2 テスト戦略
green_tree_test.cpp: GreenInterner のポインタ共有、差分再構築時のサブツリー再利用率。

red_node_test.cpp: UTF-8 / UTF-16 座標変換精度、親遡り・トークン検索。

syntax_engine_test.cpp: ISyntaxBackend のモック実装（FakeSyntaxBackend）を用いた SyntaxEngine 単体テスト（vendor/ 非依存で高速実行可能）。

lsp_integration_test.cpp: ダミー Unity プロジェクトと create_csharp_syntax_backend() を結合した E2E テスト。
