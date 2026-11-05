using MongoDB.Driver;

namespace Zr3i.Data;

/// <summary>Frozen reports are listed newest first; a report's files are found by report id and name.</summary>
public sealed class ReportIndexesMigration : IMigration
{
    public string Id => "0013-report-indexes";

    public async Task UpAsync(IMongoDatabase database, CancellationToken ct)
    {
        await IndexBuilder.Ascending(database, Collections.FrozenReports, false, ct, "TenantId", "CreatedAt");
        await IndexBuilder.Ascending(database, Collections.ReportFiles, true, ct, "TenantId", "ReportId", "Name");
    }
}
