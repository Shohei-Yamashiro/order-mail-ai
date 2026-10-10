using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OrderMail.Core.Analysis;
using OrderMail.Core.Catalog;
using OrderMail.Core.Domain;
using OrderMail.Core.Processing;
using OrderMail.Core.Settings;
using Xunit.Abstractions;

namespace OrderMail.Tests;

public class CatalogTests(ITestOutputHelper output)
{
    private static readonly Lazy<ProductIndex> SampleIndex = new(() =>
        ProductIndex.Create(ProductCsv.Parse(File.ReadAllText(Samples.ProductCsv)).Products));

    [Fact]
    [Spec(SpecCategories.Catalog, "サンプルの明細を商品マスタに照合する（AI なし）", "ルールベースで読んだサンプルの発注 11 通・23 明細すべてに、正解ラベルと同じ商品コードが付く。色・サイズ・長さ違いの似た商品には付かない。結果は付録に載せる。")]
    public void Sample_order_lines_are_matched_to_product_codes()
    {
        var labels = Samples.Labels.Where(l => l.IsOrder).ToList();
        var ok = 0;
        var total = 0;
        foreach (var label in labels)
        {
            var result = SampleIndex.Value.Resolve(RuleBasedOrderAnalyzer.Analyze(Samples.Input(label.File)));
            var codes = result.Items.Select(i => i.ProductCode ?? "(なし)").ToList();
            var same = codes.SequenceEqual(label.Codes!);
            total += label.Codes!.Count;
            ok += codes.Zip(label.Codes!).Count(p => p.First == p.Second);
            output.WriteLine($"{(same ? "OK " : "NG ")} {label.File,-30} {string.Join(" / ", result.Items.Select(i => $"{i.ProductName} → {i.ProductCode ?? "(なし)"}"))}");
        }
        output.WriteLine($"matched {ok}/{total}");
        Assert.Equal(total, ok);
    }

    [Fact]
    [Spec(SpecCategories.Catalog, "キーワード検索で候補を集める", "本文の各行から、商品名・別名（英語を含む）・品番の一致で候補を探す。引用行は使わない。似た別商品も候補に含まれ、見つからない行からは候補を出さない。")]
    public void Keyword_search_finds_products_by_name_alias_and_code()
    {
        var index = SampleIndex.Value;
        var english = index.Search(ProductIndex.QueryLines("Purchase Order", "- USB-C charging cable 1m x 40 pcs\n- Wireless mouse (black) x 25 pcs"), null, 8);
        var byCode = index.Search(ProductIndex.QueryLines("追加", "KT-200 を20本\n> GB-45 も前回お願いしました"), null, 8);

        Assert.Equal(["UC-1M", "WM-BK"], english.Take(2).Select(h => h.Product.Code).Order());
        Assert.Contains(english, h => h.Product.Code == "UC-2M");
        Assert.Equal("KT-200", byCode[0].Product.Code);
        Assert.DoesNotContain(byCode, h => h.Product.Code == "GB-45");
        Assert.Empty(index.Search(ProductIndex.QueryLines("お礼", "先日はありがとうございました。"), null, 8));
    }

    [Fact]
    [Spec(SpecCategories.Catalog, "ベクトル検索とキーワード検索を合わせる", "埋め込みがある商品は、言葉が一致しなくても意味の近さで候補に入る。両方で上位の商品が先に並ぶ（RRF）。別のモデルで作ったベクトルは使わない。")]
    public async Task Vector_search_adds_products_by_meaning()
    {
        var embedder = new FakeEmbedder();
        var products = new[]
        {
            new Product { Code = "WM-BK", Name = "ワイヤレスマウス 黒" },
            new Product { Code = "UC-1M", Name = "USB-C 充電ケーブル 1m" },
            new Product { Code = "SM-50", Name = "サージカルマスク 50枚入" },
        };
        var vectors = await embedder.EmbedAsync(products.Select(p => p.SearchText).ToList(), CancellationToken.None);
        for (var i = 0; i < products.Length; i++)
        {
            products[i].Embedding = ProductIndex.ToBytes(vectors[i]);
            products[i].EmbeddingModel = embedder.Model;
        }
        var lines = ProductIndex.QueryLines("", "ポインティングデバイスを10台\nサージカルマスクも5箱");
        var index = ProductIndex.Create(products, embedder.Model);

        var hits = index.Search(lines, await embedder.EmbedAsync(lines, CancellationToken.None), 2);

        Assert.True(index.HasVectors);
        Assert.Equal(["SM-50", "WM-BK"], hits.Select(h => h.Product.Code));
        Assert.Equal(0, hits[1].Keyword);
        Assert.True(hits[1].Vector > 0.99);
        Assert.False(ProductIndex.Create(products, "other-model").HasVectors);
    }

