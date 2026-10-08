using System.Net;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using OrderMail.Core.Settings;

namespace OrderMail.Core.Mail;

public sealed record FetchedMail(
    string MessageId,
    string Source,
    string FromAddress,
    string FromName,
    string To,
    string Subject,
    string Body,
    DateTime ReceivedAtUtc);

public interface IMailSource
{
    string Name { get; }

    /// <param name="isKnown">Returns true for message IDs already stored, so they can be skipped before download.</param>
    Task<IReadOnlyList<FetchedMail>> FetchAsync(Func<string, bool> isKnown, CancellationToken ct);
}

public static class MimeConversion
{
    public static FetchedMail ToFetched(MimeMessage message, string source, string fallbackId)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        var body = message.TextBody ?? HtmlText.ToPlainText(message.HtmlBody ?? "");
        var id = string.IsNullOrWhiteSpace(message.MessageId) ? fallbackId : message.MessageId;
        var date = message.Date == DateTimeOffset.MinValue ? DateTime.UtcNow : message.Date.UtcDateTime;
        return new FetchedMail(
            id,
            source,
            from?.Address ?? "",
            from?.Name ?? "",
            string.Join(", ", message.To.Mailboxes.Select(m => m.Address)),
            message.Subject ?? "",
            body.Replace("\r\n", "\n").Trim(),
            date);
    }
}

public static partial class HtmlText
{
    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = DropBlocks().Replace(html, "");
        text = LineBreaks().Replace(text, "\n");
        text = CellBreaks().Replace(text, " | ");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = SpaceRuns().Replace(text, " ");
        text = BlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim(' ', '|').Trim())).Trim();
    }

    [GeneratedRegex(@"<(script|style|head)[\s\S]*?</\1>", RegexOptions.IgnoreCase)]
    private static partial Regex DropBlocks();

    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h\d|table)>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreaks();

    [GeneratedRegex(@"</t[dh]>", RegexOptions.IgnoreCase)]
    private static partial Regex CellBreaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\u00A0]+")]
    private static partial Regex SpaceRuns();

    [GeneratedRegex(@"\n\s*\n(\s*\n)+")]
    private static partial Regex BlankLines();
}

public sealed class EmlFolderMailSource(string folder) : IMailSource
{
    public string Name => "eml";

    public Task<IReadOnlyList<FetchedMail>> FetchAsync(Func<string, bool> isKnown, CancellationToken ct)
    {
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException($"メールフォルダが見つかりません: {folder}");

        var result = new List<FetchedMail>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.eml").Order(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var message = MimeMessage.Load(path, ct);
            var fetched = MimeConversion.ToFetched(message, Name, "eml:" + Path.GetFileName(path));
            if (!isKnown(fetched.MessageId)) result.Add(fetched);
        }
        return Task.FromResult<IReadOnlyList<FetchedMail>>(result);
    }
}

public sealed class ImapMailSource(MailSourceSettings settings) : IMailSource
{
    public string Name => "imap";

    public async Task<IReadOnlyList<FetchedMail>> FetchAsync(Func<string, bool> isKnown, CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        var folder = await client.GetFolderAsync(settings.ImapFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);

        var since = DateTime.Today.AddDays(-Math.Max(1, settings.ImapSinceDays));
        var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(since), ct);
        var summaries = await folder.FetchAsync(uids, MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId, ct);

        var result = new List<FetchedMail>();
        foreach (var summary in summaries)
        {
            var fallbackId = $"imap:{settings.ImapFolder}:{folder.UidValidity}:{summary.UniqueId.Id}";
            var id = summary.Envelope?.MessageId is { Length: > 0 } mid ? mid : fallbackId;
            if (isKnown(id)) continue;
            var message = await folder.GetMessageAsync(summary.UniqueId, ct);
            result.Add(MimeConversion.ToFetched(message, Name, fallbackId));
        }

        await client.DisconnectAsync(true, ct);
        return result;
    }

    /// <summary>Returns the number of messages in the configured folder.</summary>
    public async Task<int> TestAsync(CancellationToken ct)
    {
        using var client = await ConnectAsync(ct);
        var folder = await client.GetFolderAsync(settings.ImapFolder, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        var count = folder.Count;
        await client.DisconnectAsync(true, ct);
        return count;
    }

    private async Task<ImapClient> ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.ImapHost))
            throw new InvalidOperationException("IMAP サーバーが設定されていません。");

        var client = new ImapClient { Timeout = 30_000 };
        try
        {
            var security = settings.ImapUseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable;
            await client.ConnectAsync(settings.ImapHost, settings.ImapPort, security, ct);
            await client.AuthenticateAsync(settings.ImapUser, settings.ImapPassword, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}

public sealed class MailSourceFactory(string baseDirectory)
{
    public IMailSource Create(MailSourceSettings settings) => settings.Kind switch
    {
        MailSourceKind.Imap => new ImapMailSource(settings),
        _ => new EmlFolderMailSource(ResolveFolder(settings.EmlFolder)),
    };

    public string ResolveFolder(string folder) =>
        Path.IsPathRooted(folder) ? folder : Path.GetFullPath(Path.Combine(baseDirectory, folder));
}
