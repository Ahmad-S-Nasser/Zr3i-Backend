using MongoDB.Bson;
using MongoDB.Driver;
using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Methods;
using Xunit;

namespace Zr3i.Tests.Integration;

[Collection(MongoCollection.Name)]
public class MethodRegistryTests(MongoFixture mongo)
{
    private const string R0 = "ZR3I-R0-2026-01";

    private async Task<KernelHarness> NewAsync()
    {
        var k = new KernelHarness(mongo);
        await k.MigrateAsync();
        await k.Methods.EnsureBuiltInsAsync();
        return k;
    }

    private static async Task<string[]> Refused(Func<Task> action) => [.. (await Assert.ThrowsAsync<ValidationFailedException>(action)).Codes];

    /// <summary>Settles every undecided parameter with a provisional value, as a stand-in for the partner's eventual answers.</summary>
    private static async Task FillUndecidedAsync(KernelHarness k)
    {
        var draft = (await k.Methods.GetAsync(R0))!;
        foreach (var p in draft.Parameters.Where(p => p.Status == ParameterStatus.Undecided))
            await k.Methods.SetParameterAsync("admin-1", R0, draft.Version, p.Key, "stand-in", ParameterStatus.StartingRule, "test");
    }

    [Fact]
    public async Task TheR0MethodStartsAsADraftWithTheDocumentedValuesAndNamedGaps()
    {
        var k = await NewAsync();

        var m = (await k.Methods.GetAsync(R0))!;

        Assert.Equal((R0, 1, "draft", null), (m.MethodId, m.Version, m.State, m.ParametersSha256));
        Assert.Equal("2", m.Get("area_gap.boundary_offset_m"));               // owner decision 1: the documented 2 m model
        Assert.Equal("1", m.Get("area_gap.flag_multiplier"));                 // owner decision 2
        Assert.Contains("5 m", m.Get("stats.population"));                    // owner decision 3: whole pixels inside the 5 m buffer
        Assert.Equal(("5", "8", "0.6"), (m.Get("pixel.buffer_m"), m.Get("pixel.min_full_pixels"), m.Get("pixel.max_lost_share")));
        Assert.Equal(ParameterStatus.Derived, m.Parameters.Single(p => p.Key == "reflectance.rule").Status);
        Assert.Equal(ParameterStatus.StartingRule, m.Parameters.Single(p => p.Key == "crop.beet.cycle_days").Status);

        // The eight that were undecided were settled by the owner on 2026-11-04 (what each says is recorded, with its source).
        Assert.DoesNotContain(m.Parameters, p => p.Status == ParameterStatus.Undecided);
        Assert.Equal("3", m.Get("crop.min_valid_scenes"));
        Assert.Contains("B8A", m.Get("index.ndmi.formula"));
        Assert.Contains("no combined verdict", m.Get("verdict.precedence"));
        Assert.Contains("seedling", m.Get("emergence.definition"));
        Assert.All(new[] { "emergence.tolerance_days", "pass.min_separation_days" }, k2 => Assert.Equal("per crop", m.Get(k2)));
        Assert.All(new[] { "crop.min_valid_scenes", "crop.indeterminate.rule", "scene.quality_thresholds", "emergence.definition", "emergence.tolerance_days",
            "index.ndmi.formula", "pass.min_separation_days", "verdict.precedence" }, key =>
            Assert.StartsWith("Owner decision 2026-11-04", m.Parameters.Single(p => p.Key == key).Source, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EveryParameterNamesItsSourceAndAnUndecidedOneHasNoValue()
    {
        var m = (await (await NewAsync()).Methods.GetAsync(R0))!;

        Assert.All(m.Parameters, p => Assert.False(string.IsNullOrWhiteSpace(p.Source), p.Key));
        Assert.All(m.Parameters.Where(p => p.Status == ParameterStatus.Undecided), p => Assert.Null(p.Value));
        Assert.All(m.Parameters.Where(p => p.Status != ParameterStatus.Undecided), p => Assert.False(string.IsNullOrWhiteSpace(p.Value), p.Key));
        Assert.Equal(m.Parameters.Count, m.Parameters.Select(p => p.Key).Distinct().Count());
    }

    [Fact]
    public async Task SeedingTwiceDoesNotCreateASecondVersion()
    {
        var k = await NewAsync();
        await k.Methods.EnsureBuiltInsAsync();
        await new MethodRegistry(k.Store, k.Audit, k.Clock).EnsureBuiltInsAsync();

        Assert.Single(await k.Methods.ListAsync());
    }

    [Fact]
    public async Task ADraftCanBeEditedAndEveryEditIsAudited()
    {
        var k = await NewAsync();

        await k.Methods.SetParameterAsync("admin-1", R0, 1, "crop.min_valid_scenes", "6", ParameterStatus.StartingRule, "Partner proposal 2026-11-06");
        await k.Methods.SetParameterAsync("admin-1", R0, 1, "area_gap.flag_multiplier", "2", ParameterStatus.StartingRule, "Changed for the test");

        var m = (await k.Methods.GetAsync(R0))!;
        Assert.Equal(("6", "2"), (m.Get("crop.min_valid_scenes"), m.Get("area_gap.flag_multiplier")));
        Assert.Equal(2, (await k.Store.Audit.FindAsync(PlatformTenant.Scope, e => e.Action == "method.parameter_set")).Count);
    }

    [Fact]
    public async Task AParameterEditIsChecked()
    {
        var k = await NewAsync();

        Assert.Equal(["invalid_key"], await Refused(() => k.Methods.SetParameterAsync("a", R0, 1, "Bad Key", "1", ParameterStatus.StartingRule, "s")));
        Assert.Equal(["invalid_status"], await Refused(() => k.Methods.SetParameterAsync("a", R0, 1, "x.y", "1", "frozen", "s")));
        Assert.Equal(["value_required"], await Refused(() => k.Methods.SetParameterAsync("a", R0, 1, "x.y", " ", ParameterStatus.StartingRule, "s")));
        Assert.Equal(["source_required"], await Refused(() => k.Methods.SetParameterAsync("a", R0, 1, "x.y", "1", ParameterStatus.StartingRule, "")));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Methods.SetParameterAsync("a", R0, 99, "x.y", "1", ParameterStatus.StartingRule, "s"));
        await Assert.ThrowsAsync<NotFoundException>(() => k.Methods.SetParameterAsync("a", "NO-SUCH", 1, "x.y", "1", ParameterStatus.StartingRule, "s"));

        // Marking something undecided clears its value rather than keeping a stale one.
        var m = await k.Methods.SetParameterAsync("a", R0, 1, "pixel.buffer_m", "ignored", ParameterStatus.Undecided, "reopened");
        Assert.Null(m.Parameters.Single(p => p.Key == "pixel.buffer_m").Value);
    }

    [Fact]
    public async Task AMethodCannotBeFrozenWhileAnythingIsUndecided()
    {
        var k = await NewAsync();
        await k.Methods.SetParameterAsync("admin-1", R0, 1, "verdict.precedence", "ignored", ParameterStatus.Undecided, "reopened");
        await k.Methods.SetParameterAsync("admin-1", R0, 1, "emergence.tolerance_days", "ignored", ParameterStatus.Undecided, "reopened");

        var codes = await Refused(() => k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", []));

        Assert.Contains("undecided:verdict.precedence", codes);
        Assert.Contains("undecided:emergence.tolerance_days", codes);
        Assert.Equal("draft", (await k.Methods.GetAsync(R0))!.State);
    }

    [Fact]
    public async Task EveryProvisionalRuleMustBeApprovedByNameBeforeFreezing()
    {
        var k = await NewAsync();
        await FillUndecidedAsync(k);
        var draft = (await k.Methods.GetAsync(R0))!;
        var provisional = draft.Parameters.Where(p => p.Status == ParameterStatus.StartingRule).Select(p => p.Key).ToList();

        var none = await Refused(() => k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", []));
        Assert.Equal(provisional.Select(key => "unapproved:" + key).Order(), none.Order());

        var missingOne = await Refused(() => k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", [.. provisional.Skip(1)]));
        Assert.Equal(["unapproved:" + provisional[0]], missingOne);

        var unknown = await Refused(() => k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", [.. provisional, "no.such.key"]));
        Assert.Equal(["unknown_approval:no.such.key"], unknown);

        Assert.Equal(["approver_required"], await Refused(() => k.Methods.FreezeAsync("admin-1", R0, 1, " ", [.. provisional])));
        Assert.Equal("draft", (await k.Methods.GetAsync(R0))!.State);
    }

    [Fact]
    public async Task FreezingFixesTheValuesRecordsTheApprovalAndSealsThemWithAHash()
    {
        var k = await NewAsync();
        await FillUndecidedAsync(k);
        var provisional = (await k.Methods.GetAsync(R0))!.Parameters.Where(p => p.Status == ParameterStatus.StartingRule).Select(p => p.Key).ToList();

        var frozen = await k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", provisional);

        Assert.Equal(("frozen", "admin-1"), (frozen.State, frozen.FrozenBy));
        Assert.Matches("^[0-9A-F]{64}$", frozen.ParametersSha256);
        Assert.DoesNotContain(frozen.Parameters, p => p.Status is ParameterStatus.StartingRule or ParameterStatus.Undecided);
        var approved = frozen.Parameters.Single(p => p.Key == "area_gap.boundary_offset_m");
        Assert.Equal(ParameterStatus.Specified, approved.Status);
        Assert.Contains("approved by Dr. Partner", approved.Source, StringComparison.Ordinal);
        Assert.Contains("PE 3A.2", approved.Source, StringComparison.Ordinal);                              // the original source is kept
        Assert.True(await k.Methods.VerifyAsync(R0, 1));
        Assert.Single(await k.Store.Audit.FindAsync(PlatformTenant.Scope, e => e.Action == "method.frozen"));
    }

    [Fact]
    public async Task AFrozenMethodCannotBeChangedFrozenAgainOrQuietlyAltered()
    {
        var k = await NewAsync();
        await FillUndecidedAsync(k);
        var provisional = (await k.Methods.GetAsync(R0))!.Parameters.Where(p => p.Status == ParameterStatus.StartingRule).Select(p => p.Key).ToList();
        await k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", provisional);

        Assert.Equal(["method_frozen"], await Refused(() => k.Methods.SetParameterAsync("a", R0, 1, "area_gap.flag_multiplier", "3", ParameterStatus.StartingRule, "s")));
        Assert.Equal(["method_frozen"], await Refused(() => k.Methods.FreezeAsync("a", R0, 1, "x", [])));
        Assert.Equal("1", (await k.Methods.GetAsync(R0, 1))!.Get("area_gap.flag_multiplier"));

        // Someone changes a value directly in the database: the seal no longer matches.
        await k.Database.GetCollection<BsonDocument>(Collections.MethodVersions).UpdateOneAsync(
            new BsonDocument { { "MethodId", R0 }, { "Version", 1 } },
            Builders<BsonDocument>.Update.Set("Parameters.$[p].Value", "5"),
            new UpdateOptions { ArrayFilters = [new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument("p.Key", "area_gap.flag_multiplier"))] });
        Assert.False(await k.Methods.VerifyAsync(R0, 1));
    }

    [Fact]
    public async Task ANewVersionStartsAsADraftCopyAndTheFrozenOneIsUntouched()
    {
        var k = await NewAsync();
        await FillUndecidedAsync(k);
        var provisional = (await k.Methods.GetAsync(R0))!.Parameters.Where(p => p.Status == ParameterStatus.StartingRule).Select(p => p.Key).ToList();
        var v1 = await k.Methods.FreezeAsync("admin-1", R0, 1, "Dr. Partner", provisional);

        var v2 = await k.Methods.NewDraftFromAsync("admin-1", R0, 1);
        await k.Methods.SetParameterAsync("admin-1", R0, 2, "area_gap.flag_multiplier", "2", ParameterStatus.StartingRule, "Proposed change");

        Assert.Equal((2, "draft", v1.Id), (v2.Version, v2.State, v2.Supersedes));
        Assert.Equal("2", (await k.Methods.GetAsync(R0, 2))!.Get("area_gap.flag_multiplier"));
        Assert.Equal("1", (await k.Methods.GetAsync(R0, 1))!.Get("area_gap.flag_multiplier"));
        Assert.Equal(2, (await k.Methods.GetAsync(R0))!.Version);                                      // "latest" is the new draft
        Assert.True(await k.Methods.VerifyAsync(R0, 1));
        Assert.Equal(["draft_exists"], await Refused(() => k.Methods.NewDraftFromAsync("admin-1", R0, 1)));    // only one open draft at a time
    }

    [Fact]
    public async Task OnlyAFrozenMethodCanBeUsedForAFrozenReport()
    {
        var k = await NewAsync();

        Assert.False((await k.Methods.GetAsync(R0))!.IsFrozen);
        Assert.Equal(["method_not_frozen"], await Refused(() => k.Methods.RequireFrozenAsync(R0, 1)));
    }
}
