using System.Text;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Jobs;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class ImportTests(MongoFixture mongo)
{
    private static readonly TenantScope A = TenantScope.ForTenant("import-tenant-a");
    private static readonly TenantScope B = TenantScope.ForTenant("import-tenant-b");

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        return k;
    }

    private static List<Dictionary<string, string>> Rows(int count, double side = 150) => [.. Enumerable.Range(0, count).Select(i => ImportCsv.Row(i, side))];

    private static async Task<ImportBatch> ValidateAsync(KernelHarness k, TenantScope scope, string csv, string name = "farms.csv")
    {
        var batch = await k.Imports.UploadAsync(scope, "u1", name, ImportCsv.Stream(csv));
        await k.RunJobsAsync();
        return (await k.Imports.GetAsync(scope, batch.Id)).Batch;
    }

    private static async Task<ImportBatch> ImportAsync(KernelHarness k, TenantScope scope, string csv)
    {
        var batch = await ValidateAsync(k, scope, csv);
        await k.Imports.CommitAsync(scope, "u1", batch.Id);
        await k.RunJobsAsync();
        return (await k.Imports.GetAsync(scope, batch.Id)).Batch;
    }

    private static async Task<string[]> CodesOf(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    [Fact]
    public async Task ValidationStagesEveryRowAndReportsEachProblemWithItsLine()
    {
        var k = await NewAsync();
        var rows = Rows(5);
        rows[1]["declared_area_feddan"] = "ten";
        rows[3]["boundary_geojson"] = ImportCsv.BowTie();

        var batch = await ValidateAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal(("validated", 5, 3, 2), (batch.Status, batch.TotalRows, batch.ValidRows, batch.InvalidRows));
        var errors = await k.Imports.ErrorsPageAsync(A, batch.Id, null, 50);
        Assert.Equal([3, 5], errors.Items.Select(r => r.RowNumber));     // header is line 1, so data row 2 is line 3
        Assert.Equal(["declared_area_feddan:invalid_number"], errors.Items[0].Errors.Select(e => $"{e.Field}:{e.Code}"));
        Assert.Equal(["boundary_geojson:self_intersection"], errors.Items[1].Errors.Select(e => $"{e.Field}:{e.Code}"));

        var csv = await k.Imports.ErrorReportCsvAsync(A, batch.Id);
        Assert.Contains("3,P-00001,F-0000,declared_area_feddan,invalid_number", csv, StringComparison.Ordinal);
        Assert.Contains("5,P-00003,F-0000,boundary_geojson,self_intersection", csv, StringComparison.Ordinal);
        Assert.Empty(await k.Store.Farms.FindAsync(A, _ => true));       // validation writes nothing to the real records
    }

    [Fact]
    public async Task CommitCreatesTheRecordsAndSkipsTheInvalidRows()
    {
        var k = await NewAsync();
        var rows = Rows(7);                          // parcels 0..4 on farm 0, parcels 5..6 on farm 1
        rows[2]["declared_crop"] = "Sugar Beet!";

        var batch = await ImportAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal(("committed", 6, 0, 0, 0), (batch.Status, batch.Created, batch.Updated, batch.Unchanged, batch.Failed));
        Assert.Equal(2, await k.Store.Farms.CountAsync(A, _ => true));
        Assert.Equal(6, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(1, await k.Store.Portfolios.CountAsync(A, _ => true));
        Assert.Equal(6, await k.Store.ParcelSeasons.CountAsync(A, _ => true));
        Assert.Equal(6, await k.Store.Boundaries.FindAsync(A, _ => true).ContinueWith(t => t.Result.Count));

        var parcel = (await k.Store.Parcels.FindAsync(A, p => p.ExternalId == "P-00000"))[0];
        var farm = (await k.Store.Farms.FindAsync(A, f => f.ExternalId == "F-0000"))[0];
        Assert.Equal(("Parcel 0", farm.Id), (parcel.Label, parcel.FarmId));
        Assert.Equal("Kafr El Sheikh", farm.Governorate);
        Assert.NotNull(farm.PortfolioId);

        var boundary = await k.Boundaries.GetCurrentAsync(A, parcel.Id);
        Assert.Equal(22500.0, boundary!.AreaM2, tolerance: 2.0);   // the file gives coordinates to 7 decimals (about 1 cm)
        Assert.Equal(("import", "import:farms.csv"), (boundary.Source, boundary.CapturedBy));
        Assert.Equal(parcel.CurrentBoundaryId, boundary.Id);

        var season = (await k.Store.ParcelSeasons.FindAsync(A, s => s.ParcelId == parcel.Id))[0];
        Assert.Equal(("2026-27-winter", "beet", 5.0), (season.Season, season.DeclaredCrop, season.DeclaredAreaFeddan));
    }

    [Fact]
    public async Task ReimportingTheSameFileCreatesNothingAndChangesNothing()
    {
        var k = await NewAsync();
        var csv = ImportCsv.Build(Rows(12));
        await ImportAsync(k, A, csv);
        var boundariesBefore = (await k.Store.Boundaries.FindAsync(A, _ => true)).Count;
        var auditBefore = (await k.Store.Audit.FindAsync(A, e => e.Action == "boundary.added" || e.Action == "parcel_season.declared")).Count;

        var second = await ImportAsync(k, A, csv);

        Assert.Equal(("committed", 0, 0, 12, 0), (second.Status, second.Created, second.Updated, second.Unchanged, second.Failed));
        Assert.Equal(3, await k.Store.Farms.CountAsync(A, _ => true));
        Assert.Equal(12, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(12, await k.Store.ParcelSeasons.CountAsync(A, _ => true));
        Assert.Equal(boundariesBefore, (await k.Store.Boundaries.FindAsync(A, _ => true)).Count);
        Assert.Equal(auditBefore, (await k.Store.Audit.FindAsync(A, e => e.Action == "boundary.added" || e.Action == "parcel_season.declared")).Count);
    }

    [Fact]
    public async Task AChangedFileUpdatesOnlyWhatChanged()
    {
        var k = await NewAsync();
        await ImportAsync(k, A, ImportCsv.Build(Rows(3)));

        var rows = Rows(3);
        rows[0]["declared_area_feddan"] = "6.5";            // claim amended
        rows[1]["boundary_geojson"] = ImportCsv.Square(1, 200);   // re-captured, a bigger square
        var second = await ImportAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal((0, 2, 1), (second.Created, second.Updated, second.Unchanged));
        var parcels = await k.Store.Parcels.FindAsync(A, _ => true);
        var id0 = parcels.Single(p => p.ExternalId == "P-00000").Id;
        var id1 = parcels.Single(p => p.ExternalId == "P-00001").Id;
        var id2 = parcels.Single(p => p.ExternalId == "P-00002").Id;

        Assert.Equal(6.5, (await k.Store.ParcelSeasons.FindAsync(A, s => s.ParcelId == id0))[0].DeclaredAreaFeddan);
        Assert.Single(await k.Boundaries.GetHistoryAsync(A, id0));
        Assert.Single(await k.Boundaries.GetHistoryAsync(A, id2));

        var history = await k.Boundaries.GetHistoryAsync(A, id1);
        Assert.Equal(2, history.Count);
        Assert.Equal(history[0].Id, history[1].Supersedes);
        Assert.Equal(40000.0, history[1].AreaM2, tolerance: 3.0);
    }

    [Fact]
    public async Task ABlankCellNeverErasesAStoredValue()
    {
        var k = await NewAsync();
        await ImportAsync(k, A, ImportCsv.Build(Rows(2)));

        var rows = Rows(2);
        foreach (var r in rows) { r["governorate"] = ""; r["declared_crop"] = ""; r["boundary_geojson"] = ""; r["portfolio_name"] = ""; }
        var second = await ImportAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal((0, 0, 2), (second.Created, second.Updated, second.Unchanged));
        Assert.Equal("Kafr El Sheikh", (await k.Store.Farms.FindAsync(A, _ => true))[0].Governorate);
        Assert.Equal("beet", (await k.Store.ParcelSeasons.FindAsync(A, _ => true))[0].DeclaredCrop);
        Assert.Equal("Delta book", (await k.Store.Portfolios.FindAsync(A, _ => true))[0].Name);
    }

    [Fact]
    public async Task AParcelNeverMovesToAnotherFarmAndTheRefusedRowLeavesNothingBehind()
    {
        var k = await NewAsync();
        await ImportAsync(k, A, ImportCsv.Build(Rows(3)));

        var rows = Rows(3);
        rows[1]["farm_external_id"] = "F-9999";
        rows[1]["farm_name"] = "A brand new farm";
        var second = await ImportAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal((0, 0, 2, 1), (second.Created, second.Updated, second.Unchanged, second.Failed));
        Assert.Equal(1, await k.Store.Farms.CountAsync(A, _ => true));           // F-9999 was never created
        var errors = await k.Imports.ErrorsPageAsync(A, second.Id, null, 10);
        Assert.Equal(["parcel_belongs_to_other_farm"], errors.Items.Single().Errors.Select(e => e.Code));
        Assert.Contains("parcel_belongs_to_other_farm", await k.Imports.ErrorReportCsvAsync(A, second.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACaptureOlderThanTheCurrentBoundaryFailsOnlyThatRow()
    {
        var k = await NewAsync();
        await ImportAsync(k, A, ImportCsv.Build(Rows(2)));      // boundaries captured "now" (2026-11-05)

        var rows = Rows(2);
        rows[0]["boundary_geojson"] = ImportCsv.Square(0, 200);
        rows[0]["captured_at"] = "2026-01-01";
        rows[1]["declared_area_feddan"] = "7";
        var second = await ImportAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal((0, 1, 0, 1), (second.Created, second.Updated, second.Unchanged, second.Failed));
        Assert.Contains("capture_older_than_current", await k.Imports.ErrorReportCsvAsync(A, second.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicatesAndDisagreementsInsideOneFileAreReported()
    {
        var k = await NewAsync();
        var rows = Rows(4);
        rows.Add(new Dictionary<string, string>(rows[0]));                                         // same parcel and season again
        rows.Add(new Dictionary<string, string>(rows[1]) { ["parcel_external_id"] = "P-NEW", ["farm_name"] = "Same farm, other name" });
        rows.Add(new Dictionary<string, string>(rows[0]) { ["season"] = "2027-summer" });         // same parcel, another season: fine

        var batch = await ValidateAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal((7, 5, 2), (batch.TotalRows, batch.ValidRows, batch.InvalidRows));
        var report = await k.Imports.ErrorReportCsvAsync(A, batch.Id);
        Assert.Contains("duplicate_in_file", report, StringComparison.Ordinal);
        Assert.Contains("conflicting_farm_data", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARowWithTheWrongNumberOfColumnsIsFlaggedAndTheRestAreFine()
    {
        var k = await NewAsync();
        var csv = ImportCsv.Build(Rows(2)) + "P-X,F-X,Short row\r\n";

        var batch = await ValidateAsync(k, A, csv);

        Assert.Equal((3, 2, 1), (batch.TotalRows, batch.ValidRows, batch.InvalidRows));
        Assert.Contains("wrong_column_count", await k.Imports.ErrorReportCsvAsync(A, batch.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CustomColumnsAreCheckedAgainstTheOrganisationsDefinitions()
    {
        var k = await NewAsync();
        await k.CustomFields.DefineAsync(A, CustomFieldEntities.Farm, "irrigation", "Irrigation", "الري", CustomFieldTypes.Choice, ["drip", "pivot"]);
        await k.CustomFields.DefineAsync(A, CustomFieldEntities.Parcel, "soil", "Soil", "التربة", CustomFieldTypes.Text);
        var columns = ImportCsv.Columns.Concat(["farm.irrigation", "parcel.soil"]);
        var rows = Rows(3);
        foreach (var r in rows) { r["farm.irrigation"] = "drip"; r["parcel.soil"] = "clay"; }
        rows[2]["farm.irrigation"] = "flood";

        var batch = await ImportAsync(k, A, ImportCsv.Build(rows, columns));

        Assert.Equal((2, 1), (batch.Created, batch.InvalidRows));
        Assert.Equal("drip", (await k.Store.Farms.FindAsync(A, _ => true))[0].CustomFields["irrigation"]);
        Assert.Equal("clay", (await k.Store.Parcels.FindAsync(A, _ => true))[0].CustomFields["soil"]);
        Assert.Contains("farm.irrigation,invalid_choice", await k.Imports.ErrorReportCsvAsync(A, batch.Id), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArabicTextAndSemicolonDelimitedFilesWork()
    {
        var k = await NewAsync();
        var rows = Rows(1);
        rows[0]["farm_name"] = "مزرعة الدلتا";
        rows[0]["governorate"] = "كفر الشيخ";

        var batch = await ImportAsync(k, A, ImportCsv.Build(rows, delimiter: ';'));

        Assert.Equal(1, batch.Created);
        var farm = (await k.Store.Farms.FindAsync(A, _ => true))[0];
        Assert.Equal(("مزرعة الدلتا", "كفر الشيخ"), (farm.Name, farm.Governorate));
    }

    [Theory]
    [InlineData("parcel_external_id,farm_name\r\nP-1,Farm\r\n", "missing_column:farm_external_id")]
    [InlineData("parcel_external_id,farm_external_id,farm_name,farm_name\r\nP-1,F-1,A,B\r\n", "duplicate_column:farm_name")]
    [InlineData("parcel_external_id,farm_external_id,farm_name,farm.nonsense\r\nP-1,F-1,A,x\r\n", "unknown_custom_field:farm.nonsense")]
    [InlineData("parcel_external_id,farm_external_id,farm_name\r\n", "no_rows")]
    [InlineData("parcel_external_id,farm_external_id,farm_name\r\nP-1,F-1,\"unterminated\r\n", "unterminated_quote:line 2")]
    public async Task AFileWithAStructuralProblemIsRejectedWholeWithTheReason(string csv, string expected)
    {
        var k = await NewAsync();

        var batch = await ValidateAsync(k, A, csv);

        Assert.Equal("failed", batch.Status);
        Assert.Contains(expected, batch.FileErrors);
        Assert.Contains(expected, await k.Imports.ErrorReportCsvAsync(A, batch.Id), StringComparison.Ordinal);
        Assert.Equal(["not_validated"], await CodesOf(() => k.Imports.CommitAsync(A, "u1", batch.Id)));
    }

    [Fact]
    public async Task AFileThatIsNotUtf8IsRejectedAndAnUnknownPlainColumnIsOnlyAWarning()
    {
        var k = await NewAsync();
        var bad = await k.Imports.UploadAsync(A, "u1", "latin.csv", new MemoryStream([0x70, 0x61, 0x72, 0xE9, 0x6E, 0x74, 0x0D, 0x0A, 0x31]));   // "parént" in Latin-1
        await k.RunJobsAsync();
        Assert.Contains("not_utf8", (await k.Imports.GetAsync(A, bad.Id)).Batch.FileErrors);

        var extra = ImportCsv.Build(Rows(2), ImportCsv.Columns.Concat(["notes"]));
        var batch = await ValidateAsync(k, A, extra);
        Assert.Equal(("validated", 2), (batch.Status, batch.ValidRows));
        Assert.Equal(["unknown_column:notes"], batch.FileWarnings);
    }

    [Fact]
    public async Task UploadRefusesEmptyAndOversizedFiles()
    {
        var k = await NewAsync();

        Assert.Equal(["empty_file"], await CodesOf(() => k.Imports.UploadAsync(A, "u1", "x.csv", new MemoryStream())));
        Assert.Equal(["file_too_large"], await CodesOf(() => k.Imports.UploadAsync(A, "u1", "x.csv", new MemoryStream(new byte[ImportService.MaxBytes + 1]))));
        Assert.Empty(await k.Imports.ListAsync(A));
    }

    [Fact]
    public async Task CommitHasToWaitForValidationAndIsSafeToCallTwice()
    {
        var k = await NewAsync();
        var uploaded = await k.Imports.UploadAsync(A, "u1", "farms.csv", ImportCsv.Stream(ImportCsv.Build(Rows(3))));
        Assert.Equal(["not_validated"], await CodesOf(() => k.Imports.CommitAsync(A, "u1", uploaded.Id)));

        await k.RunJobsAsync();
        var first = await k.Imports.CommitAsync(A, "u1", uploaded.Id);
        var again = await k.Imports.CommitAsync(A, "u1", uploaded.Id);
        Assert.Equal(first.CommitJobId, again.CommitJobId);
        Assert.Single(await k.Data.Jobs.ListAsync(A, ImportService.CommitJobType));

        await k.RunJobsAsync();
        var done = await k.Imports.CommitAsync(A, "u1", uploaded.Id);
        Assert.Equal(("committed", 3), (done.Status, done.Created));
        Assert.Single(await k.Data.Jobs.ListAsync(A, ImportService.CommitJobType));
    }

    [Fact]
    public async Task ARepeatedCommitJobFindsNothingLeftToDo()
    {
        var k = await NewAsync();
        var batch = await ImportAsync(k, A, ImportCsv.Build(Rows(4)));
        var parcelsBefore = await k.Store.Parcels.CountAsync(A, _ => true);

        await k.Data.Jobs.EnqueueAsync(A, ImportService.CommitJobType, JobJson.Serialize(new ImportJobPayload(batch.Id)), "u1", k.Clock.GetUtcNow().UtcDateTime);
        await k.RunJobsAsync();

        Assert.Equal(parcelsBefore, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(4, (await k.Imports.GetAsync(A, batch.Id)).Batch.Created);
    }

    [Fact]
    public async Task DiscardingRemovesTheStagedRowsAndBlocksCommit()
    {
        var k = await NewAsync();
        var batch = await ValidateAsync(k, A, ImportCsv.Build(Rows(3)));

        var discarded = await k.Imports.DiscardAsync(A, "u1", batch.Id);

        Assert.Equal("discarded", discarded.Status);
        Assert.Equal(0, await k.Store.ImportRows.CountAsync(A, r => r.BatchId == batch.Id));
        Assert.Equal(["not_validated"], await CodesOf(() => k.Imports.CommitAsync(A, "u1", batch.Id)));

        var committed = await ImportAsync(k, A, ImportCsv.Build(Rows(3)));
        Assert.Equal(["cannot_discard"], await CodesOf(() => k.Imports.DiscardAsync(A, "u1", committed.Id)));
    }

    [Fact]
    public async Task CommitNeedsAtLeastOneValidRow()
    {
        var k = await NewAsync();
        var rows = Rows(2);
        foreach (var r in rows) r["declared_area_feddan"] = "abc";
        var batch = await ValidateAsync(k, A, ImportCsv.Build(rows));

        Assert.Equal(("validated", 0), (batch.Status, batch.ValidRows));
        Assert.Equal(["nothing_to_commit"], await CodesOf(() => k.Imports.CommitAsync(A, "u1", batch.Id)));
    }

    [Fact]
    public async Task AnotherOrganisationCannotSeeOrUseABatchAndItsOwnImportOfTheSameIdsIsSeparate()
    {
        var k = await NewAsync();
        var csv = ImportCsv.Build(Rows(3));
        var batchA = await ImportAsync(k, A, csv);

        await Assert.ThrowsAsync<NotFoundException>(() => k.Imports.GetAsync(B, batchA.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Imports.CommitAsync(B, "u1", batchA.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Imports.ErrorsPageAsync(B, batchA.Id, null, 10));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Imports.ErrorReportCsvAsync(B, batchA.Id));
        Assert.Empty(await k.Imports.ListAsync(B));

        var batchB = await ImportAsync(k, B, csv);

        Assert.Equal(3, batchB.Created);
        Assert.Equal(3, await k.Store.Parcels.CountAsync(A, _ => true));
        Assert.Equal(3, await k.Store.Parcels.CountAsync(B, _ => true));
        Assert.Equal(3, (await k.Imports.GetAsync(A, batchA.Id)).Batch.Created);
    }

    [Fact]
    public async Task TheUploadedFileIsKeptWithItsHashAndSpansChunks()
    {
        var k = await NewAsync();
        var big = new StringBuilder(ImportCsv.Build(Rows(1)));
        while (big.Length < ImportService.ChunkBytes * 2 + 1000) big.Append("P-pad,F-pad,Padding\r\n");   // 3 chunks (the extra rows are wrong-width, which is fine here)

        var batch = await k.Imports.UploadAsync(A, "u1", @"C:\Users\someone\Desktop\farms 2026.csv", ImportCsv.Stream(big.ToString()));

        Assert.Equal("farms 2026.csv", batch.FileName);
        Assert.Matches("^[0-9A-F]{64}$", batch.FileSha256);
        var chunks = await k.Store.ImportChunks.FindAsync(A, c => c.BatchId == batch.Id);
        Assert.Equal([0, 1, 2], chunks.Select(c => c.Seq).Order());
        Assert.Equal(batch.SizeBytes, chunks.Sum(c => (long)c.Data.Length));
    }
}
