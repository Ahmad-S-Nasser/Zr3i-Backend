// W1-5 raster and geometry spike.
// Question answered: can .NET read one parcel window from a Sentinel-2 COG with GDAL, compute the area in EPSG:32636,
// and count the pixels under the polygon, with results that match an independent calculation (and QGIS)?
//
// Usage: dotnet run -- [parcel.geojson] [expected.json] [report.md]
// The scene comes from a STAC search done beforehand (see the constants below).

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using MaxRev.Gdal.Core;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using OSGeo.GDAL;
using OSGeo.OGR;
using OSGeo.OSR;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

const string SceneId = "S2C_36RTV_20261027_0_L2A";
const string SceneBase = "https://sentinel-cogs.s3.us-west-2.amazonaws.com/sentinel-s2-l2a-cogs/36/R/TV/2026/10/" + SceneId;
const double SquareFeddanM2 = 4200.83;
const double InwardBufferM = 5.0;
const double AreaToleranceM2 = 0.05;   // ProjNet vs PROJ vs the independent Python run

var dataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "data"));
var parcelPath = args.Length > 0 ? args[0] : Path.Combine(dataDir, "parcel-synthetic-zr-spike-01.geojson");
var expectedPath = args.Length > 1 ? args[1] : Path.Combine(dataDir, "expected.json");
var reportPath = args.Length > 2 ? args[2] : Path.Combine(dataDir, "report.md");

GdalBase.ConfigureAll();
var ca = Environment.GetEnvironmentVariable("ZR3I_CA_BUNDLE") ?? @"C:\Program Files\Git\mingw64\etc\ssl\certs\ca-bundle.crt";
Gdal.SetConfigOption("GDAL_CURL_CA_BUNDLE", ca);   // TLS verification stays on; the bundled libcurl just needs a CA file
Gdal.SetConfigOption("CURL_CA_BUNDLE", ca);
Gdal.SetConfigOption("GDAL_DISABLE_READDIR_ON_OPEN", "EMPTY_DIR");
Gdal.SetConfigOption("CPL_VSIL_CURL_ALLOWED_EXTENSIONS", ".tif");

if (args.Length > 1 && args[0] == "--bench") return Bench.Run(SceneBase, int.Parse(args[1], CultureInfo.InvariantCulture));

var checks = new List<(string Name, bool Ok, string Detail)>();
void Check(string name, bool ok, string detail) => checks.Add((name, ok, detail));
static string F(double v, string f = "F4") => v.ToString(f, CultureInfo.InvariantCulture);

// 1. Parcel (lon/lat, as a customer would send it) --------------------------------------------------------------------
var ring = ReadRing(parcelPath);
var expected = JsonDocument.Parse(File.ReadAllText(expectedPath)).RootElement;
double Exp(string name) => expected.GetProperty(name).GetDouble();

// 2. Area in EPSG:32636, two independent routes ------------------------------------------------------------------------
var utm = ProjectedCoordinateSystem.WGS84_UTM(36, true);
var toUtm = new CoordinateTransformationFactory().CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, utm).MathTransform;
var utmRing = ring.Select(p => { var o = toUtm.Transform(new[] { p.Lon, p.Lat }); return new Coordinate(o[0], o[1]); }).ToArray();
var gfac = new GeometryFactory();
var parcel = gfac.CreatePolygon(utmRing);                                     // NTS + ProjNet
var areaNts = parcel.Area;

var wgs = new SpatialReference(""); wgs.ImportFromEPSG(4326); wgs.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
var target = new SpatialReference(""); target.ImportFromEPSG(32636); target.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);
var wkt = "POLYGON((" + string.Join(",", ring.Select(p => $"{F(p.Lon, "F7")} {F(p.Lat, "F7")}")) + "))";
using var ogr = OSGeo.OGR.Geometry.CreateFromWkt(wkt);
ogr.AssignSpatialReference(wgs);
ogr.TransformTo(target);                                                      // GDAL/OGR + PROJ
var areaOgr = ogr.Area();

