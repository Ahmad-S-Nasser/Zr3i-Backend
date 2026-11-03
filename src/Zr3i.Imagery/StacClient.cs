using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zr3i.Kernel.Imagery;

namespace Zr3i.Imagery;

/// <param name="MaxAttempts">Per request: tries before giving up on a flaky or rate-limiting provider.</param>
/// <param name="BaseDelay">First wait between tries; doubles each time unless the provider says how long to wait.</param>
public sealed record StacOptions(
    string BaseUrl = "https://earth-search.aws.element84.com/v1",
    string Collection = "sentinel-2-l2a",
    int PageSize = 100,
    int MaxAttempts = 4,
    TimeSpan? BaseDelay = null,
    int MaxPages = 200);

public sealed class StacException(string message, int? statusCode = null, Exception? inner = null) : Exception(message, inner)
{
    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// Scene discovery against a STAC API (Earth Search by default). Third-party and sometimes slow, so every request is retried with a
/// growing delay on rate limits and server errors, honouring Retry-After; other failures stop at once. Results are always returned in
/// the same order (sensing time, then scene id), whatever order the provider answers in, and each scene carries a hash of the item as
/// received. An item that lacks a band we need is skipped and counted.
/// </summary>
public sealed class StacClient(HttpClient http, StacOptions? options = null, Func<TimeSpan, CancellationToken, Task>? delay = null) : ISceneSource
{
    private static readonly string[] RequiredAssets = ["blue", "green", "red", "nir", "scl"];
    private readonly StacOptions _o = options ?? new StacOptions();
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((d, ct) => Task.Delay(d, ct));

    public string Name => "earth-search";

    public async Task<SceneSearchResult> SearchAsync(SceneQuery q, CancellationToken ct = default)
    {
        if (!(q.MinLon is >= -180 and <= 180 && q.MaxLon is >= -180 and <= 180 && q.MinLat is >= -90 and <= 90 && q.MaxLat is >= -90 and <= 90 && q.MinLon < q.MaxLon && q.MinLat < q.MaxLat))
            throw new ArgumentException("The search area is not a valid box in longitude and latitude.", nameof(q));
        if (q.To <= q.From) throw new ArgumentException("The period must end after it starts.", nameof(q));

        var first = new JsonObject
        {
            ["collections"] = new JsonArray(_o.Collection),
            ["bbox"] = new JsonArray(q.MinLon, q.MinLat, q.MaxLon, q.MaxLat),
            ["datetime"] = $"{q.From.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}/{q.To.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}",
            ["query"] = new JsonObject { ["eo:cloud_cover"] = new JsonObject { ["lte"] = q.MaxCloudCover } },
            ["limit"] = _o.PageSize,
            ["sortby"] = new JsonArray(new JsonObject { ["field"] = "properties.datetime", ["direction"] = "asc" }),
        };

        var scenes = new Dictionary<string, Scene>(StringComparer.Ordinal);
        var skipped = 0;
        HttpMethod method = HttpMethod.Post;
        var url = $"{_o.BaseUrl.TrimEnd('/')}/search";
        JsonObject? body = first;

        for (var page = 0; page < _o.MaxPages; page++)
        {
            using var doc = JsonDocument.Parse(await SendAsync(method, url, body, ct));
            var root = doc.RootElement;

            if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in features.EnumerateArray())
                {
                    var scene = ToScene(item);
                    if (scene is null) skipped++;
                    else scenes.TryAdd(scene.SceneId, scene);
                }
            }

            var next = NextLink(root);
            if (next is null) break;
            (method, url, body) = (next.Value.Method, next.Value.Url ?? url, next.Value.Body is { } b ? Merge(first, b, next.Value.Merge) : null);
        }

