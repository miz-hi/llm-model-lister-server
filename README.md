# Model Lister

登録した OpenAI 互換 LLM サーバー(URL + ポート)にアクセスして公開モデルを一覧表示し、
さらに **OpenAI 互換のルーティングゲートウェイ** としてモデルを実サーバーへ転送するアプリ。

**単一 C# ファイルを Windows 標準の C# コンパイラ(csc.exe)でビルドした単一 exe**。
追加インストール不要 — 実行には Windows 標準の .NET Framework 4.x のみを使用し、
サーバー側へのフレームワーク追加は一切不要。

```
┌────────────┐  /v1/models 定期クロール ┌────────────────────────┐  自動ルーティング  ┌──────────────┐
│ 各 llama   │ ◄────(既定60秒)──────│ model-lister-server.exe │ ◄── /v1/chat/ ── │ OpenAI 互換  │
│.cpp 等の   │                     │ (単一 exe・TcpListener) │    completions    │ クライアント │
│ LLM サーバー│ ──────応答─────────► │  ダッシュボードも内蔵    │   (SSE 透過)      │              │
└────────────┘  増減をメモリログへ   └────────────────────────┘                    └──────────────┘
```

## ファイル構成

```
server/
  server.cs            サーバー本体(単一 C# ファイル)
  build.ps1            Windows 標準 csc.exe でビルド(追加インストーラ不要)
  www/index.html       ダッシュボード(ビルド時に exe へ埋め込まれる)
  config.json          ポート・登録サーバー一覧(初回起動時に自動生成)
```

## 実行(ビルド済み exe を使う)

リポジトリにはビルド済みの `server/model-lister-server.exe`(約 88 KB)を同梱しています。
展開してそのまま起動できます:

```powershell
cd server
.\model-lister-server.exe
# → ダッシュボード: http://localhost:8787/
```

- ダウンロードした exe が SmartScreen / ウイルス対策に止められた場合は、
  ファイルを右クリック→プロパティ→「許可する」、または
  `Unblock-File .\model-lister-server.exe` を実行してください
- 設定は初回起動時に `config.json` として自動生成されます(サンプル 1 台付き。UI から差し替え可)

## ビルド(自分でビルドする場合)

Windows 標準の C# コンパイラを使うため、追加の SDK やインストーラは不要です。

```powershell
cd server
powershell -ExecutionPolicy Bypass -File .\build.ps1
# → model-lister-server.exe 完成
```

**ソース(server.cs / www/index.html)を変更したら、build.ps1 で exe を再生成して
コミットし直してください** — UI(index.html)はビルド時に exe へ埋め込まれるため、
exe の再生成を忘れると配布バイナリの画面が古くなります。

## 起動オプションと設定の引き継ぎ

- `--port <番号>` でポートを変えられます(指定は config.json に保存され、次回以降も引き継がれる)
- `config.json` が無い状態で起動するとサンプル 1 台付きで自動生成されます
  (UI から登録変更すると同じ場所に保存される)
- 生 TcpListener のため管理者権限・URL ACL 不要。ログはメモリ内のみ(`GET /api/logs`)

## ダッシュボード

1. 「登録サーバー」に名前と URL(`http://host:port` や `…/v1`、スキーム省略可)を登録(保存で config.json に書き込み)
2. 起動/「すべて取得」で全サーバーの `/v1/models` をクロールし、モデル ID・状態(loaded 等)・
   コンテキスト長・モダリティを一覧表示。上部フィルタで横断検索
3. 「ゲートウェイ」セクションにエンドポイントとルーティングテーブル(モデル→提供サーバー)を表示

## OpenAI 互換ゲートウェイ(ルーティングプロキシ)

登録済みクロール結果を使い、モデルをそのモデルを持つサーバーへ自動転送する。

