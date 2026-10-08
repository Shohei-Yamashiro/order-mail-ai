using System.Text;
using System.Text.RegularExpressions;

namespace OrderMail.Core.Analysis;

/// <summary>
/// Resolves relative date expressions ("来週金曜", "明日") against the received date.
/// Small local models get these wrong even with a calendar, so the app computes them and hands them over.
/// </summary>
public static partial class DateHints
{
    private const string Weekdays = "月火水木金土日";

    public static IReadOnlyList<string> Find(string body, DateOnly received)
    {
        var text = string.Join('\n', body.Normalize(NormalizationForm.FormKC).Split('\n').Where(l => !l.TrimStart().StartsWith('>')));
        var monday = received.AddDays(-(((int)received.DayOfWeek + 6) % 7));
        var hints = new List<string>();

        foreach (Match m in WeekdayExpression().Matches(text))
        {
            var week = m.Groups["w"].Value switch { "今週" => 0, "来週" => 1, _ => 2 };
            var date = monday.AddDays(week * 7 + Weekdays.IndexOf(m.Groups["d"].Value[0]));
            hints.Add($"「{m.Value}」= {OrderPrompt.Day(date)}");
        }
        foreach (Match m in DayWord().Matches(text))
        {
            var offset = m.Value switch { "明後日" => 2, "明日" => 1, _ => 0 };
            hints.Add($"「{m.Value}」= {OrderPrompt.Day(received.AddDays(offset))}");
        }
        foreach (Match m in VagueExpression().Matches(text))
            hints.Add($"「{m.Value}」は日が決まらない表現");

        return hints.Distinct().ToList();
    }

    [GeneratedRegex(@"(?<w>今週|来週|再来週)(?:の)?\s*(?<d>[月火水木金土日])曜(?:日)?")]
    private static partial Regex WeekdayExpression();

    [GeneratedRegex(@"明後日|明日|本日")]
    private static partial Regex DayWord();

    [GeneratedRegex(@"今週中|来週中|今月中|来月中|なるべく早く|できるだけ早く|お早めに|至急")]
    private static partial Regex VagueExpression();
}