    [Fact]
    [Spec(SpecCategories.Catalog, "AI の答えを商品マスタで確かめる", "マスタにある商品コードは表記をそろえて残し、単位と単価がなければマスタの値で補う（メールの単価は優先）。マスタにないコードは消す。品名だけで 1 つに決まらないもの（「ボールペン」）にはコードを付けない。")]
    public void Resolve_keeps_known_codes_fills_prices_and_drops_invented_ones()
    {
        var extraction = new OrderExtraction
        {
            IsOrder = true,
            Items =
            [
                new ExtractedItem("充電ケーブル", 40, null, null, "uc-1m"),
                new ExtractedItem("ワイヤレスマウス", 25, "個", 1800, "WM-BK"),
                new ExtractedItem("謎の商品", 1, null, null, "XX-999"),
                new ExtractedItem("ボールペン", 10, "本", null),
            ],
        };

        var items = SampleIndex.Value.Resolve(extraction).Items;

        Assert.Equal(new ExtractedItem("充電ケーブル", 40, "本", 980, "UC-1M"), items[0]);
        Assert.Equal(1800, items[1].UnitPrice);
        Assert.Null(items[2].ProductCode);
        Assert.Null(items[2].UnitPrice);
        Assert.Null(items[3].ProductCode);
    }

    [Fact]
    [Spec(SpecCategories.Catalog, "商品マスタの CSV を読む", "見出し行の列名で読み、「\"」で囲んだ値（カンマ・改行を含む）を扱う。コードか名前が空の行・重複したコードは読み飛ばし、読めない単価は空にして、それぞれ行番号付きで知らせる。")]
    public void Csv_parser_reads_quotes_and_reports_bad_rows()
    {
        var csv = "\uFEFFname,code,unit_price,unit\r\n\"封筒, 長形3号\",EN-3,\"1,200\",箱\r\n\"説明に\n改行\",EN-4,abc,\r\n,EN-5,10,枚\r\n封筒,EN-3,5,枚\r\n";

        var result = ProductCsv.Parse(csv);

        Assert.Equal(2, result.Products.Count);
        Assert.Equal(("EN-3", "封筒, 長形3号", "箱", (decimal?)1200), (result.Products[0].Code, result.Products[0].Name, result.Products[0].Unit, result.Products[0].UnitPrice));
        Assert.Equal("説明に\n改行", result.Products[1].Name);
        Assert.Null(result.Products[1].UnitPrice);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("単価") && w.Contains("abc"));
        Assert.Contains(result.Warnings, w => w.Contains("重複"));
        Assert.Contains("code と name", Assert.Single(ProductCsv.Parse("品番,品名\nA,B").Warnings));
    }

    [Fact]
    [Spec(SpecCategories.Prompt, "商品マスタの候補をメールの後に付ける", "候補があるときだけ、メールの後に「商品マスタの候補」としてコード・名前・単位・標準単価を並べる。出力形式に product_code があり、候補にないコードを作らないよう指示する。")]
    public void User_prompt_lists_candidate_products_and_schema_has_product_code()
    {
        var mail = new MailInput("発注", "a@example.com", "A", "マウスを25個", new DateTime(2026, 10, 8, 0, 0, 0, DateTimeKind.Utc));

        var with = OrderPrompt.User(mail, products: [new CatalogProduct("WM-BK", "ワイヤレスマウス 黒", "個", 1980)]);
        var without = OrderPrompt.User(mail);
        var system = OrderPrompt.System(new AiSettings(), new DateTime(2026, 10, 8), hasExamples: false);

        Assert.Contains("商品マスタの候補", with);
        Assert.Contains("- WM-BK: ワイヤレスマウス 黒（単位: 個、標準単価: 1,980円）", with);
        Assert.DoesNotContain("商品マスタの候補", without);
        Assert.Contains("product_code", system);
        Assert.Contains("推測で作らない", system);
        Assert.Contains("\"product_code\"", ExtractionJson.SchemaText);
    }

    [Fact]
    [Spec(SpecCategories.Parsing, "商品コードを読み書きする", "AI の出力の product_code を読み取り、保存した結果に書き戻す。product_code のない古い結果も読める。")]
    public void Extraction_json_round_trips_product_codes()
    {
        var json = """{"is_order": true, "confidence": 0.9, "items": [{"product_name": "マウス", "quantity": 2, "unit": "個", "unit_price": null, "product_code": "WM-BK"}, {"product_name": "封筒", "quantity": 1}]}""";

        var parsed = ExtractionJson.Parse(json);
        var again = ExtractionJson.Parse(ExtractionJson.Serialize(parsed));

        Assert.Equal("WM-BK", parsed.Items[0].ProductCode);
        Assert.Null(parsed.Items[1].ProductCode);
        Assert.Equal(parsed.Items, again.Items);
    }

    [Fact]
    [Spec(SpecCategories.Ai, "AI に商品マスタの候補を送る", "解析するメールの後ろに候補が付き、手本のメールには付かない。")]
    public async Task Analyzer_sends_candidates_with_the_mail_only()
    {
        var handler = new FakeHandler(_ => Json("""{"choices":[{"message":{"role":"assistant","content":"{\"is_order\":false,\"confidence\":0.9,\"items\":[]}"}}]}"""));
        var analyzer = new OpenAiCompatibleOrderAnalyzer(new HttpClient(handler), new AiSettings { Model = "m" });
        var mail = new MailInput("発注", "a@example.com", "A", "マウスを25個", DateTime.UtcNow);
        var example = new AnalysisExample(mail with { Body = "封筒を100枚" }, new OrderExtraction { IsOrder = true, Items = [new ExtractedItem("封筒", 100, "枚", null)] });

        await analyzer.AnalyzeAsync(mail, new AnalysisContext([example], [new CatalogProduct("WM-BK", "ワイヤレスマウス 黒", "個", 1980)]), CancellationToken.None);

        var messages = Assert.Single(handler.Bodies)["messages"]!.AsArray();
        Assert.DoesNotContain("商品マスタの候補", messages[1]!["content"]!.GetValue<string>());
        Assert.Contains("WM-BK", messages[3]!["content"]!.GetValue<string>());
    }

    [Fact]
    [Spec(SpecCategories.Ai, "埋め込み API を呼ぶ", "チャットと同じ接続先の /embeddings に、モデル名と文の配列を送る（Azure OpenAI は api-key ヘッダー）。返ったベクトルは長さ 1 にそろえ、入力の順に並べる。")]
    public async Task Embedder_calls_embeddings_endpoint_and_normalizes_vectors()
    {
        var handler = new FakeHandler(_ => Json("""{"data":[{"index":1,"embedding":[0,2]},{"index":0,"embedding":[3,4]}]}"""));
        var embedder = new OpenAiCompatibleEmbedder(new HttpClient(handler), new AiSettings
        {
            Endpoint = "https://contoso.openai.azure.com/openai/v1",
            ApiKey = "azure-key",
            EmbeddingModel = " text-embedding-3-small ",
        });

        var vectors = await embedder.EmbedAsync(["マウス", "ケーブル"], CancellationToken.None);

        Assert.Equal("https://contoso.openai.azure.com/openai/v1/embeddings", handler.Urls.Single());
        Assert.Equal("azure-key", handler.ApiKeys.Single());
        var body = handler.Bodies.Single();
        Assert.Equal("text-embedding-3-small", body["model"]!.GetValue<string>());
        Assert.Equal(["マウス", "ケーブル"], body["input"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal([0.6f, 0.8f], vectors[0]);
        Assert.Equal([0f, 1f], vectors[1]);
    }

    [Fact]
    [Spec(SpecCategories.Examples, "商品コードも評価する", "確定した発注に商品コードがあるときだけ、コードの一致を評価に含める。コードが違えば全項目一致にならない。")]
    public void Evaluation_checks_product_codes_only_when_confirmed()
    {
        OrderExtraction Order(string? code) => new() { IsOrder = true, Items = [new ExtractedItem("マウス", 2, "個", null, code)] };

        var wrong = new EvaluationRow(1, "s", Order("WM-BK"), Order("WM-WH"), null, false, 0);
        var right = new EvaluationRow(2, "s", Order("WM-BK"), Order("wm-bk"), null, false, 0);
        var noCodes = new EvaluationRow(3, "s", Order(null), Order("WM-WH"), null, false, 0);
        var report = new EvaluationReport("r", 0, [wrong, right, noCodes]);

        Assert.False(wrong.AllOk);
        Assert.True(right.AllOk);
        Assert.True(noCodes.AllOk);
        Assert.Equal(2, report.WithCodes);
        Assert.Equal(1, report.CodesOk);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Two-dimensional "meaning": pointing devices and masks.</summary>
    private sealed class FakeEmbedder : IEmbedder
    {
        public string Model => "fake";

        public Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            Task.FromResult(inputs.Select(t => OpenAiCompatibleEmbedder.Normalize(
            [
                t.Contains("マウス") || t.Contains("ポインティング") ? 1f : 0.01f,
                t.Contains("マスク") ? 1f : 0.01f,
            ])).ToArray());
    }

    private sealed class FakeHandler(Func<JsonNode, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonNode> Bodies { get; } = [];
        public List<string> Urls { get; } = [];
        public List<string?> ApiKeys { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Bodies.Add(body);
            Urls.Add(request.RequestUri!.ToString());
            ApiKeys.Add(request.Headers.TryGetValues("api-key", out var values) ? values.Single() : null);
            return respond(body);
        }
    }
}
