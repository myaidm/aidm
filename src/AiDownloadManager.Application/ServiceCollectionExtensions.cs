using AiDownloadManager.Core;
using AiDownloadManager.Engine;
using AiDownloadManager.Infrastructure;
using AiDownloadManager.Protocols;
using Microsoft.Extensions.DependencyInjection;

namespace AiDownloadManager.Application;

/// <summary>
/// Composition root helpers. The entry point (WPF app, prototype)
/// calls this and then adds its own logging providers. Logging is
/// optional: the engine falls back to a null logger when none is
/// configured.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAiDownloadManager(
        this IServiceCollection services, string databasePath)
    {
        services.AddSingleton<IDownloadJournal>(sp =>
        {
            var journal = new SqliteDownloadJournal(databasePath);
            journal.InitializeAsync().GetAwaiter().GetResult();
            return journal;
        });
        services.AddSingleton<ITransport, HttpTransport>();
        services.AddSingleton(sp => new SegmentedDownloaderOptions());
        services.AddTransient<SegmentedDownloader>();
        // Antivirus hook: registered but DISABLED by default. The UI
        // enables it only after the user configures a scanner path
        // and opts in; until then no scan ever runs.
        services.AddSingleton(new AntivirusScannerOptions());
        services.AddSingleton<AntivirusScanner>();
        services.AddTransient<DownloadService>();
        return services;
    }
}
