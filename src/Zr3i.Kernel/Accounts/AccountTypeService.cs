using Zr3i.Data;

namespace Zr3i.Kernel.Accounts;

/// <summary>Reads account type presets. Presets live in the reserved platform tenant; the built-ins are created the first time they are needed.</summary>
public sealed class AccountTypeService(KernelStore store)
{
    private volatile bool _ensured;

    public async Task<AccountType?> GetAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        await EnsureBuiltInsAsync(ct);
        return (await store.AccountTypes.FindAsync(PlatformTenant.Scope, t => t.Key == key, 1, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<AccountType>> ListAsync(CancellationToken ct = default)
    {
        await EnsureBuiltInsAsync(ct);
        return await store.AccountTypes.FindAsync(PlatformTenant.Scope, _ => true, ct: ct);
    }

    /// <summary>Creates any built-in preset that is missing. Existing presets, including edited ones, are left alone. Safe to call often.</summary>
    public async Task EnsureBuiltInsAsync(CancellationToken ct = default)
    {
        if (_ensured) return;
        var existing = (await store.AccountTypes.FindAsync(PlatformTenant.Scope, _ => true, ct: ct)).Select(t => t.Key).ToHashSet();
        foreach (var builtIn in BuiltInAccountTypes.All.Where(t => !existing.Contains(t.Key)))
        {
            try
            {
                await store.AccountTypes.InsertAsync(PlatformTenant.Scope, Copy(builtIn), ct);
            }
            catch (DuplicateKeyException)
            {
                // another process created it first
            }
        }

        _ensured = true;
    }

    private static AccountType Copy(AccountType t) => new()
    {
        Key = t.Key, LabelEn = t.LabelEn, LabelAr = t.LabelAr, Model = t.Model, SelfSignup = t.SelfSignup, CreatesFarmerProfile = t.CreatesFarmerProfile, HomeView = t.HomeView,
        RoleKeys = [.. t.RoleKeys], Modules = [.. t.Modules], Navigation = [.. t.Navigation],
        SignupFields = [.. t.SignupFields.Select(f => new SignupField { Key = f.Key, LabelEn = f.LabelEn, LabelAr = f.LabelAr, Required = f.Required })],
    };
}
