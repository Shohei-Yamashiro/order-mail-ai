using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderMail.Core.Analysis;
using OrderMail.Core.Catalog;
using OrderMail.Core.Data;
using OrderMail.Core.Mail;
using OrderMail.Core.Processing;
using OrderMail.Core.Settings;

namespace OrderMail.Core;

public static class ServiceCollectionExtensions
{
    /// <param name="dataDirectory">Holds the SQLite database and settings.json.</param>
    /// <param name="baseDirectory">Relative mail folder and product CSV paths are resolved against this.</param>
    public static IServiceCollection AddOrderMailCore(this IServiceCollection services, string dataDirectory, string baseDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        services.AddDbContextFactory<OrderMailDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "ordermail.db")}"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new SettingsStore(Path.Combine(dataDirectory, "settings.json")));
        services.AddSingleton(new MailSourceFactory(baseDirectory));
        services.AddSingleton<AnalyzerFactory>();
        services.AddSingleton(sp => new ProductCatalogService(
            sp.GetRequiredService<IDbContextFactory<OrderMailDbContext>>(),
            sp.GetRequiredService<AnalyzerFactory>(),
            baseDirectory,
            sp.GetRequiredService<ILogger<ProductCatalogService>>()));
        services.AddSingleton<MailProcessor>();
        services.AddSingleton<ReviewService>();
        services.AddSingleton<DataResetService>();
        services.AddSingleton<PromptEvaluator>();
        services.AddSingleton<FeedbackService>();
        services.AddSingleton(sp => new FineTuningExporter(
            sp.GetRequiredService<IDbContextFactory<OrderMailDbContext>>(),
            sp.GetRequiredService<TimeProvider>(),
            Path.Combine(dataDirectory, "exports")));
        services.AddSingleton(new OllamaClient(new HttpClient { Timeout = TimeSpan.FromSeconds(10) }));
        services.AddSingleton<LocalFineTuningKit>();
        return services;
    }
}
