using Zr3i.Kernel.Imagery;
using Zr3i.Kernel.Jobs;

namespace Zr3i.Imagery;

/// <summary>
/// Runs one scene search and records what it finds in the shared catalogue. Safe to run again: scenes already known are left alone.
/// A provider that is down or rate-limiting is retried later; a request the provider rejects is a final failure.
/// </summary>
public sealed class DiscoverHandler(ISceneSource source, SceneCatalog catalog) : IJobHandler
{
    public string Type => DiscoveryPlanner.JobType;

    public async Task<JobOutcome> HandleAsync(JobContext context, CancellationToken ct)
    {
        var p = context.Payload<DiscoverPayload>();
        SceneSearchResult found;
        try
        {
            found = await source.SearchAsync(new SceneQuery(p.MinLon, p.MinLat, p.MaxLon, p.MaxLat, p.From, p.To, p.MaxCloudCover), ct);
        }
        catch (StacException e) when (e.StatusCode is null or >= 429)
        {
            return JobOutcome.RetryLater("provider_unavailable");
        }
        catch (StacException)
        {
            return JobOutcome.Failure("provider_rejected");
        }
        catch (ArgumentException)
        {
            return JobOutcome.Failure("invalid_query");
        }

        var change = await catalog.UpsertAsync(found.Scenes, ct);
        return JobOutcome.Success(JobJson.Serialize(new
        {
            found = found.Scenes.Count,
            skipped = found.Skipped,
            inserted = change.Inserted,
            updated = change.Updated,
            unchanged = change.Unchanged,
            source = source.Name,
        }));
    }
}
