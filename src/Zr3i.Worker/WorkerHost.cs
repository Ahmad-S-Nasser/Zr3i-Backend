using Zr3i.Data;
using Zr3i.Data.Jobs;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
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
