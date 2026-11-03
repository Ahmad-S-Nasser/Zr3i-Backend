using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zr3i.Imagery;
using Zr3i.Kernel.Imagery;
using Xunit;

namespace Zr3i.Tests.Unit.Imagery;

public class StacClientTests
{
    private static readonly SceneQuery Query = new(30.9, 30.7, 31.1, 30.9, new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), 20);

    private sealed class Script(params Func<string, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!, body));
            return responses[Math.Min(Requests.Count - 1, responses.Length - 1)](body);
        }
    }

    private static HttpResponseMessage Ok(JsonNode json) => new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Status(HttpStatusCode code, int? retryAfterSeconds = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("{}") };
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    /// <summary>The trimmed real Earth Search response saved from the W1-5 spike (3 scenes over the Nile Delta).</summary>
    private static JsonObject Fixture()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "tests", "Zr3i.Tests.Unit", "Imagery", "fixtures", "earth-search-sample.json")))!.AsObject();
    }

    private static JsonObject Page(JsonObject fixture, int[] items, JsonObject? next = null)
    {
        var all = fixture["features"]!.AsArray();
        var page = new JsonObject { ["type"] = "FeatureCollection", ["features"] = new JsonArray([.. items.Select(i => JsonNode.Parse(all[i]!.ToJsonString()))]) };
        page["links"] = next is null ? new JsonArray() : new JsonArray(next);
        return page;
    }

    private static StacClient Client(Script script, List<TimeSpan>? delays = null, int maxAttempts = 4) =>
        new(new HttpClient(script), new StacOptions(MaxAttempts: maxAttempts, BaseDelay: TimeSpan.FromSeconds(1)), (d, _) => { delays?.Add(d); return Task.CompletedTask; });

    [Fact]
    public async Task ReadsTheRealResponseIntoSceneRecordsWithTheirProvenance()
    {
        var result = await Client(new Script(_ => Ok(Fixture()))).SearchAsync(Query);

        Assert.Equal(0, result.Skipped);
        Assert.Equal(["S2C_36RTV_20261024_0_L2A", "S2C_36RUV_20261027_0_L2A", "S2C_36RTV_20261027_0_L2A"], result.Scenes.Select(s => s.SceneId));   // by sensing time: RUV was sensed 5 s before RTV
        var s = result.Scenes.Single(x => x.SceneId == "S2C_36RTV_20261027_0_L2A");
        Assert.Equal(("earth-search", "sentinel-2-l2a", "36RTV", 32636, "05.13", true), (s.Source, s.Collection, s.Tile, s.Epsg, s.ProcessingBaseline, s.BoaOffsetApplied));
        Assert.Equal(new DateTime(2026, 10, 27, 8, 51, 36, 37, DateTimeKind.Utc), s.SensingTime);
        Assert.Equal(0.01643, s.CloudCover, precision: 5);
        Assert.Equal(["blue", "green", "nir", "red", "scl"], s.Assets.Keys.Order());
        Assert.EndsWith("/B04.tif", s.Assets["red"], StringComparison.Ordinal);
        Assert.StartsWith("https://", s.Assets["scl"], StringComparison.Ordinal);
        Assert.Equal("sentinel-2c", s.Platform);
        Assert.Equal("Contains modified Copernicus Sentinel data 2026", s.Attribution);
        Assert.Matches("^[0-9A-F]{64}$", s.ItemSha256);
        Assert.Equal(3, result.Scenes.Select(x => x.ItemSha256).Distinct().Count());
        Assert.InRange(s.MinLon, 29, 32);
    }

    [Fact]
    public async Task TheSearchAsksForExactlyWhatWasRequested()
    {
        var script = new Script(_ => Ok(Fixture()));
        await Client(script).SearchAsync(Query);

        var (method, uri, body) = script.Requests.Single();
        var json = JsonNode.Parse(body)!;
        Assert.Equal((HttpMethod.Post, "https://earth-search.aws.element84.com/v1/search"), (method, uri.ToString()));
        Assert.Equal("sentinel-2-l2a", json["collections"]![0]!.GetValue<string>());
        Assert.Equal([30.9, 30.7, 31.1, 30.9], json["bbox"]!.AsArray().Select(n => n!.GetValue<double>()));
        Assert.Equal("2026-09-01T00:00:00Z/2026-11-01T00:00:00Z", json["datetime"]!.GetValue<string>());
        Assert.Equal(20, json["query"]!["eo:cloud_cover"]!["lte"]!.GetValue<double>());
        Assert.Equal(100, json["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task TheOrderDoesNotDependOnTheOrderTheProviderAnswersIn()
    {
        var forward = Fixture();
        var backward = Fixture();
        var items = backward["features"]!.AsArray();
        var reversed = new JsonArray([.. items.Reverse().Select(i => JsonNode.Parse(i!.ToJsonString()))]);
        backward["features"] = reversed;

        var a = await Client(new Script(_ => Ok(forward))).SearchAsync(Query);
        var b = await Client(new Script(_ => Ok(backward))).SearchAsync(Query);

        Assert.Equal(a.Scenes.Select(s => s.SceneId), b.Scenes.Select(s => s.SceneId));
        Assert.Equal(a.Scenes.Select(s => s.ItemSha256), b.Scenes.Select(s => s.ItemSha256));
    }

    [Fact]
    public async Task FollowsTheNextLinkUntilThereIsNoneAndCountsEachSceneOnce()
    {
        var f = Fixture();
        var next = new JsonObject { ["rel"] = "next", ["method"] = "POST", ["href"] = "https://earth-search.aws.element84.com/v1/search", ["body"] = new JsonObject { ["next"] = "token-2", ["limit"] = 100 } };
        var script = new Script(_ => Ok(Page(f, [0, 1], next)), _ => Ok(Page(f, [1, 2])));        // scene 1 appears on both pages

        var result = await Client(script).SearchAsync(Query);

        Assert.Equal(3, result.Scenes.Count);
        Assert.Equal(2, script.Requests.Count);
        Assert.Contains("token-2", script.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetryableFailureIsRetriedWithGrowingDelaysThenSucceeds()
    {
        var delays = new List<TimeSpan>();
        var script = new Script(_ => Status(HttpStatusCode.ServiceUnavailable), _ => Status(HttpStatusCode.BadGateway), _ => Ok(Fixture()));

        var result = await Client(script, delays).SearchAsync(Query);

        Assert.Equal(3, result.Scenes.Count);
        Assert.Equal(3, script.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task ARateLimitReplyIsWaitedOutAsTheProviderAsks()
    {
        var delays = new List<TimeSpan>();
        var script = new Script(_ => Status(HttpStatusCode.TooManyRequests, retryAfterSeconds: 7), _ => Ok(Fixture()));

        await Client(script, delays).SearchAsync(Query);

        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task ItGivesUpAfterTheLastAttemptWithAClearError()
    {
        var script = new Script(_ => Status(HttpStatusCode.ServiceUnavailable));

        var e = await Assert.ThrowsAsync<StacException>(() => Client(script, maxAttempts: 3).SearchAsync(Query));

        Assert.Equal(3, script.Requests.Count);
        Assert.Equal(503, e.StatusCode);
    }

    [Fact]
    public async Task ARejectedRequestIsNotRetried()
    {
        var script = new Script(_ => Status(HttpStatusCode.BadRequest));

        var e = await Assert.ThrowsAsync<StacException>(() => Client(script).SearchAsync(Query));

        Assert.Single(script.Requests);
        Assert.Equal(400, e.StatusCode);
    }

    [Fact]
    public async Task ASceneMissingABandWeNeedIsSkippedAndCountedNotSilentlyDropped()
    {
        var f = Fixture();
        f["features"]![1]!["assets"]!.AsObject().Remove("scl");
        f["features"]![2]!["assets"]!.AsObject().Remove("nir");

        var result = await Client(new Script(_ => Ok(f))).SearchAsync(Query);

        Assert.Single(result.Scenes);
        Assert.Equal(2, result.Skipped);
    }

    [Fact]
    public async Task AnItemThatChangesAtTheProviderGetsADifferentHash()
    {
        var before = Fixture();
        var after = Fixture();
        after["features"]![0]!["properties"]!["eo:cloud_cover"] = 55.5;

        var a = (await Client(new Script(_ => Ok(before))).SearchAsync(Query)).Scenes.Single(s => s.SceneId == "S2C_36RTV_20261027_0_L2A");
        var b = (await Client(new Script(_ => Ok(after))).SearchAsync(Query)).Scenes.Single(s => s.SceneId == "S2C_36RTV_20261027_0_L2A");
        var again = (await Client(new Script(_ => Ok(before))).SearchAsync(Query)).Scenes.Single(s => s.SceneId == "S2C_36RTV_20261027_0_L2A");

        Assert.NotEqual(a.ItemSha256, b.ItemSha256);
        Assert.Equal(a.ItemSha256, again.ItemSha256);
    }

    [Fact]
    public async Task CancellationStopsTheSearch()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new Script(_ => Ok(Fixture()))).SearchAsync(Query, cts.Token));
    }

    [Theory]
    [InlineData(31.0, 30.0, 30.9, 30.9)]      // min lon above max lon
    [InlineData(30.9, 30.9, 31.0, 30.8)]      // min lat above max lat
    [InlineData(-200.0, 30.0, 31.0, 30.9)]
    public async Task ANonsenseAreaIsRefusedBeforeAnyRequest(double minLon, double minLat, double maxLon, double maxLat)
    {
        var script = new Script(_ => Ok(Fixture()));

        await Assert.ThrowsAsync<ArgumentException>(() => Client(script).SearchAsync(Query with { MinLon = minLon, MinLat = minLat, MaxLon = maxLon, MaxLat = maxLat }));

        Assert.Empty(script.Requests);
    }

    [Fact]
    public async Task APeriodThatEndsBeforeItStartsIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Client(new Script(_ => Ok(Fixture()))).SearchAsync(Query with { To = Query.From.AddDays(-1) }));
    }

    [Fact]
    public async Task TheClientReadsJsonItDoesNotUnderstandWithoutCrashing()
    {
        var garbage = new Script(_ => Ok(new JsonObject { ["unexpected"] = "shape" }));
        var result = await Client(garbage).SearchAsync(Query);

        Assert.Empty(result.Scenes);
        await Assert.ThrowsAsync<StacException>(() => Client(new Script(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") })).SearchAsync(Query));
        _ = JsonSerializer.Serialize(result.Skipped);
    }
}
