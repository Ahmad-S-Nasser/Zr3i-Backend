using System.Threading.RateLimiting;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imports;
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
        builder.Services.AddSingleton<OrganisationService>();
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
        app.MapMeEndpoints();

        return app;
    }
}
