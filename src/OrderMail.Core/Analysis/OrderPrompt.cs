using System.Globalization;
using System.Text;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Analysis;

/// <summary>A past mail and the answer a person confirmed for it; sent to the AI as a worked example.</summary>
public sealed record AnalysisExample(MailInput Mail, OrderExtraction Expected);

/// <summary>
/// Builds the chat messages. Only <see cref="AiSettings.Instructions"/> is user-editable;
/// the context (own company, calendar) and the output contract are always appended so parsing keeps working.
/// </summary>
public static class OrderPrompt
{
    public const int MaxBodyChars = 8000;
    public const int MaxExampleBodyChars = 1500;

    public const string DefaultInstructions = """
        # 判定基準
        - 発注: 商品と数量を示して、購入・納品・手配を依頼または確定しているメール。言い回しや敬語の程度は問わない。追加注文や、問いかけの形の依頼（「手配していただけますか？」）も発注。
        - 発注ではない: 見積依頼、価格・在庫・納期の問い合わせ、発送状況の確認、キャンセル・変更の依頼、請求・支払いの連絡、お礼、営業・宣伝、システム通知、社内連絡など。
        - 引用部分（行頭が「>」）は過去のやり取りなので、判定と抽出には使わない。

        # 抽出のルール
        - customer_company と customer_name は発注する側（差出人）のもの。宛名（「御中」「様」「さま」が付く相手）は当社なので使わない。署名や名乗り（「〇〇の△△です」）から読み取る。
        - 品名は本文の表記どおりに書き、「品番」「品名」などの見出し語は付けない。
        - 数量と単位は分ける（「50本」なら quantity は 50、unit は "本"）。本文に単位があれば必ず unit に入れる。
        - delivery_date は、メールの後に「日付の手がかり」があればその日付を使う。なければ「前提」のカレンダーで日付を求める。「来週中」「なるべく早く」のように日が決まらない表現は null にして、原文を notes に書く。
        - notes には配送方法・価格条件・同梱の指示など、明細以外の依頼事項を書く。
        - メールに書かれていないことは推測で埋めず null にする。

        # 自信度（confidence）
        - is_order の判定が正しい確からしさを 0〜1 で表す。発注である確率ではない。
        - 発注ではないとはっきり分かるメール（宣伝・社内連絡など）は、is_order を false、confidence を 0.9 以上にする。
        - 発注か問い合わせか迷うなど、判断に迷う点があれば 0.5〜0.7 にする。
        """;

    private const string OutputSpec = """
        # 出力
        次のキーを持つ JSON オブジェクトだけを出力する（説明文やコードブロックは付けない）。
        - is_order: 発注なら true
        - confidence: is_order の判定が正しい確からしさ（0〜1）
        - reason: 判定理由（日本語で1〜2文）
        - customer_company: 発注元の会社名（不明なら null）
        - customer_name: 発注元の担当者名（不明なら null）
        - delivery_date: 希望納期（YYYY-MM-DD。不明なら null）
        - notes: 補足（なければ null）
        - items: 明細の配列。各要素は product_name（品名）、quantity（数値）、unit（単位。不明なら null）、unit_price（税抜単価の数値。メールに書かれていなければ null）、product_code（商品コード）。発注でなければ空配列。
        - product_code: メールの後に「商品マスタの候補」があり、同じ商品が含まれていればそのコード。色・サイズ・容量・長さが違う商品は選ばない。候補がない、または該当しないときは null（コードを推測で作らない）。
        """;

    private const int MaxProductLines = 20;

    private const string ExamplesNote = """
        # 手本
        この後の会話の前半は、担当者が確認した過去のメールと正しい出力です。取引先ごとの書き方や品名の表記の参考にし、最後のメールを解析してください。
        """;

    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja-JP");

    public static string Instructions(AiSettings settings) =>
        string.IsNullOrWhiteSpace(settings.Instructions) ? DefaultInstructions : settings.Instructions.Trim();

    public static string System(AiSettings settings, DateTime receivedLocal, bool hasExamples)
    {
        var parts = new List<string>
        {
            "あなたは受発注業務のアシスタントです。受信メールが「当社への発注（注文）」かどうかを判定し、発注であれば内容を抽出してください。",
            Instructions(settings),
            Context(settings.OwnCompanyName, DateOnly.FromDateTime(receivedLocal)),
            OutputSpec,
        };
        if (hasExamples) parts.Add(ExamplesNote);
        return string.Join("\n\n", parts);
    }

    public static string User(MailInput mail, int maxBodyChars = MaxBodyChars, IReadOnlyList<CatalogProduct>? products = null)
    {
        var text = MailText(mail, maxBodyChars);
        if (products is not { Count: > 0 }) return text;
        var lines = products.Take(MaxProductLines).Select(p =>
        {
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.Unit)) details.Add("単位: " + p.Unit);
            if (p.UnitPrice is { } price) details.Add($"標準単価: {price:#,0.##}円");
            return $"- {p.Code}: {p.Name}" + (details.Count > 0 ? $"（{string.Join("、", details)}）" : "");
        });
        return text + "\n\n商品マスタの候補（本文の品名からアプリが検索済み。似た別商品も含まれる）:\n" + string.Join("\n", lines);
    }

    private static string MailText(MailInput mail, int maxBodyChars)
    {
        var received = mail.ReceivedAtUtc.ToLocalTime();
        var body = mail.Body.Length > maxBodyChars ? mail.Body[..maxBodyChars] + "\n（以下省略）" : mail.Body;
        var text = $"""
            差出人: {mail.FromName} <{mail.FromAddress}>
            件名: {mail.Subject}
            受信日時: {received:yyyy-MM-dd HH:mm}

            本文:
            {body}
            """;
        var hints = DateHints.Find(mail.Subject + "\n" + mail.Body, DateOnly.FromDateTime(received));
        return hints.Count == 0 ? text : text + "\n\n日付の手がかり（受信日からアプリが計算済み）:\n" + string.Join("\n", hints.Select(h => "- " + h));
    }

    /// <summary>True when the extracted customer is actually the receiving company (the addressee).</summary>
    public static bool IsOwnCompany(string? company, string ownCompanyName)
    {
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(ownCompanyName)) return false;
        return Compact(company).Contains(Compact(ownCompanyName), StringComparison.OrdinalIgnoreCase);
    }

    private static string Compact(string s) =>
        new(s.Normalize(NormalizationForm.FormKC).Where(c => !char.IsWhiteSpace(c)).ToArray());

    public static string Answer(OrderExtraction expected) => ExtractionJson.Serialize(expected, indented: false);

    private static string Context(string ownCompany, DateOnly received)
    {
        var monday = received.AddDays(-(((int)received.DayOfWeek + 6) % 7));
        string[] weeks = ["今週", "来週", "再来週"];
        var calendar = weeks.Select((label, w) =>
            $"  {label}: " + string.Join(" ", Enumerable.Range(0, 7).Select(d => Day(monday.AddDays(w * 7 + d)))));
        var company = string.IsNullOrWhiteSpace(ownCompany)
            ? "- 当社はこのメールの受信者（発注を受ける側）。"
            : $"- 当社は「{ownCompany.Trim()}」で、このメールの受信者（発注を受ける側）。";
        return $"""
            # 前提
            {company}
            - 受信日: {Day(received)}
            - カレンダー（週は月曜始まり）:
            {string.Join("\n", calendar)}
            """;
    }

    public static string Day(DateOnly d) =>
        d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "(" + d.ToString("ddd", Japanese) + ")";
}
