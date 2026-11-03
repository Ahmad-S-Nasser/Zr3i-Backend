using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Imagery;

/// <summary>The shared catalogue of scenes (platform tenant). Adding the same scene again is harmless; a changed item is noticed.</summary>
public sealed class SceneCatalog(KernelStore store, AuditService audit, TimeProvider clock)
{
    private const int Page = 500;

    /// <summary>
    /// Adds new scenes, updates the ones whose item hash changed, and leaves the rest alone. A changed hash is audited, because a result
    /// that cites the old item can then no longer be reproduced from the catalogue alone.
    /// </summary>
    public async Task<CatalogueChange> UpsertAsync(IReadOnlyList<Scene> scenes, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        int inserted = 0, updated = 0, unchanged = 0;

        foreach (var chunk in scenes.Chunk(Page))
        {
            var ids = chunk.Select(s => s.SceneId).ToList();
            var existing = (await store.Scenes.FindAsync(PlatformTenant.Scope, s => ids.Contains(s.SceneId), Page, ct)).ToDictionary(s => s.SceneId);

            var toInsert = new List<Scene>();
            var toReplace = new List<Scene>();
            foreach (var scene in chunk)
            {
                if (!existing.TryGetValue(scene.SceneId, out var current))
                {
                    scene.IngestedAt = now;
                    toInsert.Add(scene);
                }
                else if (current.ItemSha256 == scene.ItemSha256)
                {
                    unchanged++;
                }
                else
                {
                    scene.Id = current.Id;
                    scene.IngestedAt = now;
                    toReplace.Add(scene);
                }
            }

            if (toInsert.Count > 0) await store.Scenes.InsertManyAsync(PlatformTenant.Scope, toInsert, ct);
            if (toReplace.Count > 0) await store.Scenes.ReplaceManyAsync(PlatformTenant.Scope, toReplace, ct);
            foreach (var s in toReplace) await audit.RecordAsync(PlatformTenant.Scope, "system", "scene.item_changed", "scene", s.SceneId, ct: ct);
            inserted += toInsert.Count;
            updated += toReplace.Count;
        }

        return new CatalogueChange(inserted, updated, unchanged);
    }

    /// <summary>Scenes sensed inside the period, always in the same order: sensing time, then scene id.</summary>
    public async Task<IReadOnlyList<Scene>> ListAsync(DateTime from, DateTime to, string? tile = null, CancellationToken ct = default)
    {
        var scenes = tile is null
            ? await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SensingTime >= from && s.SensingTime <= to, 1000, ct)
            : await store.Scenes.FindAsync(PlatformTenant.Scope, s => s.SensingTime >= from && s.SensingTime <= to && s.Tile == tile, 1000, ct);
        return [.. scenes.OrderBy(s => s.SensingTime).ThenBy(s => s.SceneId, StringComparer.Ordinal)];
    }
}