Check("area NTS+ProjNet vs independent (m2)", Math.Abs(areaNts - Exp("planar_area_m2_epsg32636")) < AreaToleranceM2, $"{F(areaNts)} vs {F(Exp("planar_area_m2_epsg32636"))}");
Check("area GDAL/PROJ vs independent (m2)", Math.Abs(areaOgr - Exp("planar_area_m2_epsg32636")) < AreaToleranceM2, $"{F(areaOgr)} vs {F(Exp("planar_area_m2_epsg32636"))}");

var inner = parcel.Buffer(-InwardBufferM);
Check("5 m inward buffer area vs independent (m2)", Math.Abs(inner.Area - Exp("inner5m_area_m2")) < 0.5, $"{F(inner.Area)} vs {F(Exp("inner5m_area_m2"))}");

// 3. Window read from the remote COG ----------------------------------------------------------------------------------
var sw = Stopwatch.StartNew();
using var red = Gdal.Open($"/vsicurl/{SceneBase}/B04.tif", Access.GA_ReadOnly) ?? throw new InvalidOperationException("Cannot open B04.");
var gt = new double[6]; red.GetGeoTransform(gt);
var openMs = sw.ElapsedMilliseconds;
var srs = new SpatialReference(red.GetProjection());
Check("raster CRS is EPSG:32636", srs.GetAuthorityCode(null) == "32636", $"{srs.GetAuthorityName(null)}:{srs.GetAuthorityCode(null)}");
Check("pixel size is 10 m", Math.Abs(gt[1] - 10) < 1e-9 && Math.Abs(gt[5] + 10) < 1e-9, $"{gt[1]} x {gt[5]}");

var env = parcel.EnvelopeInternal;
var x0 = (int)Math.Floor((env.MinX - gt[0]) / gt[1]) - 1;
var x1 = (int)Math.Ceiling((env.MaxX - gt[0]) / gt[1]) + 1;
var y0 = (int)Math.Floor((gt[3] - env.MaxY) / -gt[5]) - 1;
var y1 = (int)Math.Ceiling((gt[3] - env.MinY) / -gt[5]) + 1;
var w = x1 - x0; var h = y1 - y0;
var dn = new int[w * h];
sw.Restart();
using (var band = red.GetRasterBand(1)) band.ReadRaster(x0, y0, w, h, dn, w, h, 0, 0);
var readMs = sw.ElapsedMilliseconds;
Check("window is a small part of the tile", (long)w * h < 0.0001 * red.RasterXSize * red.RasterYSize, $"{w}x{h} px of {red.RasterXSize}x{red.RasterYSize}");

// 4. Pixel tests ------------------------------------------------------------------------------------------------------
var prepParcel = PreparedGeometryFactory.Prepare(parcel);
var prepInner = PreparedGeometryFactory.Prepare(inner);
int centreIn = 0, wholeIn = 0, centreInner = 0, wholeInner = 0;
var inside = new List<(int Col, int Row, double X, double Y)>();
for (var r = 0; r < h; r++)
{
    for (var c = 0; c < w; c++)
    {
        var left = gt[0] + (x0 + c) * gt[1]; var top = gt[3] + (y0 + r) * gt[5];
        var cx = left + gt[1] / 2; var cy = top + gt[5] / 2;
        var square = gfac.CreatePolygon([new(left, top), new(left + gt[1], top), new(left + gt[1], top + gt[5]), new(left, top + gt[5]), new(left, top)]);
        var centre = gfac.CreatePoint(new Coordinate(cx, cy));
        if (prepParcel.Contains(centre)) { centreIn++; inside.Add((c, r, cx, cy)); }
        if (prepParcel.Contains(square)) wholeIn++;
        if (prepInner.Contains(centre)) centreInner++;
        if (prepInner.Contains(square)) wholeInner++;
    }
}

