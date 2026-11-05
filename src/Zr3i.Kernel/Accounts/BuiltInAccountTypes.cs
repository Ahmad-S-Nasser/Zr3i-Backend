using Zr3i.Data;

namespace Zr3i.Kernel.Accounts;

public sealed class SignupField
{
    public string Key { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;
    public string LabelAr { get; set; } = string.Empty;
    public bool Required { get; set; }
}

/// <summary>
/// A preset: everything that makes one kind of customer different, as data. The kernel never asks "is this a bank?"; it reads the
/// preset's roles, modules, navigation, home view and sign-up fields. Zr3i staff can edit presets; the built-ins are only created
/// when missing and are never overwritten.
/// </summary>
public sealed class AccountType : ITenantEntity
{
    public string Id { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string LabelEn { get; set; } = string.Empty;
    public string LabelAr { get; set; } = string.Empty;

    /// <summary>How the customer relates to Zr3i, for documentation and the Account types screen only; nothing branches on it.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>False for Zr3i staff: those accounts are invitation only.</summary>
    public bool SelfSignup { get; set; } = true;

    /// <summary>True when an organisation of this type is one farmer's own account: it gets a farmer profile, so it can own consent.</summary>
    public bool CreatesFarmerProfile { get; set; }
    public List<string> RoleKeys { get; set; } = [];
    public List<string> Modules { get; set; } = [];
    public string HomeView { get; set; } = string.Empty;
    public List<string> Navigation { get; set; } = [];
    public List<SignupField> SignupFields { get; set; } = [];
}

/// <summary>
/// The five built-in account types (plan v2, design system "Account types"). Arabic labels are a first draft and need native review.
/// This is the one place in the source where the type names are written; a test enforces that.
/// </summary>
public static class BuiltInAccountTypes
{
    private static readonly SignupField CommercialRegister = new()
    {
        Key = "commercial_register_number", LabelEn = "Commercial register number", LabelAr = "رقم السجل التجاري", Required = true,
    };

    public static IReadOnlyList<AccountType> All { get; } =
    [
        new()
        {
            Key = "zr3i-admin", LabelEn = "Zr3i admin", LabelAr = "إدارة Zr3i", Model = "provider", SelfSignup = false,
            RoleKeys = ["org-admin", "analyst"], Modules = ["verification", "risk"],
            HomeView = "tenants", Navigation = ["tenants", "access-requests", "account-types", "methods", "users", "audit"], SignupFields = [],
        },
        new()
        {
            Key = "bank", LabelEn = "Bank", LabelAr = "بنك", Model = "b2b_lender",
            RoleKeys = ["org-admin", "analyst", "viewer"], Modules = ["verification", "risk"],
            HomeView = "tranche-check", Navigation = ["tranches", "reports", "users", "audit"], SignupFields = [CommercialRegister],
        },
        new()
        {
            Key = "insurance", LabelEn = "Insurance", LabelAr = "تأمين", Model = "b2b_insurer",
            RoleKeys = ["org-admin", "analyst", "viewer"], Modules = ["verification", "risk"],
            HomeView = "event-evidence", Navigation = ["portfolio", "event-evidence", "risk", "methodology", "users", "audit"], SignupFields = [CommercialRegister],
        },
        new()
        {
            Key = "company", LabelEn = "Company", LabelAr = "شركة", Model = "b2b",
            RoleKeys = ["org-admin", "analyst", "viewer", "field-user"], Modules = ["verification", "risk"],
            HomeView = "portfolio", Navigation = ["portfolio", "farms", "farmers", "risk", "imports", "methodology", "users", "audit"], SignupFields = [CommercialRegister],
        },
        new()
        {
            Key = "farmer", LabelEn = "Farmer", LabelAr = "مزارع", Model = "b2b_b2b2c", CreatesFarmerProfile = true,
            RoleKeys = ["org-admin", "viewer"], Modules = ["verification"],
            HomeView = "my-farms", Navigation = ["my-farms", "consent", "reports"],
            SignupFields = [new SignupField { Key = "mobile_number", LabelEn = "Mobile number", LabelAr = "رقم الهاتف المحمول", Required = true }],
        },
    ];
}
