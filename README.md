# 発注メール AI 仕分け

受信メールを取り込み、文面がばらばらな発注メールを AI で読み取って発注データに登録する業務アプリです。
AI の判定に自信がないものは「要確認」に回し、担当者がブラウザ画面で確認・修正して確定します。

- **zip を解凍して exe を起動するだけで動作**（インストール不要・.NET 不要・DB サーバー不要）
- **AI は接続設定式**：Ollama / LM Studio などのローカル LLM（無料）や Gemini / OpenAI など、OpenAI 互換 API ならどれでも利用可能
- **AI なしでも動作**：キーワードと書式パターンによるルールベース判定を内蔵（サンプル 20 通で正解率 100%）

## 画面

| 画面 | 内容 |
|---|---|
| 受信メール | 取り込んだメールの一覧。状態（要確認 / 発注 / 発注以外 / 解析失敗）と AI の自信度で絞り込み |
| メール詳細 | 左に本文、右に AI が抽出した発注内容。修正して「発注として登録」または「発注ではない」で確定 |
| 発注一覧 | 登録済みの発注（自動登録か人が確認済みか、金額、希望納期） |
| 設定 | メールの取得元、AI の接続先（プリセットとテスト解析）、AI への指示（プロンプト）の編集、確認済みメールでの評価、自動登録のしきい値 |

## 構成

```mermaid
flowchart LR
  B[ブラウザ] --> APP
  subgraph APP["OrderMail.exe"]
    UI[画面<br/>Blazor Server + Fluent UI]
    SVC[業務ロジック<br/>MailProcessor / ReviewService]
    W[定期取り込み<br/>MailPollingWorker]
  end
  UI --> SVC
  W --> SVC
  SVC --> SRC[メール取得<br/>.eml フォルダ / IMAP]
  SVC --> AI[解析<br/>OpenAI 互換 API / ルールベース]
  SVC --> DB[(SQLite<br/>data/ordermail.db)]
```

### 処理の流れ

1. **取り込み**：`.eml` フォルダまたは IMAP から新着メールを取得（`Message-ID` で重複除外、HTML メールはテキスト化）
2. **解析**：AI に JSON スキーマを指定して「発注か」「自信度」「会社名・担当者・希望納期・明細」を抽出
3. **振り分け**：自信度がしきい値（既定 0.8）以上なら自動で「発注」登録または「発注以外」。未満や明細なしは「要確認」
4. **人の確認**：詳細画面で修正して確定。確定済みのメールは再解析しても判定が上書きされない

### AI 接続の工夫

- 接続先ごとに構造化出力の対応が違うため、`json_schema` → `json_object` → 指定なし の順に自動で切り替え
- 出力が JSON として読めないときは、エラー内容を伝えて 1 回だけ再生成させる
- 数量が `"50本"` のような文字列、自信度が 0〜100 のような揺れも吸収してから保存
- 解析履歴（どの解析器で、何秒かかり、どう判定したか）をメールごとに保存し、接続先の比較に使える
- **手本**：担当者が確定したメールのうち、差出人が同じものや文面が似ているものを数件、「メールと正しい答え」の会話例として AI に渡す（モデルの再学習は不要で、確定するほど取引先ごとの書き方に合ってくる）
- **相対日付の計算**：「今週土曜」「来週金曜」「明日」などはアプリが受信日から日付に換算して AI に渡し、「来週中」のように日が決まらない表現はそう伝える（小さいローカル LLM は自力の日付計算を誤りやすいため）
- **当社名**：設定した当社名を AI に伝え、それでも会社名に当社名が返ってきたら空欄に戻す（宛名との取り違え対策）

### プロンプトの調整

設定画面の「AI への指示（プロンプト）」で、判定と抽出のルールを書き換えられます。出力形式（JSON のキー）と、当社名・受信日のカレンダーはアプリが自動で付け足すので、ルールを書き換えても解析結果を読み取れなくなることはありません。
「確認済みメールで評価」を押すと、担当者が確定したメールを画面上の設定（保存前でも可）で解析し直し、判定・明細・会社名・納期の一致率と、自動で確定できる割合を前回の結果と並べて表示します。結果は保存されず、メールの状態も変わりません。
- API キーと IMAP パスワードは Windows の DPAPI でユーザーごとに暗号化して保存

## 使い方

### 配布用 zip から

1. `OrderMail-win-x64.zip` を解凍して `OrderMail.exe` を起動（ブラウザが開きます）
2. 「受信メール」で「取り込み・解析」を押すと、同梱のサンプルメール 20 通を取り込みます

### AI をつなぐ例（無料）

- **Ollama**：[Ollama](https://ollama.com/) を入れて `ollama pull qwen2.5:7b` → 設定画面で「Ollama」プリセット
- **Gemini**：[Google AI Studio](https://aistudio.google.com/) で API キーを発行 → 「Gemini」プリセットにキーを入力

## 開発

必要なもの：[.NET 10 SDK](https://dotnet.microsoft.com/)

```powershell
dotnet run --project src/OrderMail.Web     # http://localhost:5080
dotnet test                                # テスト
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1   # artifacts\OrderMail-win-x64.zip を作成
```

`publish.ps1` は確認用に `OrderMail-latest\OrderMail.exe` も更新します（`data` フォルダは残します）。

| フォルダ | 内容 |
|---|---|
| `src/OrderMail.Core` | 取り込み・解析・DB・振り分けのロジック（画面に依存しない） |
| `src/OrderMail.Web` | Blazor Server の画面と起動処理 |
| `tests/OrderMail.Tests` | xUnit のテスト（AI 接続は偽の HTTP サーバーで検証） |
| `samples/mails` | デモ用の発注メール・発注以外のメール 20 通（`samples/labels.json` が正解ラベル） |
| `scripts/publish.ps1` | 配布用 zip の作成 |

## 技術スタック

C# / .NET 10、ASP.NET Core Blazor Server、Fluent UI Blazor、Entity Framework Core + SQLite、MailKit、xUnit
