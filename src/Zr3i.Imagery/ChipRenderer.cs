using Zr3i.Geo;

namespace Zr3i.Imagery;

/// <summary>One pixel of the window as read from the scene: the four 10 m bands and the scene classification, as raw counts.</summary>
public readonly record struct ChipPixel(int Blue, int Green, int Red, int Nir, int Scl);

/// <param name="OriginCol">Lattice column of the window's left edge (the pixel test's 10 m lattice: pixel corners sit on multiples of 10 m).</param>
/// <param name="OriginRow">Lattice row of the window's bottom edge. Rows grow northwards, so the picture's top is the highest row.</param>
/// <param name="At">The pixel at a lattice column and row inside the window.</param>
/// <param name="DeclaredRing">The declared boundary's outer ring, in UTM 36N metres.</param>
/// <param name="Measured">The whole pixels the pixel test counted inside the boundary.</param>
public sealed record ChipInput(
    int OriginCol, int OriginRow, int Width, int Height, int Scale, Func<int, int, ChipPixel> At, SceneRadiometry Radiometry,
    IReadOnlyList<(double X, double Y)> DeclaredRing, IReadOnlySet<(int Col, int Row)> Measured);

public sealed record ChipImages(byte[] TrueColour, byte[] Ndvi, int WidthPx, int HeightPx);

/// <summary>
/// Draws the two pictures for a parcel. Colours are the design-system tokens, so the picture and its legend on screen agree: the NDVI classes
/// (img-ndvi-low below 0.25, mid 0.25 to 0.6, high above 0.6), the declared outline (img-boundary, solid, with a navy halo), the measured
/// footprint (img-measured, dashed) and pixels the scene could not see (not-assessable tone, hatched, never drawn as bare ground).
/// It is a pure function of its input, so the same scene and boundary always give the same bytes.
/// </summary>
public static class ChipRenderer
{
    // Token values from docs/design-system/tokens.json.
    public static readonly (byte R, byte G, byte B) NdviLow = (0xC9, 0xA4, 0x5C);
    public static readonly (byte R, byte G, byte B) NdviMid = (0x9C, 0xBF, 0x6A);
    public static readonly (byte R, byte G, byte B) NdviHigh = (0x2F, 0x7A, 0x4A);
    public static readonly (byte R, byte G, byte B) Boundary = (0xFF, 0xD2, 0x3F);
    public static readonly (byte R, byte G, byte B) Measured = (0x3F, 0xD0, 0xFF);
    public static readonly (byte R, byte G, byte B) Halo = (0x13, 0x1E, 0x29);
    public static readonly (byte R, byte G, byte B) UnusableFill = (0xE8, 0xEC, 0xEE);
    public static readonly (byte R, byte G, byte B) UnusableHatch = (0x55, 0x64, 0x6E);

    public const double NdviMidFrom = 0.25;
    public const double NdviHighFrom = 0.6;

    /// <summary>The picture is drawn for a good look at one parcel, so the window is capped.</summary>
    public const int MaxWindow = 160;

    public static ChipImages Render(ChipInput input)
    {
        if (input.Scale < 1 || input.Width < 1 || input.Height < 1) throw new ArgumentException("The window is empty.", nameof(input));
        int w = input.Width * input.Scale, h = input.Height * input.Scale;
        var colour = new byte[w * h * 3];
        var ndvi = new byte[w * h * 3];

        for (var row = 0; row < input.Height; row++)
        {
            for (var col = 0; col < input.Width; col++)
            {
                var p = input.At(input.OriginCol + col, input.OriginRow + row);
                var top = (input.Height - 1 - row) * input.Scale;     // the highest row is the top of the picture
                var rgb = TrueColour(p, input.Radiometry);
                var usable = IsUsable(p.Scl);
                var n = usable ? NdviOf(p, input.Radiometry) : null;
                for (var dy = 0; dy < input.Scale; dy++)
                {
                    for (var dx = 0; dx < input.Scale; dx++)
                    {
                        var x = col * input.Scale + dx;
                        var y = top + dy;
                        Put(colour, w, x, y, rgb);
                        Put(ndvi, w, x, y, n is null ? ((x + y) % 4 == 0 ? UnusableHatch : UnusableFill) : NdviClass(n.Value));
                    }
                }
            }
        }

        foreach (var buffer in new[] { colour, ndvi })
        {
            DrawMeasured(buffer, w, h, input);
            DrawDeclared(buffer, w, h, input);
        }

        return new ChipImages(PngEncoder.Encode(w, h, colour), PngEncoder.Encode(w, h, ndvi), w, h);
    }

