using System.Collections.Concurrent;
using MaxRev.Gdal.Core;
using OSGeo.GDAL;
using Zr3i.Geo;

namespace Zr3i.Imagery;

/// <summary>A block of one band, read for a set of lattice pixels, with what is needed to look any of them up.</summary>
public sealed class BandWindow(int[] values, PixelWindow window, RasterGrid grid)
{
    public int[] Values { get; } = values;
    public PixelWindow Window { get; } = window;
    public RasterGrid Grid { get; } = grid;

    /// <summary>The value at a 10 m lattice pixel, whatever this band's own resolution is.</summary>
    public int ValueAt(int latticeCol, int latticeRow)
    {
        var (c, r) = Grid.CellOf(latticeCol, latticeRow);
        return Values[Window.IndexOf(c, r)];
    }
}

/// <summary>Reads only the part of a band that holds the given pixels. The ingest job depends on this, not on GDAL.</summary>
public interface IBandReader
{
    /// <summary>Who read it, for provenance (for example "gdal 3.13.3").</summary>
    string Name { get; }

    Task<BandWindow> ReadAsync(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct = default);
}

/// <summary>
/// Reads cloud-optimised GeoTIFFs with GDAL (bundled native libraries, no system install). Remote files are opened once and kept open;
/// reading a window fetches only the blocks it needs, and GDAL's block cache lets neighbouring parcels reuse them. TLS verification is
/// always on: the bundled libcurl needs a CA file, taken from ZR3I_CA_BUNDLE, or on Windows from Git's bundle for development.
/// </summary>
public sealed class GdalBandReader : IBandReader, IDisposable
{
    private static readonly object Setup = new();
    private static bool _configured;

    private readonly ConcurrentDictionary<string, Lazy<Opened>> _open = new();

    public GdalBandReader(string? caBundle = null)
    {
        lock (Setup)
        {
            if (_configured) return;
            GdalBase.ConfigureAll();
            var ca = caBundle ?? Environment.GetEnvironmentVariable("ZR3I_CA_BUNDLE") ?? DevelopmentBundle();
            if (ca is not null)
            {
                Gdal.SetConfigOption("GDAL_CURL_CA_BUNDLE", ca);
                Gdal.SetConfigOption("CURL_CA_BUNDLE", ca);
            }

            Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
            Gdal.SetConfigOption("CPL_VSIL_CURL_ALLOWED_EXTENSIONS", ".tif");
            Gdal.SetConfigOption("GDAL_HTTP_MAX_RETRY", "3");
            Gdal.SetConfigOption("GDAL_HTTP_RETRY_DELAY", "2");
            Gdal.SetConfigOption("VSI_CACHE", "TRUE");
            Gdal.SetConfigOption("VSI_CACHE_SIZE", "52428800");
            Gdal.SetConfigOption("CPL_VSIL_CURL_CACHE_SIZE", "104857600");
            Gdal.SetConfigOption("GDAL_CACHEMAX", "256");
            _configured = true;
        }
    }

    public string Name => $"gdal {Gdal.VersionInfo("RELEASE_NAME")}";

    public Task<BandWindow> ReadAsync(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct = default) =>
        Task.Run(() => ReadCore(href, latticePixels, ct), ct);

    private BandWindow ReadCore(string href, IReadOnlyList<(int Col, int Row)> latticePixels, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opened = _open.GetOrAdd(href, h => new Lazy<Opened>(() => Open(h))).Value;

        var window = opened.Grid.WindowOf(latticePixels);
        if (window.XOff < 0 || window.YOff < 0 || window.XOff + window.Width > opened.Width || window.YOff + window.Height > opened.Height)
            throw new InvalidOperationException("The parcel lies outside this raster.");

        var values = new int[window.Cells];
        lock (opened)      // a GDAL dataset is not safe to read from two threads at once
        {
            using var band = opened.Dataset.GetRasterBand(1);
            band.ReadRaster(window.XOff, window.YOff, window.Width, window.Height, values, window.Width, window.Height, 0, 0);
        }

        return new BandWindow(values, window, opened.Grid);
    }

    private static Opened Open(string href)
    {
        var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? "/vsicurl/" + href : href;
        var ds = Gdal.Open(path, Access.GA_ReadOnly) ?? throw new InvalidOperationException("Cannot open the raster.");
        var gt = new double[6];
        ds.GetGeoTransform(gt);
        if (gt[2] != 0 || gt[4] != 0 || gt[5] >= 0) throw new InvalidOperationException("Only north-up rasters are supported.");
        return new Opened(ds, new RasterGrid(gt[0], gt[3], gt[1]), ds.RasterXSize, ds.RasterYSize);
    }

    private static string? DevelopmentBundle()
    {
        var git = @"C:\Program Files\Git\mingw64\etc\ssl\certs\ca-bundle.crt";
        return OperatingSystem.IsWindows() && File.Exists(git) ? git : null;
    }

    public void Dispose()
    {
        foreach (var o in _open.Values.Where(l => l.IsValueCreated)) o.Value.Dataset.Dispose();
        _open.Clear();
    }

    private sealed record Opened(Dataset Dataset, RasterGrid Grid, int Width, int Height);
}
