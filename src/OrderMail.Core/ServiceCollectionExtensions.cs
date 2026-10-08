using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderMail.Core.Analysis;
using OrderMail.Core.Data;
using OrderMail.Core.Mail;
using OrderMail.Core.Processing;
using OrderMail.Core.Settings;

namespace OrderMail.Core;

public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Holds the SQLite database and settings.json.</param>
    /// <param name="baseDirectory">Relative mail folder paths are resolved against this.</param>
    public static IServiceCollection AddOrderMailCore(this IServiceCollection services, string dataDirectory, string baseDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<OrderMailDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "ordermail.db")}"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SettingsStore(Path.Combine(dataDirectory, "settings.json")));
        services.AddSingleton(new MailSourceFactory(baseDirectory));
        services.AddSingleton<AnalyzerFactory>();
        services.AddSingleton<MailProcessor>();
        services.AddSingleton<ReviewService>();
        return services;
    }
}