        return new SceneSearchResult([.. scenes.Values.OrderBy(s => s.SensingTime).ThenBy(s => s.SceneId, StringComparer.Ordinal)], skipped);
    }

    private async Task<string> SendAsync(HttpMethod method, string url, JsonObject? body, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException e) when (attempt < _o.MaxAttempts)
            {
                await _delay(Backoff(attempt, null), ct);
                _ = e;
                continue;
            }
            catch (HttpRequestException e)
            {
                throw new StacException("The scene provider could not be reached.", null, e);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var text = await response.Content.ReadAsStringAsync(ct);
                    try
                    {
                        using var _ = JsonDocument.Parse(text);
                    }
                    catch (JsonException e)
                    {
                        throw new StacException("The scene provider answered with something that is not JSON.", (int)response.StatusCode, e);
                    }

                    return text;
                }

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
                if (!retryable) throw new StacException($"The scene provider refused the request ({(int)response.StatusCode}).", (int)response.StatusCode);
                if (attempt >= _o.MaxAttempts) throw new StacException($"The scene provider kept failing ({(int)response.StatusCode}).", (int)response.StatusCode);

                await _delay(Backoff(attempt, response.Headers.RetryAfter?.Delta), ct);
            }
        }
    }

    private TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter ?? TimeSpan.FromTicks((_o.BaseDelay ?? TimeSpan.FromSeconds(1)).Ticks * (1L << (attempt - 1)));

    private static (HttpMethod Method, string? Url, JsonObject? Body, bool Merge)? NextLink(JsonElement root)
    {
        if (!root.TryGetProperty("links", out var links) || links.ValueKind != JsonValueKind.Array) return null;
        foreach (var link in links.EnumerateArray())
        {
            if (!link.TryGetProperty("rel", out var rel) || rel.GetString() != "next") continue;
            var method = link.TryGetProperty("method", out var m) && string.Equals(m.GetString(), "GET", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post;
            var url = link.TryGetProperty("href", out var h) ? h.GetString() : null;
            JsonObject? body = link.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? JsonNode.Parse(b.GetRawText())!.AsObject() : null;
            var merge = link.TryGetProperty("merge", out var mg) && mg.ValueKind == JsonValueKind.True;
            return (method, url, body, merge);
        }

        return null;
    }

    private static JsonObject Merge(JsonObject original, JsonObject link, bool merge)
    {
        if (!merge) return link;
        var result = JsonNode.Parse(original.ToJsonString())!.AsObject();
        foreach (var (key, value) in link) result[key] = value?.DeepClone();
        return result;
    }

    private Scene? ToScene(JsonElement item)
    {
        if (!item.TryGetProperty("id", out var idElement) || idElement.GetString() is not { Length: > 0 } id) return null;
        if (!item.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object) return null;
        if (!props.TryGetProperty("datetime", out var dt) || !DateTimeOffset.TryParse(dt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sensed)) return null;
        if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) return null;

        var links = new Dictionary<string, string>();
        foreach (var key in RequiredAssets)
        {
            if (!assets.TryGetProperty(key, out var a) || !a.TryGetProperty("href", out var href) || href.GetString() is not { Length: > 0 } h) return null;
            links[key] = h;
        }

        var epsg = props.TryGetProperty("proj:epsg", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32()
            : props.TryGetProperty("proj:code", out var c) && c.GetString() is { } code && code.StartsWith("EPSG:", StringComparison.Ordinal) && int.TryParse(code[5..], out var parsed) ? parsed : 0;
        if (epsg == 0) return null;

        var bbox = item.TryGetProperty("bbox", out var bb) && bb.ValueKind == JsonValueKind.Array && bb.GetArrayLength() >= 4 ? bb.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new double[4];
        return new Scene
        {
            SceneId = id,
            Source = Name,
            Collection = item.TryGetProperty("collection", out var col) ? col.GetString() ?? _o.Collection : _o.Collection,
            Platform = Platform(props, id),
            SensingTime = sensed.UtcDateTime,
            Tile = Tile(props, id),
            Epsg = epsg,
            CloudCover = props.TryGetProperty("eo:cloud_cover", out var cc) && cc.ValueKind == JsonValueKind.Number ? cc.GetDouble() : 100,
            ProcessingBaseline = props.TryGetProperty("s2:processing_baseline", out var pb) ? pb.GetString() ?? string.Empty : string.Empty,
            BoaOffsetApplied = props.TryGetProperty("earthsearch:boa_offset_applied", out var boa) && boa.ValueKind == JsonValueKind.True,
            Assets = links,
            MinLon = bbox[0], MinLat = bbox[1], MaxLon = bbox[2], MaxLat = bbox[3],
            ItemSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.GetRawText()))),
            Attribution = $"Contains modified Copernicus Sentinel data {sensed.UtcDateTime.Year}",
        };
    }

    private static string Tile(JsonElement props, string id)
    {
        if (props.TryGetProperty("grid:code", out var g) && g.GetString() is { } code) return code.StartsWith("MGRS-", StringComparison.Ordinal) ? code[5..] : code;
        if (props.TryGetProperty("s2:mgrs_tile", out var t) && t.GetString() is { Length: > 0 } tile) return tile;
        var parts = id.Split('_');
        return parts.Length > 1 ? parts[1] : string.Empty;
    }

    private static string Platform(JsonElement props, string id)
    {
        if (props.TryGetProperty("platform", out var p) && p.GetString() is { Length: > 0 } platform) return platform.ToLowerInvariant();
        return id.Length >= 3 && id[..2] == "S2" ? $"sentinel-2{char.ToLowerInvariant(id[2])}" : string.Empty;
    }
}
