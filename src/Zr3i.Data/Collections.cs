namespace Zr3i.Data;

/// <summary>Collection names, in one place so the migrations and the repositories cannot drift apart.</summary>
public static class Collections
{
    public const string Organisations = "organisations";
    public const string Users = "users";
    public const string Memberships = "memberships";
    public const string Roles = "roles";
    public const string Invitations = "invitations";
    public const string Sessions = "sessions";
    public const string LoginChallenges = "login_challenges";
    public const string AuditEvents = "audit_events";
    public const string Portfolios = "portfolios";
    public const string Farms = "farms";
    public const string Parcels = "parcels";
    public const string ParcelSeasons = "parcel_seasons";
    public const string Boundaries = "boundaries";
    public const string CustomFieldDefinitions = "custom_field_definitions";
    public const string Jobs = "jobs";
    public const string ImportBatches = "import_batches";
    public const string ImportChunks = "import_chunks";
    public const string ImportRows = "import_rows";
}
