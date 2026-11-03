using Zr3i.Imagery;
using Zr3i.Kernel.Imagery;
using Xunit;

namespace Zr3i.Tests.Unit.Imagery;

/// <summary>
/// Talks to the real Earth Search service. Off by default so the normal run needs no network and cannot be broken by someone else's
/// outage; run it on purpose with ZR3I_NETWORK_TESTS=1. When off, each test returns at once.
/// </summary>
[Trait("Category", "Network")]
public class StacLiveTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("ZR3I_NETWORK_TESTS") == "1";

    [Fact]
    public async Task TheRealServiceReturnsTheScenesTheSpikeSawOverTheNileDelta()
    {
        if (!Enabled) return;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var query = new SceneQuery(30.9, 30.7, 31.1, 30.9, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 31, 0, 0, 0, DateTimeKind.Utc), 5);

        var result = await new StacClient(http).SearchAsync(query);

        Assert.Contains(result.Scenes, s => s.SceneId == "S2C_36RTV_20261027_0_L2A");
        var scene = result.Scenes.Single(s => s.SceneId == "S2C_36RTV_20261027_0_L2A");
        Assert.Equal(("36RTV", 32636, "05.13", true), (scene.Tile, scene.Epsg, scene.ProcessingBaseline, scene.BoaOffsetApplied));
        Assert.EndsWith("/B04.tif", scene.Assets["red"], StringComparison.Ordinal);
        Assert.Equal(result.Scenes.OrderBy(s => s.SensingTime).ThenBy(s => s.SceneId, StringComparer.Ordinal).Select(s => s.SceneId), result.Scenes.Select(s => s.SceneId));
        Assert.Equal(0, result.Skipped);

        // The same question again gives the same items with the same hashes.
        var again = await new StacClient(http).SearchAsync(query);
        Assert.Equal(result.Scenes.Select(s => (s.SceneId, s.ItemSha256)), again.Scenes.Select(s => (s.SceneId, s.ItemSha256)));
    }
}
