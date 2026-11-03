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
}
