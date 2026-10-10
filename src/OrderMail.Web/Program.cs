using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.FluentUI.AspNetCore.Components;
using OrderMail.Core;
using OrderMail.Core.Catalog;
using OrderMail.Core.Data;
using OrderMail.Core.Settings;
using OrderMail.Web.Components;
using OrderMail.Web.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

var dataDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, builder.Configuration["DataDirectory"] ?? "data"));

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddFluentUIComponents();
builder.Services.AddOrderMailCore(dataDirectory, AppContext.BaseDirectory);
builder.Services.AddHostedService<MailPollingWorker>();

var app = builder.Build();

using (var db = app.Services.GetRequiredService<IDbContextFactory<OrderMailDbContext>>().CreateDbContext())
{
    db.EnsureSchema();
}
await app.Services.GetRequiredService<ProductCatalogService>()
    .ImportIfEmptyAsync(app.Services.GetRequiredService<SettingsStore>().Current.Catalog);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

if (app.Configuration.GetValue("OpenBrowser", false))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var url = app.Urls.FirstOrDefault() ?? "http://localhost:5080";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    });
}

app.Logger.LogInformation("データフォルダ: {DataDirectory}", dataDirectory);
app.Logger.LogInformation("このウィンドウを閉じるとアプリが終了します。");

app.Run();
