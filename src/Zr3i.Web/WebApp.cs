using System.Threading.RateLimiting;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Accounts;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Consent;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Methods;
using Zr3i.Modules.Verification;
using Zr3i.Kernel.Identity;
using Zr3i.Web.Auth;
using Zr3i.Web.Endpoints;

namespace Zr3i.Web;

/// <summary>
/// Builds the API host. Kept out of Program.cs so tests can start the real app with their own settings.
/// Settings: Mongo:ConnectionString, Mongo:Database, Mongo:MigrateOnStartup, Auth:PasswordIterations,
/// RateLimit:AuthPerMinute, Cors:Origins. The connection string comes from user-secrets or the environment, never the repo.
/// </summary>
public static class WebApp
{
    public static WebApplication Build(string[] args, IDictionary<string, string?>? overrides = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        if (overrides is not null) builder.Configuration.AddInMemoryCollection(overrides);
        var config = builder.Configuration;

        var connection = config["Mongo:ConnectionString"] ?? throw new InvalidOperationException("Mongo:ConnectionString is not set.");
        var database = config["Mongo:Database"] ?? "zr3i";

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(DataStore.Connect(connection, database));
        builder.Services.AddSingleton(sp => new KernelStore(sp.GetRequiredService<DataStore>()));
        builder.Services.AddSingleton(new PasswordHasher(config.GetValue("Auth:PasswordIterations", PasswordHasher.DefaultIterations)));
        builder.Services.AddSingleton<AuditService>();
        builder.Services.AddSingleton<AccountTypeService>();
        builder.Services.AddSingleton<OrganisationService>();
        builder.Services.AddSingleton<PlatformService>();
        builder.Services.AddSingleton<AccessRequestService>();
        builder.Services.AddSingleton<MethodRegistry>();
        builder.Services.AddSingleton<Zr3i.Kernel.Crops.CropConfigs>();
        builder.Services.AddSingleton<SceneCatalog>();
        builder.Services.AddSingleton<Zr3i.Kernel.Findings.AccuracyRegister>();
        builder.Services.AddSingleton<FarmListService>();
        builder.Services.AddSingleton<ParcelListService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Programmes.TrancheService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Programmes.EventWindowService>();
        builder.Services.AddSingleton(sp => new Zr3i.Kernel.Reports.ReportService(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<Zr3i.Kernel.Findings.VerificationAnalysisService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Chips.IChipStore>(sp => new Zr3i.Kernel.Chips.FileChipStore(Zr3i.Kernel.Chips.ChipRoot.From(sp.GetRequiredService<IConfiguration>()["Imagery:ChipRoot"])));
        builder.Services.AddSingleton(sp => new Zr3i.Kernel.Chips.ChipPlanner(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<Zr3i.Kernel.Chips.ChipService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Notifications.NotificationService>();
        builder.Services.AddSingleton(sp => new Zr3i.Kernel.Imagery.ImagerySummaryService(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<SceneCatalog>()));
        builder.Services.AddSingleton<Zr3i.Kernel.Search.SearchService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Identity.AuthenticatorResetService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Admin.MemberDirectoryService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Admin.AuditReader>();
        builder.Services.AddSingleton<Zr3i.Kernel.Admin.TenantDirectoryService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Admin.PortfolioService>();
        builder.Services.AddSingleton<Zr3i.Kernel.Indicators.IndicatorRecorder>();
        builder.Services.AddSingleton<Zr3i.Modules.Risk.CriService>();
        builder.Services.AddSingleton<Zr3i.Modules.Risk.FriService>();
        builder.Services.AddSingleton<Zr3i.Modules.Risk.ReferenceService>();
        builder.Services.AddSingleton<Zr3i.Modules.Risk.RiskService>();
        builder.Services.AddSingleton(sp => new Zr3i.Kernel.Findings.AssessmentPlanner(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new IngestPlanner(sp.GetRequiredService<SceneCatalog>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton(sp => new DiscoveryPlanner(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<FarmerService>();
        builder.Services.AddSingleton<ReadabilityService>();
        var sizeEdges = config.GetSection("Verification:SizeClassEdgesFeddan").Get<double[]>() ?? [];
        builder.Services.AddSingleton(new SizeClassConfig(sizeEdges.Length > 0 ? new SizeClassScheme(sizeEdges) : null));   // bad edges stop the start-up
        builder.Services.AddSingleton<ConsentService>();
        builder.Services.AddSingleton<ConsentGate>();
        builder.Services.AddSingleton<SharedDataService>();
        builder.Services.AddSingleton<InvitationService>();
        builder.Services.AddSingleton<SignInService>();
        builder.Services.AddSingleton<SessionService>();
        builder.Services.AddSingleton<AuthorizationService>();
        builder.Services.AddSingleton<Guard>();
        builder.Services.AddSingleton<CustomFieldService>();
        builder.Services.AddSingleton<FarmService>();
        builder.Services.AddSingleton<BoundaryService>();
        builder.Services.AddSingleton(sp => new ImportService(sp.GetRequiredService<KernelStore>(), sp.GetRequiredService<DataStore>().Jobs, sp.GetRequiredService<AuditService>(), sp.GetRequiredService<TimeProvider>()));

        builder.Services.AddAuthentication(SessionAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SessionAuthenticationHandler>(
                SessionAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();

        var perMinute = config.GetValue("RateLimit:AuthPerMinute", 20);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(AuthEndpoints.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        });

        var origins = config.GetSection("Cors:Origins").Get<string[]>() ?? [];
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        {
            if (origins.Length > 0) p.WithOrigins(origins).WithHeaders("Authorization", "Content-Type").WithMethods("GET", "POST");
        }));

        var app = builder.Build();

        if (config.GetValue("Mongo:MigrateOnStartup", false))
            app.Services.GetRequiredService<DataStore>().MigrateAsync().GetAwaiter().GetResult();

        // The built-in method drafts (ZR3I-R0-2026-01) exist from the first start.
        app.Services.GetRequiredService<MethodRegistry>().EnsureBuiltInsAsync().GetAwaiter().GetResult();

        // First start of a new installation: invite the first Zr3i administrator. The token is shown once, here, and not stored in clear.
        if (config["Platform:BootstrapAdminEmail"] is { Length: > 0 } adminEmail)
        {
            var token = app.Services.GetRequiredService<PlatformService>().BootstrapAsync(adminEmail).GetAwaiter().GetResult();
            if (token is not null)
                app.Logger.LogWarning("Zr3i administrator invitation for {Email} (shown once): {Token}", adminEmail, token);
        }

        // TLS is terminated by the host; HTTPS redirection and HSTS are decided with the hosting choice (W6-4).
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/ready", async (DataStore data, CancellationToken ct) =>
            await data.PingAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
        app.MapAuthEndpoints();
        app.MapInvitationEndpoints();
        app.MapImportEndpoints();
        app.MapAccessRequestEndpoints();
        app.MapConsentEndpoints();
        app.MapReadabilityEndpoints();
        app.MapMethodEndpoints();
        app.MapCropEndpoints();
        app.MapFindingEndpoints();
        app.MapAccuracyEndpoints();
        app.MapRiskEndpoints();
        app.MapFarmEndpoints();
        app.MapParcelEndpoints();
        app.MapTrancheEventEndpoints();
        app.MapReportEndpoints();
        app.MapNotificationEndpoints();
        app.MapAuthenticatorResetEndpoints();
        app.MapAdminReadEndpoints();
        app.MapImageryEndpoints();
        app.MapMeEndpoints();

        return app;
    }
}