Check("pixels, centre inside polygon", centreIn == (int)Exp("pixels_centre_in_polygon"), $"{centreIn} vs {(int)Exp("pixels_centre_in_polygon")}");
Check("pixels, whole pixel inside polygon", wholeIn == (int)Exp("pixels_whole_in_polygon"), $"{wholeIn} vs {(int)Exp("pixels_whole_in_polygon")}");
Check("pixels, centre inside 5 m inward polygon", centreInner == (int)Exp("pixels_centre_in_inner5m"), $"{centreInner} vs {(int)Exp("pixels_centre_in_inner5m")}");
Check("pixels, whole pixel inside 5 m inward polygon", wholeInner == (int)Exp("pixels_whole_in_inner5m"), $"{wholeInner} vs {(int)Exp("pixels_whole_in_inner5m")}");

// The same pixels read a second way (one-pixel reads): proves the window indexing, not just the geometry.
var mismatches = 0;
using (var band = red.GetRasterBand(1))
{
    var one = new int[1];
    foreach (var p in inside.Take(25))
    {
        band.ReadRaster(x0 + p.Col, y0 + p.Row, 1, 1, one, 1, 1, 0, 0);
        if (one[0] != dn[p.Row * w + p.Col]) mismatches++;
    }
}
Check("window read equals single-pixel reads (25 pixels)", mismatches == 0, $"{mismatches} mismatches");

var values = inside.Select(p => dn[p.Row * w + p.Col]).ToArray();
Check("red band values are plausible digital numbers", values.Length > 0 && values.Min() >= 0 && values.Max() < 20000, $"min {values.Min()}, max {values.Max()}");

// 5. Scene classification at the same pixels (20 m grid, different geotransform) ---------------------------------------
using var scl = Gdal.Open($"/vsicurl/{SceneBase}/SCL.tif", Access.GA_ReadOnly) ?? throw new InvalidOperationException("Cannot open SCL.");
var sgt = new double[6]; scl.GetGeoTransform(sgt);
var sx0 = (int)Math.Floor((env.MinX - sgt[0]) / sgt[1]) - 1; var sx1 = (int)Math.Ceiling((env.MaxX - sgt[0]) / sgt[1]) + 1;
var sy0 = (int)Math.Floor((sgt[3] - env.MaxY) / -sgt[5]) - 1; var sy1 = (int)Math.Ceiling((sgt[3] - env.MinY) / -sgt[5]) + 1;
var sWidth = sx1 - sx0; var sHeight = sy1 - sy0;
var sclData = new int[sWidth * sHeight];
sw.Restart();
using (var band = scl.GetRasterBand(1)) band.ReadRaster(sx0, sy0, sWidth, sHeight, sclData, sWidth, sHeight, 0, 0);
var sclMs = sw.ElapsedMilliseconds;
var classes = new SortedDictionary<int, int>();
foreach (var p in inside)
{
    var sc = (int)Math.Floor((p.X - sgt[0]) / sgt[1]) - sx0; var sr = (int)Math.Floor((sgt[3] - p.Y) / -sgt[5]) - sy0;
    var cls = sclData[sr * sWidth + sc];
    classes[cls] = classes.GetValueOrDefault(cls) + 1;
}
string[] sclNames = ["no data", "saturated/defective", "dark area", "cloud shadow", "vegetation", "not vegetated", "water", "unclassified", "cloud medium", "cloud high", "thin cirrus", "snow/ice"];
Check("scene classification covers every parcel pixel", classes.Values.Sum() == inside.Count, $"{classes.Values.Sum()} of {inside.Count}");

