# Unity 専用 LSP サーバー 設計レビュー（doc/design.md v1.3 に対して）

確認日: 2026-11-17

## 1. 結論

技術的には実現可能。ただし設計書の全機能を一度に作るのは大規模（源代码およそ 20〜30 ファイル＋ビルド基盤＋テスト）であり、段階実装が必須。まずは難所である差分パース／GreenBuilder の再利用を検証するフェーズ1 PoC から着手する方針に決定した。

## 2. 確認したリポジトリ状態・環境

- リポジトリは実質ゼロから。`doc/`（`design.md`, `investigation.md`）と `vendor/tree-sitter-c-sharp/` のみが存在し、`CMakeLists.txt`、全ソース、テストは未作成。
- `vendor/tree-sitter/`（tree-sitter ランタイム本体）は**未配置**。`vendor/tree-sitter-c-sharp/src/tree_sitter/` には `alloc.h`, `array.h`, `parser.h` のヘッダのみがあり、`ts_parser_*` 等をリンクするには別途ランタイム `lib.c` が必要。
- ユーザーにより、`vendor/tree-sitter`（ランタイム）は未取得、`vendor/tree-sitter-c-sharp` は取得済みであることが確認された（2026-11-17）。ランタイムは新たに取得する必要がある。
- `.gitignore` は Zig 用（`.zig-cache/`, `zig-out/`, `zig-pkg/`, `*.o`）のみ。設計にある `.aiignore` は未作成。
- 設計書では `docs/` と記載されているが、実リポジトリでは `doc/`（表記差のみ）。

## 3. 主要な論点・リスク

1. **GreenBuilder の差分再利用が最大の難所** (design.md:118-120)。tree-sitter は変更レンジは出力するが「どのノードが再利用可能か」は出力しない。旧ツリーと新ツリーを照合する独自アルゴリズム（親内相対オフセット＋kind＋葉テキストによる比較）が必要。
2. **UTF-8 ↔ UTF-16 座標変換** (design.md:158-159)。`TSInputEdit` はバイト位置、LSP `Position` は UTF-16 コード単位。設計の `TextEdit` に両方の座標を持たせる方針で対応可能だが、日本語コメント・絵文字を含む差分更新のテストが必須。
3. **セマンティクスは近似になる**。軽量 `SymbolTable` による方式では definition/references は単純ケース中心。完全な C# 型推論（ジェネリクス、LINQ、拡張メソッド、Source Generator）はこの構成では非現実的であり、スコープに明記すべき。
4. **依存取得方法が設計で未定義**。nlohmann/json、spdlog、GoogleTest（＋ tree-sitter ランタイム）の入手方法を決める必要がある。
5. **レイテンシ目標の矛盾**。30ms の診断デバウンス (design.md:222) と didChange から 10〜30ms 以内という応答目標 (design.md:245) は、計測起点を定義しないと整合しない。
6. **LLM 非閲覧隔離は慣習的制約**。`vendor/` と `src/backend/ts_bridge.*` を `.aiignore` 等で除外しても技術的強制ではない。レビュー時点では vendor のファイル名のみを確認し、ソース本文は未読。

## 4. 決定事項（ユーザー確認済み）

- 今回の範囲: **フェーズ1の PoC から**。全体実装は後続フェーズに回す。
- tree-sitter 以外の依存（nlohmann/json, spdlog, GoogleTest）は **CMake FetchContent で自動取得**。ビルド成果物は `build/_deps` に置き、LLM 閲覧対象のソースツリーには混ぜない。

## 5. フェーズ1 PoC 実装計画

### 5.1 実装範囲（依存順）

