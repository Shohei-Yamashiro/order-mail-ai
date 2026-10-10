using System.Text;
using OrderMail.Core.Analysis;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Processing;

/// <summary>
/// The same model as an Ollama tag, in trainable form on Hugging Face (Unsloth's copies),
/// with what the training script needs to train only on the answers.
/// Strings for the script are Python literals (\n stays escaped).
/// </summary>
public sealed record LocalBaseModel(
    string OllamaModel,
    string HuggingFaceModel,
    string ChatTemplate,
    string InstructionPart,
    string ResponsePart,
    string FallbackTemplate,
    IReadOnlyList<string> FallbackParameters)
{
    private const string ChatMl = """
        {{- if .System }}<|im_start|>system
        {{ .System }}<|im_end|>
        {{ end }}{{- range .Messages }}<|im_start|>{{ .Role }}
        {{ .Content }}<|im_end|>
        {{ end }}<|im_start|>assistant
        """;

    private const string Llama3 = """
        {{- if .System }}<|start_header_id|>system<|end_header_id|>

        {{ .System }}<|eot_id|>{{ end }}{{- range .Messages }}<|start_header_id|>{{ .Role }}<|end_header_id|>

        {{ .Content }}<|eot_id|>{{ end }}<|start_header_id|>assistant<|end_header_id|>

        """;

    private static LocalBaseModel Qwen(string size, string hf) =>
        new($"qwen2.5:{size}", hf, "qwen-2.5", @"<|im_start|>user\n", @"<|im_start|>assistant\n", ChatMl, ["stop \"<|im_start|>\"", "stop \"<|im_end|>\""]);

    private static LocalBaseModel Llama(string name, string hf) =>
        new(name, hf, "llama-3.1", @"<|start_header_id|>user<|end_header_id|>\n\n", @"<|start_header_id|>assistant<|end_header_id|>\n\n", Llama3,
            ["stop \"<|start_header_id|>\"", "stop \"<|end_header_id|>\"", "stop \"<|eot_id|>\""]);

    public static IReadOnlyList<LocalBaseModel> Known { get; } =
    [
        Qwen("0.5b", "unsloth/Qwen2.5-0.5B-Instruct"),
        Qwen("1.5b", "unsloth/Qwen2.5-1.5B-Instruct"),
        Qwen("3b", "unsloth/Qwen2.5-3B-Instruct"),
        Qwen("7b", "unsloth/Qwen2.5-7B-Instruct"),
        Qwen("14b", "unsloth/Qwen2.5-14B-Instruct"),
        Llama("llama3.1:8b", "unsloth/Meta-Llama-3.1-8B-Instruct"),
        Llama("llama3.2:3b", "unsloth/Llama-3.2-3B-Instruct"),
    ];

    private static readonly Dictionary<string, string> DefaultTags = new()
    {
        ["qwen2.5"] = "7b",
        ["llama3.1"] = "8b",
        ["llama3.2"] = "3b",
    };

    /// <summary>"qwen2.5:7b", "qwen2.5:7b-instruct-q4_K_M" and "qwen2.5" are all Qwen2.5 7B.</summary>
    public static LocalBaseModel? Find(string ollamaModel)
    {
        var name = ollamaModel.Trim().ToLowerInvariant();
        var colon = name.IndexOf(':');
        var family = colon < 0 ? name : name[..colon];
        var tag = colon < 0 ? DefaultTags.GetValueOrDefault(family, "") : name[(colon + 1)..].Split('-')[0];
        return Known.FirstOrDefault(m => m.OllamaModel == $"{family}:{tag}");
    }
}

