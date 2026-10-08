using OrderMail.Core.Processing;
using OrderMail.Core.Settings;

namespace OrderMail.Web.Services;

/// <summary>Runs import and analysis periodically when automatic polling is enabled in settings.</summary>
public sealed class MailPollingWorker(MailProcessor processor, SettingsStore settings, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), time);
        var lastRun = DateTimeOffset.MinValue;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var mail = settings.Current.Mail;
            if (!mail.AutoPoll || processor.IsRunning) continue;
            if (time.GetUtcNow() - lastRun < TimeSpan.FromMinutes(Math.Max(1, mail.PollIntervalMinutes))) continue;

            lastRun = time.GetUtcNow();
            await processor.RunAsync(stoppingToken);
        }
    }
}
