using OrderMail.Core.Domain;

namespace OrderMail.Web.Services;

public static class StatusDisplay
{
    public static string Label(MailStatus status) => status switch
    {
        MailStatus.Unanalyzed => "未解析",
        MailStatus.NeedsReview => "要確認",
        MailStatus.Registered => "発注",
        MailStatus.NotOrder => "発注以外",
        MailStatus.Failed => "解析失敗",
        _ => status.ToString(),
    };

    public static string CssClass(MailStatus status) => status switch
    {
        MailStatus.NeedsReview => "badge badge-review",
        MailStatus.Registered => "badge badge-order",
        MailStatus.NotOrder => "badge badge-other",
        MailStatus.Failed => "badge badge-failed",
        _ => "badge",
    };

    public static string Confidence(double? value) => value is { } v ? $"{v:P0}" : "-";

    public static string Local(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("yyyy/MM/dd HH:mm");
}
