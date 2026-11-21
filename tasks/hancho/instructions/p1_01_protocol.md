# Worker Instruction: CARD-P1-01 Protocol 基盤実装

## 目的
`src/UnityCSharpFastLSP.Protocol/` に、JSON-RPC 2.0 メッセージ処理、Source-generated DTO、Content-Length フレーミングパーサーを実装する。

## 要件
1. `src/UnityCSharpFastLSP.Protocol/` に以下のファイルを実装・整備する:
   - `ProtocolModels.cs`: `Position`, `Range`, `Location`, `Diagnostic`, `PublishDiagnosticsParams`, `DocumentSymbol`, `InitializeParams`, `InitializeResult`, `ServerCapabilities` 等の DTO 定義。
   - `LspJsonSerializerContext.cs`: Source-generated `JsonSerializerContext` を定義し、各モデルを `[JsonSerializable]` に登録。
   - `LspMessageReader.cs`: `Stream` から `Content-Length` を解析し、`ArrayPool<byte>` を使ってボディを読み取る。
   - `LspMessageWriter.cs`: `Stream`（stdout）へスレッドセーフに Content-Length フレーミングで書き込む。
2. ホットパスでのアロケーションを最小化し、リフレクションを使用しないこと。
3. `dotnet build src/UnityCSharpFastLSP.Protocol/ -c Release` がエラー・警告 0 で成功すること。