```bash
# モデル名を指定するだけで、そのモデルを持つサーバーへ自動転送される
curl http://localhost:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model": "glm-5.3-flash", "messages": [{"role":"user","content":"こんにちは"}]}'

# サーバーに設定したエイリアスでも呼べる(1サーバー1モデル前提)
curl http://localhost:8787/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model": "my-alias", "messages": [{"role":"user","content":"こんにちは"}]}'
```

- 対応: `POST /v1/chat/completions` `/v1/completions` `/v1/embeddings` `/v1/rerank`
  (`stream: true` の SSE はチャンク転送で透過)
- `GET /v1/models` … 全サーバーのモデルを統合した一覧(`owned_by` に提供サーバー ID)。
  エイリアスも `alias_of` 付きで model として並ぶので、クライアントのモデルピッカーにそのまま出る
- **モデルエイリアス**: 各サーバーの `alias` に英数字の名前を付けると、そのサーバーのモデルの
  別名として使える(1サーバー1モデル前提。複数返すサーバーは最初の1件に紐付け)。
  プロバイダ側で差し替えられても**エイリアス名は変わらない**ので、クライアント側の設定は不要なまま
- 名前の解決順序: `ユーザー設定エイリアス` → `モデルID完全一致` → `サーバーID/モデルID`
- 同名モデルが複数サーバーにある場合は `サーバーID/モデルID` 形式で指定。
  曖昧な名前をそのまま渡すとエラーと候補一覧を返す
- **定期クロール**: 既定 60 秒で全サーバーの `/v1/models` を再取得し、ルーティングテーブルを更新。
  プロバイダ側のモデル増減・エイリアスの紐替えはメモリログに記録される(`GET /api/logs`)。
  落ちたサーバーのモデル/エイリアスは一時的に一覧から消え、復帰すると自動で戻る
- **落ちたサーバーだけ一時無効**: 応答の無いサーバーはサーキットブレーカのように 15 秒から最大 10 分へ
  指数バックオフで再試行を待ち、その間そのサーバーだけプロープをスキップする。
  他のサーバーには影響せず、リクエストはキャッシュ済みのテーブルで即応する
  (クロール待ちで全体が止まることはない)。無効中のサーバーは
  `GET /api/routes` の `servers[]`(`down` / `retryInSec`)とダッシュボードに「一時無効」と表示され、
  復帰すると `server recovered` がログに出て自動で戻る
- 手動操作不要。UI の「ルート再スキャン」でも即時更新できる
- ターゲットサーバーの API キーは config.json の設定を自動付与。未設定の場合のみクライアントの `Authorization` を透過
- 一時的な切断(TLS keep-alive の失くなった接続)には 1 回だけ再接続してリトライする

OpenAI SDK からは `base_url="http://<host>:8787/v1"` を向けるだけで使える。

## config.json

```json
{
  "port": 8787,
  "crawlIntervalSec": 60,
  "servers": [
    { "id": "sample", "name": "サンプル", "url": "http://localhost:8080/v1", "key": "", "alias": "" }
  ]
}
```

- `crawlIntervalSec`: 定期クロールの間隔秒(既定 60、`0` で無効、最小 10)
- `alias`: そのサーバーのモデルの別名(空で無効)。UI の「モデルエイリアス」欄からも設定できる

## API

- `GET /api/ping` / `GET /api/logs` … ヘルスチェック・メモリ内ログ
- `GET|POST /api/servers` … 登録一覧の読み書き
- `POST /api/probe` `POST /api/probe-all` … 任意 URL のクロール
- `GET /api/routes` … ルーティングテーブル・エイリアス一覧・各サーバーの健全性(`servers[]`)(`?refresh=1` で再スキャン)

## 常駐させる場合(参考)

単一 exe なので任意の方法で OK。例: スケジュールタスクでログオン時/起動時に起動。
HTTPS で公開したい場合は Caddy 等のリバースプロキシを前段に置いて
`reverse_proxy 127.0.0.1:8787`(SSE を透過させるため `flush_interval -1`)とするのが簡単です。
