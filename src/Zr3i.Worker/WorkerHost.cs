using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Imagery;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Worker;

/// <summary>
/// Builds the background worker host. Settings: Mongo:ConnectionString, Mongo:Database, Worker:Concurrency (default 2),
/// Worker:PollSeconds (default 1), Worker:LeaseSeconds (default 120), Worker:MaxRunningPerTenant (default 2).
/// Kept out of Program.cs so tests can start the real host with their own settings.
/// </summary>
public static class WorkerHost
{
    public static IHost Build(string[] args, IDictionary<string, string?>? overrides = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
        var config = builder.Configuration;

        var connection = config["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString is not set.");
        var data = DataStore.Connect(connection, config["Mongo:Database"] ?? "zr3i");

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(data);
        builder.Services.AddSingleton(sp => new KernelStore(sp.GetRequiredService<DataStore>()));
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<CustomFieldService>();
        builder.Services.AddSingleton<FarmService>();
        builder.Services.AddSingleton<BoundaryService>();
        builder.Services.AddSingleton(data.CreateJobClaimer(new JobQueueOptions(
            TimeSpan.FromSeconds(config.GetValue("Worker:LeaseSeconds", 120)),
            config.GetValue("Worker:MaxRunningPerTenant", 2),
            TimeSpan.FromSeconds(30))));

        JobHandlers.Register(builder.Services);
        builder.Services.AddHostedService<JobWorker>();
        return builder.Build();
    }
}

/// <summary>The one place job handlers are listed. Each module adds its own here.</summary>
internal static class JobHandlers
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<IJobHandler, ImportValidateHandler>();
        services.AddSingleton<IJobHandler, ImportCommitHandler>();

        // Scene discovery: a shared HTTP client for the provider, the shared catalogue, and the handler that connects them.
        services.AddSingleton(new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(60) });
        services.AddSingleton<ISceneSource>(sp => new StacClient(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<SceneCatalog>();
        services.AddSingleton<IJobHandler, DiscoverHandler>();

        // Reading scenes for parcels: GDAL (bundled), the method registry for provenance, and the ingest handler.
        services.AddSingleton<IBandReader>(_ => new GdalBandReader());
        services.AddSingleton<Zr3i.Kernel.Methods.MethodRegistry>();
        services.AddSingleton<IJobHandler, IngestHandler>();

        // Notifications: handlers tell the person when their report or import is done.
        services.AddSingleton<Zr3i.Kernel.Identity.AuthorizationService>();
        services.AddSingleton<Zr3i.Kernel.Notifications.NotificationService>();

        // Reports: the same read services the screens use, a browser for PDFs (the only place one is used), and the freeze handler.
        services.AddSingleton<Zr3i.Kernel.Farms.ParcelListService>();
        services.AddSingleton<Zr3i.Kernel.Programmes.TrancheService>();
        services.AddSingleton<Zr3i.Kernel.Indicators.IndicatorRecorder>();
        services.AddSingleton<Zr3i.Modules.Risk.CriService>();
        services.AddSingleton<Zr3i.Modules.Risk.FriService>();
        services.AddSingleton<Zr3i.Modules.Risk.ReferenceService>();
        services.AddSingleton<Zr3i.Modules.Risk.RiskService>();
        services.AddSingleton<Zr3i.Reports.SnapshotBuilder>();
        services.AddSingleton<Zr3i.Reports.IPdfRenderer>(sp => new Zr3i.Reports.ChromiumPdfRenderer(sp.GetRequiredService<IConfiguration>()["Reports:BrowserPath"]));
        services.AddSingleton<IJobHandler, Zr3i.Reports.ReportFreezeHandler>();

        // Assessing a season: the per-crop settings and the findings handler. Size classes come from the same configuration as the web app.
        services.AddSingleton<Zr3i.Kernel.Crops.CropConfigs>();
        services.AddSingleton<IJobHandler>(sp =>
        {
            var edges = sp.GetRequiredService<IConfiguration>().GetSection("Verification:SizeClassEdgesFeddan").Get<double[]>() ?? [];
            return new Zr3i.Modules.Verification.AssessHandler(
                sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<Zr3i.Kernel.Methods.MethodRegistry>(), sp.GetRequiredService<Zr3i.Kernel.Crops.CropConfigs>(),
                edges.Length > 0 ? new Zr3i.Modules.Verification.SizeClassScheme(edges) : null, sp.GetRequiredService<TimeProvider>());
        });
    }
}

/// <summary>Runs N loops, each claiming and running one job at a time, and sleeps briefly when the queue is empty.</summary>
public sealed class JobWorker(JobClaimer claimer, IEnumerable<IJobHandler> handlers, TimeProvider clock, IConfiguration config, ILogger<JobWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var concurrency = Math.Max(1, config.GetValue("Worker:Concurrency", 2));
        var poll = TimeSpan.FromSeconds(Math.Max(0.1, config.GetValue("Worker:PollSeconds", 1.0)));
        var handlerList = handlers.ToList();
        logger.LogInformation("Worker started with {Concurrency} loops and {Handlers} handlers", concurrency, handlerList.Count);

        return Task.WhenAll(Enumerable.Range(0, concurrency).Select(i =>
        {
            var runner = new JobRunner(claimer, handlerList, clock, $"{Environment.MachineName}-{Environment.ProcessId}-{i}");
            return Task.Run(() => Loop(runner, poll, stoppingToken), CancellationToken.None);
        }));
    }

    private async Task Loop(JobRunner runner, TimeSpan poll, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                if (!await runner.RunOnceAsync(stop)) await Task.Delay(poll, stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Job loop error; backing off");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stop); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
