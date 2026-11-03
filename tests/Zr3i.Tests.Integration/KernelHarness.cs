using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Kernel.Audit;
using Zr3i.Kernel.Identity;

namespace Zr3i.Tests.Integration;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A complete Kernel on a fresh set of collections, with a clock the test controls.</summary>
public sealed class KernelHarness
{
    public const string Password = "a long enough password";

    public KernelHarness(MongoFixture mongo)
    {
        var db = mongo.Database.Client.GetDatabase("zr3i_test_kernel_" + Guid.NewGuid().ToString("N"));
        Database = db;
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
        Store = new KernelStore(new DataStore(db));
        var hasher = new PasswordHasher(iterations: 1000);
        Audit = new AuditService(Store, Clock);
        Organisations = new OrganisationService(Store, Audit, Clock);
        Invitations = new InvitationService(Store, hasher, Audit, Clock);
        SignIn = new SignInService(Store, hasher, Audit, Clock);
        Sessions = new SessionService(Store, Clock);
        Authorization = new AuthorizationService(Store);
    }

    public MongoDB.Driver.IMongoDatabase Database { get; }
    public ManualTimeProvider Clock { get; }
    public KernelStore Store { get; }
    public AuditService Audit { get; }
    public OrganisationService Organisations { get; }
    public InvitationService Invitations { get; }
    public SignInService SignIn { get; }
    public SessionService Sessions { get; }
    public AuthorizationService Authorization { get; }

    /// <summary>Creates an organisation and a signed-up admin user in it. Returns the user and their TOTP secret.</summary>
    public async Task<(Organisation Org, User User, string TotpSecret)> OrgWithAdminAsync(string orgName, string email)
    {
        var org = await Organisations.CreateAsync(orgName, "company");
        var token = await Invitations.InviteAsync(TenantScope.ForTenant(org.Id), invitedByUserId: "system", email, ["org-admin"]);
        var secret = Totp.NewSecret();
        var user = await Invitations.AcceptAsync(token, Password, "en", secret, Totp.CodeAt(secret, Clock.GetUtcNow()));
        return (org, user, secret);
    }

    public async Task<string?> FullSignInAsync(string email, string secret)
    {
        var challenge = await SignIn.BeginAsync(email, Password);
        return challenge is null ? null : await SignIn.CompleteAsync(challenge, Totp.CodeAt(secret, Clock.GetUtcNow()));
    }
}
