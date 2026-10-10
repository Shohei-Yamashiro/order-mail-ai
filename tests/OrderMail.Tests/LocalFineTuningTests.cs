using System.Net;
using System.Text;
using OrderMail.Core.Analysis;
using OrderMail.Core.Processing;

namespace OrderMail.Tests;

public class LocalFineTuningTests
{
    [Fact]
    [Spec(SpecCategories.Feedback, "Ollama のモデル名から学習用のモデルを決める", "「qwen2.5:7b」「qwen2.5:7b-instruct-q4_K_M」「qwen2.5」は同じ Qwen2.5 7B（Hugging Face の学習用モデル）になり、サイズ違いは別のモデルになる。対応表にないモデルは決めない。")]
    public void Base_model_is_found_from_the_ollama_name()
    {
        Assert.Equal("unsloth/Qwen2.5-7B-Instruct", LocalBaseModel.Find("qwen2.5:7b")!.HuggingFaceModel);
        Assert.Equal("unsloth/Qwen2.5-7B-Instruct", LocalBaseModel.Find(" Qwen2.5:7b-instruct-q4_K_M ")!.HuggingFaceModel);
        Assert.Equal("unsloth/Qwen2.5-7B-Instruct", LocalBaseModel.Find("qwen2.5")!.HuggingFaceModel);
        Assert.Equal("unsloth/Qwen2.5-3B-Instruct", LocalBaseModel.Find("qwen2.5:3b")!.HuggingFaceModel);
        Assert.Equal("llama-3.1", LocalBaseModel.Find("llama3.1:8b")!.ChatTemplate);
        Assert.Null(LocalBaseModel.Find("gemma3:4b"));
        Assert.Null(LocalBaseModel.Find("gpt-4.1-mini"));
    }

    [Fact]
    [Spec(SpecCategories.Feedback, "Ollama から元のモデルのテンプレートを取得する", "OpenAI 互換のエンドポイント（…/v1）から Ollama の /api/show を呼び、テンプレートと停止語などのパラメーターを読み取る。Ollama に接続できないときは null を返す。")]
    public async Task Ollama_show_reads_template_and_parameters()
    {
        string? url = null;
        var client = new OllamaClient(new HttpClient(new FakeHandler(request =>
        {
            url = request.RequestUri!.ToString();
            return Json("""{"template": "{{ .Prompt }}", "parameters": "stop                           \"<|im_start|>\"\nstop                           \"<|im_end|>\"\nnum_ctx                        4096"}""");
        })));

        var info = await client.ShowAsync("http://localhost:11434/v1/", "qwen2.5:7b");
        var unreachable = await new OllamaClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("refused")))).ShowAsync("http://localhost:11434/v1", "m");

        Assert.Equal("http://localhost:11434/api/show", url);
        Assert.Equal("{{ .Prompt }}", info!.Template);
        Assert.Equal(["stop \"<|im_start|>\"", "stop \"<|im_end|>\"", "num_ctx 4096"], info.Parameters);
        Assert.Null(unreachable);
    }

    [Fact]
    [Spec(SpecCategories.Feedback, "学習済みモデルを Ollama に登録する Modelfile を作る", "元のモデルのテンプレートと停止語を引き継ぎ、文脈長を広げて temperature を 0 にする（元の num_ctx は使わない）。学習済みファイルの場所は学習スクリプトが埋める。")]
    public void Modelfile_keeps_the_base_template_and_widens_the_context()
    {
        var modelfile = LocalFineTuningKit.ModelfileTemplate(LocalBaseModel.Find("qwen2.5:7b"), new OllamaModelInfo("{{ .Prompt }}", ["stop \"<|im_end|>\"", "num_ctx 4096"]));
        var fallback = LocalFineTuningKit.ModelfileTemplate(LocalBaseModel.Find("qwen2.5:7b"), null);

        Assert.Contains("FROM __GGUF__", modelfile);
        Assert.Contains("TEMPLATE \"\"\"{{ .Prompt }}\"\"\"", modelfile);
        Assert.Contains("PARAMETER stop \"<|im_end|>\"", modelfile);
        Assert.Contains($"PARAMETER num_ctx {LocalFineTuningKit.ContextLength}", modelfile);
        Assert.DoesNotContain("num_ctx 4096", modelfile);
        Assert.Contains("PARAMETER temperature 0", modelfile);
        Assert.Contains("<|im_start|>assistant", fallback);
    }

    [Fact]
    [Spec(SpecCategories.Feedback, "学習スクリプトに元のモデルと答えだけを学習する設定を入れる", "学習スクリプトに Hugging Face の元のモデル名・会話テンプレート・答えの区切り・登録するモデル名・エポック数が入り、最後に GGUF を作って Modelfile を書く。")]
    public void Train_script_uses_the_base_model_and_trains_on_answers_only()
    {
        var script = LocalFineTuningKit.TrainScript(LocalBaseModel.Find("qwen2.5:7b"), "ordermail-ft", 3);

        Assert.Contains("BASE_MODEL = \"unsloth/Qwen2.5-7B-Instruct\"", script);
        Assert.Contains("CHAT_TEMPLATE = \"qwen-2.5\"", script);
        Assert.Contains("RESPONSE_PART = \"<|im_start|>assistant\\n\"", script);
        Assert.Contains("EPOCHS = 3", script);
        Assert.Contains("OUTPUT_MODEL = \"ordermail-ft\"", script);
        Assert.Contains("train_on_responses_only", script);
        Assert.Contains("save_pretrained_gguf", script);
        Assert.Contains("\"Modelfile\"", script);
    }

    internal static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
