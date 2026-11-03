using Zr3i.Data;
using Zr3i.Kernel;
using Zr3i.Geo;
using Zr3i.Kernel.Audit;
using Zr3i.Data.Jobs;
using Zr3i.Kernel.Farms;
using Zr3i.Kernel.Imports;
using Zr3i.Kernel.Jobs;
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
        Data = new DataStore(db);
        Store = new KernelStore(Data);
        var hasher = new PasswordHasher(iterations: 1000);
        Audit = new AuditService(Store, Clock);
        Organisations = new OrganisationService(Store, Audit, Clock);
        Invitations = new InvitationService(Store, hasher, Audit, Clock);
        SignIn = new SignInService(Store, hasher, Audit, Clock);
        Sessions = new SessionService(Store, Clock);
        Authorization = new AuthorizationService(Store);
        CustomFields = new CustomFieldService(Store);
        Farms = new FarmService(Store, CustomFields, Audit, Clock);
        Boundaries = new BoundaryService(Store, Audit, Clock);
        Imports = new ImportService(Store, Data.Jobs, Audit, Clock);
        Runner = new JobRunner(
            Data.CreateJobClaimer(new JobQueueOptions(TimeSpan.FromMinutes(2), 2, TimeSpan.Zero)),
            [new ImportValidateHandler(Store, Audit, Clock), new ImportCommitHandler(Store, Audit, Clock)],
            Clock, "test-worker");
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
    public DataStore Data { get; }
    public CustomFieldService CustomFields { get; }
    public FarmService Farms { get; }
    public BoundaryService Boundaries { get; }
    public ImportService Imports { get; }
    public JobRunner Runner { get; }

    /// <summary>Runs every queued job to completion in this process, as the worker would.</summary>
    public Task<int> RunJobsAsync() => Runner.RunUntilIdleAsync();

    /// <summary>Applies the migrations (indexes), as the API does on start. Tests that rely on unique keys or geo queries call this.</summary>
    public Task MigrateAsync() => Data.MigrateAsync();

    /// <summary>The synthetic spike parcel (about 10 feddan near the Nile Delta).</summary>
    public static PolygonInput SpikePolygon()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Zr3i.slnx"))) dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, "spikes", "data", "parcel-synthetic-zr-spike-01.geojson");
        return GeoJsonReader.TryReadPolygon(File.ReadAllText(path), out var polygon, out var error) ? polygon! : throw new InvalidOperationException(error);
    }

    /// <summary>A square of the given side in metres on the EPSG:32636 grid, as the lon/lat a customer would send.</summary>
    public static PolygonInput SquareMetres(double side, double easting = 303800, double northing = 3409200) =>
        PolygonInput.FromExterior(new[] { (easting, northing), (easting + side, northing), (easting + side, northing + side), (easting, northing + side), (easting, northing) }
            .Select(p => { var (lon, lat) = Epsg32636.ToLonLat(p.Item1, p.Item2); return new LonLat(lon, lat); }));

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
