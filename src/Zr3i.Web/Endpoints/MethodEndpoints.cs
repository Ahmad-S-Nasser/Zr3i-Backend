using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Methods;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record ParameterBody(string? Value, string? Status, string? Source, string? Note);

public sealed record FreezeBody(string? ApprovedBy, string[]? ApprovedKeys);

/// <summary>
/// Methods and their parameters. Everyone signed in can read how a result was produced and how settled each rule is; only Zr3i
/// administrators can edit a draft, freeze it, or open a new version.
/// </summary>
public static class MethodEndpoints
{
    public const string Manage = "platform:methods:manage";

    public static void MapMethodEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/methods", async (MethodRegistry registry, CancellationToken ct) =>
            Results.Ok((await registry.ListAsync(ct)).Select(Summary))).RequireAuthorization();

        app.MapGet("/api/methods/{methodId}", async (string methodId, int? version, MethodRegistry registry, CancellationToken ct) =>
        {
            var m = await registry.GetAsync(methodId, version, ct);
            return m is null ? Results.Json(new { error = "not_found" }, statusCode: StatusCodes.Status404NotFound) : Results.Ok(await Detail(registry, m, ct));
        }).RequireAuthorization();

        var admin = app.MapGroup("/api/platform/methods/{methodId}/{version:int}").RequireAuthorization();

        admin.MapPut("parameters/{key}", async (string methodId, int version, string key, ParameterBody b, HttpContext http, Guard guard, MethodRegistry registry, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                var m = await registry.SetParameterAsync(http.User.UserId(), methodId, version, key, b.Value, b.Status ?? string.Empty, b.Source ?? string.Empty, b.Note, ct);
                return Results.Ok(await Detail(registry, m, ct));
            }));

        admin.MapPost("freeze", async (string methodId, int version, FreezeBody b, HttpContext http, Guard guard, MethodRegistry registry, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                var m = await registry.FreezeAsync(http.User.UserId(), methodId, version, b.ApprovedBy ?? string.Empty, b.ApprovedKeys ?? [], ct);
                return Results.Ok(await Detail(registry, m, ct));
            }));

        admin.MapPost("new-draft", async (string methodId, int version, HttpContext http, Guard guard, MethodRegistry registry, CancellationToken ct) =>
            await guard.RunAsync(http, PlatformTenant.Id, Manage, ct, async () =>
            {
                var m = await registry.NewDraftFromAsync(http.User.UserId(), methodId, version, ct);
                return Results.Created((string?)null, await Detail(registry, m, ct));
            }));
    }

    private static object Summary(MethodVersion m) => new
    {
        methodId = m.MethodId,
        name = m.Name,
        version = m.Version,
        state = m.State,
        frozenAt = m.FrozenAt,
        sealSha256 = m.ParametersSha256,
        parameters = m.Parameters.GroupBy(p => p.Status).ToDictionary(g => g.Key, g => g.Count()),
    };

    private static async Task<object> Detail(MethodRegistry registry, MethodVersion m, CancellationToken ct) => new
    {
        methodId = m.MethodId,
        name = m.Name,
        version = m.Version,
        state = m.State,
        frozenAt = m.FrozenAt,
        approvedBy = m.ApprovedBy,
        sealSha256 = m.ParametersSha256,
        sealVerified = m.IsFrozen ? await registry.VerifyAsync(m.MethodId, m.Version, ct) : (bool?)null,
        parameters = m.Parameters.Select(p => new { key = p.Key, value = p.Value, unit = p.Unit, status = p.Status, source = p.Source, note = p.Note }),
    };
}