// 6. Report -----------------------------------------------------------------------------------------------------------
var allOk = checks.All(c => c.Ok);
var sb = new StringBuilder();
sb.AppendLine("# W1-5 raster spike report");
sb.AppendLine();
sb.AppendLine($"Result: **{(allOk ? "ALL CHECKS PASS" : "CHECKS FAILED")}**. Parcel `ZR-SPIKE-01` is SYNTHETIC (declared 10.0 feddan). Scene `{SceneId}` (2026-10-27, Sentinel-2 L2A, EPSG:32636, 10 m).");
sb.AppendLine();
sb.AppendLine("## Numbers to compare with QGIS");
sb.AppendLine();
sb.AppendLine("| Quantity | Value |");
sb.AppendLine("| --- | --- |");
sb.AppendLine($"| Area, planar in EPSG:32636 (NTS + ProjNet) | {F(areaNts)} m2 = {F(areaNts / SquareFeddanM2)} feddan |");
sb.AppendLine($"| Area, planar in EPSG:32636 (GDAL/OGR + PROJ) | {F(areaOgr)} m2 |");
sb.AppendLine($"| Area, 5 m inward buffer | {F(inner.Area)} m2 |");
sb.AppendLine($"| Parcel bounding box, EPSG:32636 | E {F(env.MinX, "F2")} to {F(env.MaxX, "F2")}, N {F(env.MinY, "F2")} to {F(env.MaxY, "F2")} |");
sb.AppendLine($"| 10 m pixels, centre inside polygon | {centreIn} |");
sb.AppendLine($"| 10 m pixels, whole pixel inside polygon | {wholeIn} |");
sb.AppendLine($"| 10 m pixels, centre inside 5 m inward polygon | {centreInner} |");
sb.AppendLine($"| 10 m pixels, whole pixel inside 5 m inward polygon | {wholeInner} |");
sb.AppendLine($"| Red band (B04) digital numbers at centre-inside pixels | min {values.Min()}, mean {F(values.Average(), "F1")}, max {values.Max()} |");
sb.AppendLine($"| Scene classification at those pixels | {string.Join(", ", classes.Select(k => $"{k.Key} {(k.Key < sclNames.Length ? sclNames[k.Key] : "?")}: {k.Value}"))} |");
sb.AppendLine();
sb.AppendLine("Which definition of 'pixel inside' the Verification rule uses is not decided here: the R0 specification must say. All four are reported.");
sb.AppendLine();
sb.AppendLine("## Read cost");
sb.AppendLine();
sb.AppendLine($"- Open and read header: {openMs} ms. Red window {w} x {h} px ({(long)w * h * 2} bytes uncompressed): {readMs} ms. Scene classification window {sWidth} x {sHeight} px: {sclMs} ms.");
sb.AppendLine($"- The full red tile is {red.RasterXSize} x {red.RasterYSize} px ({(long)red.RasterXSize * red.RasterYSize * 2 / 1_000_000} MB uncompressed); only the window was requested.");
sb.AppendLine();
sb.AppendLine("## Checks");
sb.AppendLine();
sb.AppendLine("| Check | Result | Detail |");
sb.AppendLine("| --- | --- | --- |");
foreach (var c in checks) sb.AppendLine($"| {c.Name} | {(c.Ok ? "pass" : "FAIL")} | {c.Detail} |");
sb.AppendLine();
sb.AppendLine("## Repeat in QGIS");
sb.AppendLine();
sb.AppendLine($"1. Project CRS EPSG:32636. Add the GeoJSON (it is EPSG:4326) and the raster `/vsicurl/{SceneBase}/B04.tif`.");
sb.AppendLine("2. Area: field calculator `area(transform($geometry,'EPSG:4326','EPSG:32636'))` gives the planar value above. `$area` uses the project ellipsoid and will differ by about 0.015 percent; that is expected.");
sb.AppendLine("3. Pixels: Processing > Zonal statistics (count) with the polygon, or Raster pixels to points then Select by location, using pixel centres; for the buffer use Buffer with distance -5.");

Console.WriteLine(sb.ToString());
File.WriteAllText(reportPath, sb.ToString(), new UTF8Encoding(false));
Console.WriteLine($"Report written to {reportPath}");
return allOk ? 0 : 1;

static List<(double Lon, double Lat)> ReadRing(string path)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var coords = doc.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates")[0];
    return [.. coords.EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble()))];
}
