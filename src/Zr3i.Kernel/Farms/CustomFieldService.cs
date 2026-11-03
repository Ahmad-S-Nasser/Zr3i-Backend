using Zr3i.Data;

namespace Zr3i.Kernel.Farms;

public sealed class CustomFieldService(KernelStore store)
{
    public async Task<CustomFieldDefinition> DefineAsync(
        TenantScope scope,
        string entity,
        string key,
        string labelEn,
        string labelAr,
        string type,
        IReadOnlyList<string>? options = null,
        bool required = false,
        CancellationToken ct = default)
    {
        if (!CustomFieldEntities.All.Contains(entity)) throw new ValidationFailedException("invalid_entity");
        if (!CustomFieldRules.IsValidKey(key)) throw new ValidationFailedException("invalid_key");
        if (!CustomFieldTypes.All.Contains(type)) throw new ValidationFailedException("invalid_type");
        if (string.IsNullOrWhiteSpace(labelEn)) throw new ValidationFailedException("label_en_required");
        if (string.IsNullOrWhiteSpace(labelAr)) throw new ValidationFailedException("label_ar_required");

        var cleanOptions = (options ?? []).Select(o => o?.Trim() ?? string.Empty).Where(o => o.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (type == CustomFieldTypes.Choice && cleanOptions.Count == 0) throw new ValidationFailedException("options_required");

        if ((await store.CustomFieldDefinitions.FindAsync(scope, d => d.Entity == entity && d.Key == key, 1, ct)).Count > 0)
            throw new ValidationFailedException("duplicate_key");

        var definition = new CustomFieldDefinition
        {
            Entity = entity,
            Key = key,
            LabelEn = labelEn.Trim(),
            LabelAr = labelAr.Trim(),
            Type = type,
            Options = type == CustomFieldTypes.Choice ? cleanOptions : [],
            Required = required,
        };
        try
        {
            await store.CustomFieldDefinitions.InsertAsync(scope, definition, ct);
        }
        catch (DuplicateKeyException)
        {
            throw new ValidationFailedException("duplicate_key");
        }

        return definition;
    }

    public async Task<IReadOnlyList<CustomFieldDefinition>> ListAsync(TenantScope scope, string entity, CancellationToken ct = default) =>
        await store.CustomFieldDefinitions.FindAsync(scope, d => d.Entity == entity, ct: ct);

    /// <summary>Checks values against the tenant's definitions for one kind of record and returns them in canonical form.</summary>
    /// <exception cref="ValidationFailedException">Codes look like "custom_field:{key}:{code}".</exception>
    public async Task<Dictionary<string, string>> ValidateAsync(
        TenantScope scope, string entity, IReadOnlyDictionary<string, string?>? values, CancellationToken ct = default)
    {
        var result = CustomFieldRules.Validate(await ListAsync(scope, entity, ct), values);
        if (result.Errors.Count > 0)
            throw new ValidationFailedException([.. result.Errors.Select(e => $"custom_field:{e.Key}:{e.Code}")]);
        return new Dictionary<string, string>(result.Values);
    }
}
