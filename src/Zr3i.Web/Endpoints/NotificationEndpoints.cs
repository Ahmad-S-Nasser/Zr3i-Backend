using Zr3i.Data;
using Zr3i.Kernel.Notifications;
using Zr3i.Kernel.Search;
using Zr3i.Web.Auth;

namespace Zr3i.Web.Endpoints;

public sealed record MarkReadBody(string[]? Ids, bool? All);

/// <summary>
/// A person's own notifications and the top-bar search. Both return only the signed-in person's own organisation's data, so any member
/// may call them; what a role may find is gated inside the search by the same permissions the screens use.
/// </summary>
public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organisations/{tenantId}/notifications", async (string tenantId, bool? unread, HttpContext http, Guard guard, NotificationService notifications, CancellationToken ct) =>
            await guard.RunAsMemberAsync(http, tenantId, ct, async _ =>
            {
                var scope = TenantScope.ForTenant(tenantId);
                var userId = http.User.UserId();
                var rows = await notifications.ListAsync(scope, userId, unread == true, ct);
                return Results.Ok(new
                {
                    unreadCount = await notifications.UnreadCountAsync(scope, userId, ct),
                    items = rows.Select(n => new { id = n.Id, code = n.Code, objectType = n.ObjectType, objectId = n.ObjectId, detail = n.Detail, createdAt = n.CreatedAt, read = n.ReadAt != null }),
                });
            })).RequireAuthorization();

        app.MapPost("/api/organisations/{tenantId}/notifications/read", async (string tenantId, MarkReadBody b, HttpContext http, Guard guard, NotificationService notifications, CancellationToken ct) =>
            await guard.RunAsMemberAsync(http, tenantId, ct, async _ =>
                Results.Ok(new { marked = await notifications.MarkReadAsync(TenantScope.ForTenant(tenantId), http.User.UserId(), b.Ids, b.All == true, ct) }))).RequireAuthorization();

        app.MapGet("/api/organisations/{tenantId}/search", async (string tenantId, string? q, HttpContext http, Guard guard, SearchService search, CancellationToken ct) =>
            await guard.RunAsMemberAsync(http, tenantId, ct, async access =>
                Results.Ok(new { groups = (await search.SearchAsync(TenantScope.ForTenant(tenantId), access, q, ct)).Select(g => new { kind = g.Kind, hits = g.Hits.Select(h => new { id = h.Id, label = h.Label, context = h.Context }) }) }))).RequireAuthorization();
    }
}
