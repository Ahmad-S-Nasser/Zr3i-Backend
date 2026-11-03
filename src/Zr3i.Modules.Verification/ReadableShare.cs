using System.Globalization;

namespace Zr3i.Modules.Verification;

/// <param name="Key">Machine key such as "1-3" or "10+"; the screens turn it into words and units.</param>
/// <param name="ToFeddan">Null for the last, open-ended class.</param>
public sealed record SizeClass(string Key, double FromFeddan, double? ToFeddan);

/// <summary>
/// Parcel size classes from a list of edges in feddan. The edges are configuration, not code: they come from the R0 specification and
/// are shared with the accuracy register. A class includes its lower edge and excludes its upper one.
/// </summary>
public sealed class SizeClassScheme
{
    public SizeClassScheme(IReadOnlyList<double> edgesFeddan)
    {
        ArgumentNullException.ThrowIfNull(edgesFeddan);
        if (edgesFeddan.Count == 0) throw new ArgumentException("At least one edge is needed.", nameof(edgesFeddan));
        if (edgesFeddan.Any(e => !double.IsFinite(e) || e <= 0)) throw new ArgumentException("Edges must be finite and greater than zero.", nameof(edgesFeddan));
        for (var i = 1; i < edgesFeddan.Count; i++)
            if (edgesFeddan[i] <= edgesFeddan[i - 1]) throw new ArgumentException("Edges must be strictly increasing.", nameof(edgesFeddan));

        Edges = [.. edgesFeddan];
        var classes = new List<SizeClass>();
        double from = 0;
        foreach (var edge in Edges)
        {
            classes.Add(new SizeClass($"{Fmt(from)}-{Fmt(edge)}", from, edge));
            from = edge;
        }

        classes.Add(new SizeClass($"{Fmt(from)}+", from, null));
        Classes = classes;
    }

    public IReadOnlyList<double> Edges { get; }

    public IReadOnlyList<SizeClass> Classes { get; }

    public int IndexOf(double areaFeddan)
    {
        for (var i = 0; i < Edges.Count; i++)
            if (areaFeddan < Edges[i]) return i;
        return Edges.Count;
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <param name="Share">Null when the class has no parcels: no figure rather than a zero.</param>
public sealed record ClassShare(string Key, double FromFeddan, double? ToFeddan, int Parcels, int Readable, double? Share);

/// <summary>
/// Readable share by size class. Deliberately has no overall figure: classes are never pooled (design system, Data analysis),
/// because a large share of big parcels would hide a small one for small parcels.
/// </summary>
public static class ReadableShare
{
    public static IReadOnlyList<ClassShare> ByClass(IEnumerable<(double AreaFeddan, bool Readable)> parcels, SizeClassScheme scheme)
    {
        var total = new int[scheme.Classes.Count];
        var readable = new int[scheme.Classes.Count];
        foreach (var (area, ok) in parcels)
        {
            var i = scheme.IndexOf(area);
            total[i]++;
            if (ok) readable[i]++;
        }

        return [.. scheme.Classes.Select((c, i) => new ClassShare(c.Key, c.FromFeddan, c.ToFeddan, total[i], readable[i], total[i] == 0 ? null : (double)readable[i] / total[i]))];
    }
}