1. ビルド基盤: `CMakeLists.txt`, `CMakePresets.json`, `.aiignore`。`CMAKE_CXX_STANDARD 20`、FetchContent で依存を取得。
2. 境界インターフェース: `include/unity_lsp/syntax_backend.hpp`。設計 3.3 の `SyntaxKind`, `ParseEventKind`, `TextEdit`, `ParseEvent`, `ParseResult`, `ISyntaxBackend`, `create_csharp_syntax_backend()` を定義。
3. ブリッジ（LLM 非閲覧）: `src/backend/ts_bridge.{hpp,cpp}`。`TSParser/TSTree/TSInputEdit` を閉じ込め、DFS の `ParseEvent` を生成。バイト→UTF-16 変換もここで実施。
4. 構文コア: `green_node`, `green_interner`, `green_builder`, `red_node`, `document_snapshot.hpp`, `syntax_engine`。
5. LSP 最小ループ: `src/transport/stdio_transport.{hpp,cpp}`（Content-Length フレーミング、stdout 専有、ログは spdlog 経由で stderr）、`src/lsp/protocol.hpp`, `json_codec`, `server`（initialize, initialized, didOpen/didChange, publishDiagnostics, documentSymbol, shutdown/exit, $/cancelRequest）、`src/main.cpp`。
6. テスト: `green_tree_test.cpp`（インターン同一ポインタとサブツリー再利用率）, `red_node_test.cpp`（日本語/絵文字の座標変換）, `syntax_engine_test.cpp`（`FakeSyntaxBackend` で vendor 非依存）, `lsp_integration_test.cpp`（実ブリッジ＋ダミー Unity プロジェクトで E2E）。

### 5.2 PoC の割り切り

- **差分イベント列方式は一旦採用しない**。PoC では `parse_incremental()` が全イベント列を返し、GreenBuilder が旧ツリーと「親内相対オフセット＋kind＋葉テキスト」で照合して再利用する。絶対位置で比較しないため編集後の座標シフトに強く、挙動を決定的にテストできる。変更イベント列への最適化はレイテンシ計測後の課題。
- 診断は構文エラー（Error/Missing ノード）のみ。Unity 固有ルール・プロジェクトインデクサは後フェーズ。
- パフォーマンス目標（10〜30ms / 200MB 以下）は PoC では目標として記載のみとし、計測は後フェーズで実施。

### 5.3 検証方法

- `cmake --preset default` → `cmake --build --preset default` → `ctest --preset default`
  （または `cmake -S . -B build && cmake --build build && ctest --test-dir build`）
- 日本語コメント・絵文字を含む C# ソースで `didChange` → `publishDiagnostics` の座標がズレないことを E2E で確認。

### 5.4 後続フェーズ

- フェーズ2: `ProjectIndexer` / `SymbolTable` / definition・references。
- フェーズ3: completion / hover / `IUnityRule`（LifecycleRule, SerializeRule, ComponentRule）。
- フェーズ4: 30ms デバウンス、キャンセレーション詳細、レイテンシ・メモリ計測とチューニング。

## 6. C++20 対応確認

- MSVC `19.44.35207`（Visual Studio 2022 17.14）および CMake `3.31.6` がインストール済み。
- STL の機能マクロを実ヘッダで確認: `__cpp_lib_expected 202211L`, `__cpp_lib_span 202002L`, `__cpp_lib_shared_mutex 201505L`, `__cpp_lib_filesystem 201703L`, `__cpp_lib_format 202304L`（`yvals_core.h`）。`std::jthread` / `std::stop_token` も同世代で利用可能。
- 設計が必要とする `std::expected`, `std::span`, `std::jthread`, `std::stop_token`, `std::shared_mutex`, `std::filesystem` はすべて揃っている。CMake で `CMAKE_CXX_STANDARD 20`（MSVC では `/std:c++20`）を指定すればビルド可能。
- なお plan mode 中はファイル作成が禁止されていたため、実コンパイル検証ではなく STL ヘッダのマクロ確認に留めた。実装開始後の初回 `cmake --build` で実検証する。

## 7. 未解決・要確認事項

- tree-sitter ランタイム（`lib.c`）の配置先: `vendor/tree-sitter` は未取得、`vendor/tree-sitter-c-sharp` は取得済み（ユーザー確認済み）。設計方針どおり `vendor/tree-sitter/` に配置するか、他の依存と同様に FetchContent で `build/_deps` に取得するかを決定する必要がある。
- stdio トランスポートで受け付ける LSP ヘッダ（`Content-Type` の扱い）と診断デバウンスの計測起点の定義。
- 隔離要件（`.aiignore` / エージェント設定）の実際の effectiveness。慣習であり技術的強制ではない点を認識しておく。