    /// <summary>Vegetation, not vegetated and water are the classes whose pixels count (the same rule as the readability test).</summary>
    public static bool IsUsable(int scl) => PixelTestRules.UsableClasses.Contains(scl);

    public static (byte R, byte G, byte B) NdviClass(double ndvi) => ndvi < NdviMidFrom ? NdviLow : ndvi < NdviHighFrom ? NdviMid : NdviHigh;

    public static double? NdviOf(ChipPixel p, SceneRadiometry radiometry)
    {
        var nir = Reflectance.FromDn(p.Nir, radiometry);
        var red = Reflectance.FromDn(p.Red, radiometry);
        if (nir is null || red is null || !Reflectance.IsPlausible(nir.Value) || !Reflectance.IsPlausible(red.Value)) return null;
        return Indices.NormalisedDifference(nir.Value, red.Value);
    }

    /// <summary>Red, green and blue as the eye would see them: reflectance stretched to about 0.30 with a gentle curve. No data is black.</summary>
    public static (byte R, byte G, byte B) TrueColour(ChipPixel p, SceneRadiometry radiometry)
    {
        byte Channel(int dn)
        {
            var r = Reflectance.FromDn(dn, radiometry);
            if (r is null) return 0;
            var v = Math.Clamp(r.Value / 0.30, 0, 1);
            return (byte)Math.Round(Math.Pow(v, 1 / 1.8) * 255);
        }

        return (Channel(p.Red), Channel(p.Green), Channel(p.Blue));
    }

    private static void Put(byte[] buffer, int width, int x, int y, (byte R, byte G, byte B) c)
    {
        var i = (y * width + x) * 3;
        buffer[i] = c.R;
        buffer[i + 1] = c.G;
        buffer[i + 2] = c.B;
    }

    private static void DrawDeclared(byte[] buffer, int w, int h, ChipInput input)
    {
        var points = input.DeclaredRing.Select(p => (X: (p.X / 10.0 - input.OriginCol) * input.Scale, Y: (input.OriginRow + input.Height - p.Y / 10.0) * input.Scale)).ToList();
        for (var i = 0; i + 1 < points.Count; i++) Line(buffer, w, h, points[i], points[i + 1], 3, Halo, dashed: false);
        for (var i = 0; i + 1 < points.Count; i++) Line(buffer, w, h, points[i], points[i + 1], 2, Boundary, dashed: false);
    }

    /// <summary>The edges between a counted pixel and one that is not, drawn as short dashes so the footprint never looks like the declared outline.</summary>
    private static void DrawMeasured(byte[] buffer, int w, int h, ChipInput input)
    {
        var s = input.Scale;
        foreach (var (col, row) in input.Measured)
        {
            var left = (col - input.OriginCol) * s;
            var top = (input.OriginRow + input.Height - 1 - row) * s;
            if (!input.Measured.Contains((col - 1, row))) Edge(buffer, w, h, left, top, left, top + s);
            if (!input.Measured.Contains((col + 1, row))) Edge(buffer, w, h, left + s, top, left + s, top + s);
            if (!input.Measured.Contains((col, row + 1))) Edge(buffer, w, h, left, top, left + s, top);
            if (!input.Measured.Contains((col, row - 1))) Edge(buffer, w, h, left, top + s, left + s, top + s);
        }
    }

    private static void Edge(byte[] buffer, int w, int h, double x0, double y0, double x1, double y1) =>
        Line(buffer, w, h, (x0, y0), (x1, y1), 1, Measured, dashed: true);

    /// <summary>A line of the given half-width in picture pixels. A dashed line is on for four pixels along the picture and off for four.</summary>
    private static void Line(byte[] buffer, int w, int h, (double X, double Y) a, (double X, double Y) b, int halfWidth, (byte R, byte G, byte B) colour, bool dashed)
    {
        var steps = (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)));
        for (var i = 0; i <= steps; i++)
        {
            var t = steps == 0 ? 0 : (double)i / steps;
            int x = (int)Math.Round(a.X + (b.X - a.X) * t), y = (int)Math.Round(a.Y + (b.Y - a.Y) * t);
            if (dashed && ((x + y) / 4) % 2 != 0) continue;
            for (var dy = -halfWidth + 1; dy < halfWidth; dy++)
            {
                for (var dx = -halfWidth + 1; dx < halfWidth; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px >= 0 && py >= 0 && px < w && py < h) Put(buffer, w, px, py, colour);
                }
            }
        }
    }
}