public sealed record LocalKitExport(
    string Directory,
    int TrainCount,
    int ValidationCount,
    LocalBaseModel? BaseModel,
    bool TemplateFromOllama,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Writes a folder for fine-tuning the local model outside the app: the confirmed mails as JSONL,
/// a LoRA training script (Unsloth) that ends with a GGUF file, a Modelfile that registers it in Ollama
/// with the base model's prompt template, and the steps in Japanese.
/// </summary>
public sealed class LocalFineTuningKit(FineTuningExporter exporter, OllamaClient ollama)
{
    public const int MaxSeqLength = 4096;

    /// <summary>Below this the model mostly memorizes the few examples.</summary>
    public const int RecommendedTrainCount = 100;

    /// <summary>Ollama's default context is too short for the system prompt plus a long mail.</summary>
    public const int ContextLength = 8192;

    public async Task<LocalKitExport> ExportAsync(AiSettings settings, int validationEvery, string outputModel, int epochs, CancellationToken ct = default)
    {
        var data = await exporter.BuildAsync(settings, validationEvery, ct);
        var warnings = data.Warnings.ToList();
        if (data.Train.Count is > 0 and < RecommendedTrainCount)
            warnings.Insert(0, $"学習用が {data.Train.Count} 件です。効果を出すには {RecommendedTrainCount} 件以上（できれば数百件）が目安です。少ないと学習したメールを丸暗記し、新しいメールでかえって悪くなることがあります。");

        var baseModel = LocalBaseModel.Find(settings.Model);
        if (baseModel is null)
            warnings.Add($"モデル「{settings.Model}」に対応する学習用のモデルが分かりません。train.py の BASE_MODEL などを書き換えてください（対応表: {string.Join("、", LocalBaseModel.Known.Select(m => m.OllamaModel))}）。");
        var info = await ollama.ShowAsync(settings.Endpoint, settings.Model, ct);
        if (info is null)
            warnings.Add("Ollama から元のモデルのテンプレートを取得できなかったため、標準のテンプレートを使います（Ollama を起動してから書き出すと、元のモデルと同じものになります）。");

        var name = string.IsNullOrWhiteSpace(outputModel) ? "ordermail-ft" : outputModel.Trim();
        var directory = Path.Combine(exporter.ExportDirectory, $"local-finetune-{exporter.Stamp()}");
        Directory.CreateDirectory(directory);
        await FineTuningExporter.WriteAsync(Path.Combine(directory, "train.jsonl"), data.Train, ct);
        if (data.Validation.Count > 0) await FineTuningExporter.WriteAsync(Path.Combine(directory, "validation.jsonl"), data.Validation, ct);
        await WriteTextAsync(Path.Combine(directory, "train.py"), TrainScript(baseModel, name, Math.Clamp(epochs, 1, 10)), ct);
        await WriteTextAsync(Path.Combine(directory, "Modelfile.in"), ModelfileTemplate(baseModel, info), ct);
        await WriteTextAsync(Path.Combine(directory, "README.md"), Readme(settings.Model, baseModel, name, data.Train.Count, data.Validation.Count), ct);
        return new LocalKitExport(directory, data.Train.Count, data.Validation.Count, baseModel, info is not null, warnings);
    }

    public static string TrainScript(LocalBaseModel? model, string outputModel, int epochs) => $$"""
        # OrderMail が書き出したローカル LLM のファインチューニング（LoRA）スクリプトです。
        # 手順は同じフォルダの README.md を見てください。
        import glob
        import os
        import sys

        from unsloth import FastLanguageModel
        from unsloth.chat_templates import get_chat_template, train_on_responses_only
        from datasets import load_dataset
        from trl import SFTConfig, SFTTrainer

        BASE_MODEL = "{{model?.HuggingFaceModel ?? "（学習用のモデル名を入れてください）"}}"
        CHAT_TEMPLATE = "{{model?.ChatTemplate ?? "qwen-2.5"}}"
        INSTRUCTION_PART = "{{model?.InstructionPart ?? @"<|im_start|>user\n"}}"
        RESPONSE_PART = "{{model?.ResponsePart ?? @"<|im_start|>assistant\n"}}"
        MAX_SEQ_LENGTH = {{MaxSeqLength}}  # VRAM が足りないときは 2048 に下げる
        EPOCHS = {{epochs}}
        OUTPUT_MODEL = "{{outputModel}}"
        HERE = os.path.dirname(os.path.abspath(__file__))

        model, tokenizer = FastLanguageModel.from_pretrained(
            model_name=BASE_MODEL, max_seq_length=MAX_SEQ_LENGTH, load_in_4bit=True)
        model = FastLanguageModel.get_peft_model(
            model, r=16, lora_alpha=16, lora_dropout=0, bias="none",
            target_modules=["q_proj", "k_proj", "v_proj", "o_proj", "gate_proj", "up_proj", "down_proj"],
            use_gradient_checkpointing="unsloth", random_state=3407)
        tokenizer = get_chat_template(tokenizer, chat_template=CHAT_TEMPLATE)


        def to_text(batch):
            return {"text": [tokenizer.apply_chat_template(m, tokenize=False) for m in batch["messages"]]}


        files = {"train": os.path.join(HERE, "train.jsonl")}
        if os.path.exists(os.path.join(HERE, "validation.jsonl")):
            files["validation"] = os.path.join(HERE, "validation.jsonl")
        data = load_dataset("json", data_files=files).map(to_text, batched=True)

        trainer = SFTTrainer(
            model=model, tokenizer=tokenizer,
            train_dataset=data["train"], eval_dataset=data.get("validation"),
            args=SFTConfig(
                dataset_text_field="text", max_seq_length=MAX_SEQ_LENGTH,
                per_device_train_batch_size=1, gradient_accumulation_steps=8,
                num_train_epochs=EPOCHS, learning_rate=2e-4, warmup_steps=5,
                optim="adamw_8bit", weight_decay=0.01, lr_scheduler_type="linear",
                logging_steps=1, seed=3407, report_to="none",
                output_dir=os.path.join(HERE, "outputs")))
        # Learn only the answers, not the instructions and mails.
        trainer = train_on_responses_only(trainer, instruction_part=INSTRUCTION_PART, response_part=RESPONSE_PART)
        trainer.train()
        if "validation" in data:
            print(trainer.evaluate())

        gguf_dir = os.path.join(HERE, "gguf")
        model.save_pretrained_gguf(gguf_dir, tokenizer, quantization_method="q4_k_m")
        found = glob.glob(os.path.join(gguf_dir, "**", "*.gguf"), recursive=True)
        quantized = [f for f in found if "q4_k_m" in os.path.basename(f).lower()]
        if not (quantized or found):
            sys.exit("GGUF ファイルが見つかりません。")
        gguf = max(quantized or found, key=os.path.getsize)

        with open(os.path.join(HERE, "Modelfile.in"), encoding="utf-8") as f:
            modelfile = f.read().replace("__GGUF__", "./" + os.path.relpath(gguf, HERE).replace(os.sep, "/"))
        with open(os.path.join(HERE, "Modelfile"), "w", encoding="utf-8") as f:
            f.write(modelfile)
        print()
        print("学習が終わりました。次のコマンドで Ollama に登録してください:")
        print(f'  ollama create {OUTPUT_MODEL} -f "{os.path.join(HERE, "Modelfile")}"')

        """;

    public static string ModelfileTemplate(LocalBaseModel? model, OllamaModelInfo? info)
    {
        var template = info?.Template ?? model?.FallbackTemplate ?? LocalBaseModel.Known[0].FallbackTemplate;
        var parameters = (info?.Parameters is { Count: > 0 } p ? p : model?.FallbackParameters ?? LocalBaseModel.Known[0].FallbackParameters)
            .Where(x => !x.StartsWith("num_ctx ", StringComparison.Ordinal) && !x.StartsWith("temperature ", StringComparison.Ordinal));
        var sb = new StringBuilder();
        sb.AppendLine("# OrderMail が書き出した Modelfile のひな形です。train.py が __GGUF__ を学習済みモデルのファイルに置き換えて Modelfile を作ります。");
        sb.AppendLine("FROM __GGUF__");
        sb.AppendLine($"TEMPLATE \"\"\"{template}\"\"\"");
        foreach (var parameter in parameters) sb.AppendLine("PARAMETER " + parameter);
        sb.AppendLine($"PARAMETER num_ctx {ContextLength}");
        sb.AppendLine("PARAMETER temperature 0");
        return sb.ToString();
    }

    private static string Readme(string ollamaModel, LocalBaseModel? model, string outputModel, int trainCount, int validationCount) => $$"""
        # ローカル LLM のファインチューニング（{{outputModel}}）

        OrderMail が書き出した学習キットです。担当者が確定したメールで、いま使っているローカル LLM（{{ollamaModel}}）を追加学習（LoRA）し、Ollama に新しいモデル `{{outputModel}}` として登録します。

        - 学習用 {{trainCount}} 件（`train.jsonl`）・検証用 {{validationCount}} 件（`validation.jsonl`）
        - 学習に使う元のモデル: {{model?.HuggingFaceModel ?? "（対応表にないため train.py の BASE_MODEL を書き換えてください）"}}（Ollama の {{ollamaModel}} と同じモデル）
        - 1 行に「アプリが送る指示文・メール・担当者が確定した答え」が入っています。答えの部分だけを学習します。

        ## 必要なもの

        - NVIDIA の GPU（VRAM 8GB 以上。7B のモデルを 4bit で学習します。足りないときは train.py の MAX_SEQ_LENGTH を 2048 に下げるか、qwen2.5:3b を元にして書き出し直します）
        - Python（Unsloth が対応している版。3.13 では動かないことがあるので、3.11 か 3.12 を推奨）
        - インターネット接続（初回に元のモデルを Hugging Face から取得します。7B で約 5GB）
        - Ollama（学習したモデルを動かすため）

        学習はアプリの外で行います。アプリの動作には Python は不要です。

        ## 手順（PowerShell）

        ```powershell
        cd "このフォルダ"
        py -3.12 -m venv .venv
        .\.venv\Scripts\Activate.ps1
        # PyTorch（CUDA 版）を入れてから Unsloth を入れる。詳しくは Unsloth のインストール手順（Windows）を参照
        pip install torch --index-url https://download.pytorch.org/whl/cu124
        pip install unsloth
        python train.py
        ollama create {{outputModel}} -f Modelfile
        ```

        `python train.py` が終わると `gguf` フォルダと `Modelfile` ができます。WSL2（Ubuntu）でも同じ手順で学習できます。

        ## アプリで使う

        1. 「設定」の「AI の接続先」で、モデルを `{{outputModel}}` に書き換える（保存はまだしない）
        2. 「確認済みメールで評価」を押し、元のモデル（{{ollamaModel}}）の結果と並べて比べる
        3. 良くなっていれば保存する。悪くなったらモデル名を {{ollamaModel}} に戻す

        ## 注意

        - 学習に使ったメールで評価すると実力より良く見えます。検証用に分けたメールや、新しく届いたメールでも確かめてください。
        - 指示文（「AI への指示」や当社名）を変えたら、書き出し直して学習し直してください。学習時と同じ指示文で使うのが前提です。
        - 学習データにはメール本文が含まれます。社内の取り扱いルールに従って保管してください。

        """;

    private static Task WriteTextAsync(string path, string text, CancellationToken ct) =>
        File.WriteAllTextAsync(path, text.ReplaceLineEndings("\n"), new UTF8Encoding(false), ct);
}
