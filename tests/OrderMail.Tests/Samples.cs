using System.Text.Json;
using OrderMail.Core.Analysis;
using OrderMail.Core.Mail;

namespace OrderMail.Tests;

/// <param name="Codes">Product master codes of the order lines, in mail order.</param>
public sealed record SampleLabel(string File, bool IsOrder, int Items, IReadOnlyList<string>? Codes = null);

internal static class Samples
{
    public static string MailFolder => Path.Combine(AppContext.BaseDirectory, "samples", "mails");

    public static IReadOnlyList<SampleLabel> Labels { get; } = JsonSerializer.Deserialize<List<SampleLabel>>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "labels.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public static string ProductCsv => Path.Combine(AppContext.BaseDirectory, "samples", "products.csv");

    public static FetchedMail Load(string file)
    {
        var message = MimeKit.MimeMessage.Load(Path.Combine(MailFolder, file));
        return MimeConversion.ToFetched(message, "eml", file);
    }

    public static MailInput Input(string file)
    {
        var m = Load(file);
        return new MailInput(m.Subject, m.FromAddress, m.FromName, m.Body, m.ReceivedAtUtc);
    }
}
