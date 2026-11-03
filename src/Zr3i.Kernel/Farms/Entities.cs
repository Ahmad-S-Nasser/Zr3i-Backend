using Zr3i.Data;

namespace Zr3i.Kernel.Farms;

// Tenant-owned (plan v2, CLAUDE.md rule 1): the organisation that imports a farm owns its records, and another organisation
// reaches them only through a grant. ExternalId is the id in the owner's own system; null when there is none. It is unique per
// tenant and collection, which is what makes a re-import idempotent. CustomFields hold validated, canonical string values.

public sealed class Portfolio : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ExternalId { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

public sealed class Farm : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? PortfolioId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Governorate { get; set; }
    public string? Markaz { get; set; }
    public string? Village { get; set; }

    /// <summary>Nullable on purpose: Egypt has no public parcel layer, so a cadastral reference is never a precondition.</summary>
    public string? CadastralRef { get; set; }
    public string? ExternalId { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

/// <summary>The stable spatial unit. Its shape lives in <see cref="BoundaryVersion"/> rows, never on the parcel itself.</summary>
public sealed class Parcel : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string FarmId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? ExternalId { get; set; }

    /// <summary>A pointer to the latest boundary, kept for speed. The boundaries themselves are the record of truth.</summary>
    public string? CurrentBoundaryId { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = [];
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// One parcel in one season, as the customer declares it. Everything "declared" here is a claim, labelled as such;
/// the measured values live elsewhere. Crop verification belongs to the Verification module and is not stored here.
/// </summary>
public sealed class ParcelSeason : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public string FarmId { get; set; } = string.Empty;
    public string Season { get; set; } = string.Empty;
    public string? DeclaredCrop { get; set; }
    public double? DeclaredAreaFeddan { get; set; }
    public string? DeclaredSowingDate { get; set; }
    public string? DeclaredSource { get; set; }
    public DateTime DeclaredAt { get; set; }
    public string? ExternalId { get; set; }
    public Dictionary<string, string> CustomFields { get; set; } = [];
}

/// <summary>
/// One immutable shape of a parcel (append-only). A re-capture is a new row whose <see cref="Supersedes"/> points back at the one it replaces.
/// The current boundary is the latest capture that nothing supersedes. Area is a derived fact of this boundary under
/// <see cref="AreaMethodId"/>, stored with it so the number can always be reproduced.
/// </summary>
public sealed class BoundaryVersion : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string ParcelId { get; set; } = string.Empty;
    public GeoJsonPolygon Geometry { get; set; } = new();
    public double AreaM2 { get; set; }
    public double AreaFeddan { get; set; }
    public string AreaMethodId { get; set; } = string.Empty;
    public int AreaEpsg { get; set; }
    public double CentroidLon { get; set; }
    public double CentroidLat { get; set; }
    public double MinLon { get; set; }
    public double MinLat { get; set; }
    public double MaxLon { get; set; }
    public double MaxLat { get; set; }
    public List<string> Warnings { get; set; } = [];

    /// <summary>Fingerprint of the shape (see BoundaryHash), so a re-import of the same boundary is recognised.</summary>
    public string GeometrySha256 { get; set; } = string.Empty;

    // Provenance
    public string Source { get; set; } = string.Empty;
    public string CapturedBy { get; set; } = string.Empty;
    public DateTime CapturedAt { get; set; }
    public double? AccuracyM { get; set; }
    public string? Supersedes { get; set; }
    public DateTime CreatedAt { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
}

public static class BoundarySources
{
    public const string PhoneGps = "phone_gps";
    public const string Survey = "survey";
    public const string Digitised = "digitised";
    public const string Cadastral = "cadastral";
    public const string Import = "import";

    public static readonly IReadOnlyList<string> All = [PhoneGps, Survey, Digitised, Cadastral, Import];
}
