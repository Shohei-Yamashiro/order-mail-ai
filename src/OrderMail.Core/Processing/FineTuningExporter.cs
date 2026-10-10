using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Microsoft.EntityFrameworkCore;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Domain;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Processing;

public sealed record FineTuningExport(string TrainPath, int TrainCount, string? ValidationPath, int ValidationCount, IReadOnlyList<string> Warnings);

/// <summary>
/// Writes confirmed mails as chat fine-tuning data (OpenAI / Azure OpenAI JSONL: one {"messages": [...]} per line).
/// Each line holds the same system prompt and mail text the app sends, and the confirmed answer,
/// so a model tuned on it can be used by setting its name in the AI settings.
/// </summary>
public sealed class FineTuningExporter(IDbContextFactory<OrderMailDbContext> dbFactory, TimeProvider time, string exportDirectory)
{
    /// <summary>OpenAI requires at least this many training examples.</summary>
    public const int MinimumTrainCount = 10;

    private static readonly JsonSerializerOptions LineOptions = new() { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) };

    public string ExportDirectory => exportDirectory;

    /// <param name="validationEvery">Every n-th mail (by id) goes to the validation file; 0 writes no validation file.</param>
    public async Task<FineTuningExport> ExportAsync(AiSettings settings, int validationEvery, CancellationToken ct = default)
    {
        var data = await BuildAsync(settings, validationEvery, ct);
        var warnings = data.Warnings.ToList();
        if (data.Train.Count is > 0 and < MinimumTrainCount)
            warnings.Insert(0, $"学習用が {data.Train.Count} 件です。OpenAI・Azure OpenAI のファインチューニングには {MinimumTrainCount} 件以上が必要で、効果を出すには数百件が目安です。");

        Directory.CreateDirectory(exportDirectory);
        var stamp = Stamp();
        var trainPath = Path.Combine(exportDirectory, $"finetune-{stamp}-train.jsonl");
        await WriteAsync(trainPath, data.Train, ct);
        string? validationPath = null;
        if (data.Validation.Count > 0)
        {
            validationPath = Path.Combine(exportDirectory, $"finetune-{stamp}-validation.jsonl");
            await WriteAsync(validationPath, data.Validation, ct);
        }
        return new FineTuningExport(trainPath, data.Train.Count, validationPath, data.Validation.Count, warnings);
    }

    public sealed record TrainingData(IReadOnlyList<string> Train, IReadOnlyList<string> Validation, IReadOnlyList<string> Warnings);

    /// <summary>Confirmed mails as JSONL lines, every n-th one held out for validation, with warnings about the data itself.</summary>
    public async Task<TrainingData> BuildAsync(AiSettings settings, int validationEvery, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var mails = await db.Mails
            .AsNoTracking()
            .Include(m => m.Order).ThenInclude(o => o!.Items)
            .Where(m => m.ReviewedAt != null && (m.Status == MailStatus.NotOrder || (m.Status == MailStatus.Registered && m.Order != null)))
            .OrderBy(m => m.Id)
            .ToListAsync(ct);

        var train = new List<string>();
        var validation = new List<string>();
        for (var i = 0; i < mails.Count; i++)
        {
            var line = Line(settings, mails[i]);
            if (validationEvery > 1 && (i + 1) % validationEvery == 0) validation.Add(line);
            else train.Add(line);
        }

        var warnings = new List<string>();
        if (mails.Count == 0) warnings.Add("確認済みのメールがありません。");
        var orders = mails.Count(m => m.Status == MailStatus.Registered);
        if (mails.Count > 0 && (orders == 0 || orders == mails.Count)) warnings.Add("発注と発注以外の片方しかありません。両方を含めると判定が偏りにくくなります。");
        return new TrainingData(train, validation, warnings);
    }

    public string Stamp() => time.GetLocalNow().ToString("yyyyMMdd-HHmmss");

    public static string Line(AiSettings settings, ReceivedMail mail)
    {
        var input = MailInput.From(mail);
        object[] messages =
        [
            new { role = "system", content = OrderPrompt.System(settings, input.ReceivedAtUtc.ToLocalTime(), hasExamples: false) },
            new { role = "user", content = OrderPrompt.User(input) },
            new { role = "assistant", content = OrderPrompt.Answer(ExampleSelector.ConfirmedResult(mail)) },
        ];
        return JsonSerializer.Serialize(new { messages }, LineOptions);
    }

    public static Task WriteAsync(string path, IReadOnlyList<string> lines, CancellationToken ct) =>
        File.WriteAllTextAsync(path, string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""), new UTF8Encoding(false), ct);
}
