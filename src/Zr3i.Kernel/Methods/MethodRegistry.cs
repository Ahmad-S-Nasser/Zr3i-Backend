using System.Text.RegularExpressions;
using Zr3i.Data;
using Zr3i.Kernel.Audit;

namespace Zr3i.Kernel.Methods;

/// <summary>
/// Versioned methods with parameters whose status is explicit. A draft can be edited; freezing needs every parameter settled and every
/// provisional rule approved by name; a frozen version can never change, and a change is a new version. Frozen reports may only cite a
/// frozen version (<see cref="RequireFrozenAsync"/>).
/// </summary>
public sealed partial class MethodRegistry(KernelStore store, AuditService audit, TimeProvider clock)
{
    [GeneratedRegex("^[a-z0-9][a-z0-9_.]{0,79}$")]
    private static partial Regex KeyShape();

    /// <summary>Creates the built-in draft methods that are missing. Safe to call often.</summary>
    public async Task EnsureBuiltInsAsync(CancellationToken ct = default)
    {
        if (await store.MethodVersions.CountAsync(PlatformTenant.Scope, m => m.MethodId == R0Draft.MethodId, ct) > 0) return;
        try
        {
            var draft = R0Draft.Create("system", Now());
            await store.MethodVersions.InsertAsync(PlatformTenant.Scope, draft, ct);
            await audit.RecordAsync(PlatformTenant.Scope, "system", "method.created", "method", draft.Id, purpose: R0Draft.MethodId, ct: ct);
        }
        catch (DuplicateKeyException)
        {
            // another process seeded it first
        }
    }

    public async Task<IReadOnlyList<MethodVersion>> ListAsync(CancellationToken ct = default) =>
        [.. (await store.MethodVersions.FindAsync(PlatformTenant.Scope, _ => true, ct: ct)).OrderBy(m => m.MethodId, StringComparer.Ordinal).ThenBy(m => m.Version)];

    /// <summary>A specific version, or the latest when none is named.</summary>
    public async Task<MethodVersion?> GetAsync(string methodId, int? version = null, CancellationToken ct = default)
    {
        var all = await store.MethodVersions.FindAsync(PlatformTenant.Scope, m => m.MethodId == methodId, ct: ct);
        return version is null ? all.OrderBy(m => m.Version).LastOrDefault() : all.FirstOrDefault(m => m.Version == version);
    }

    /// <exception cref="ValidationFailedException">method_not_frozen.</exception>
    public async Task<MethodVersion> RequireFrozenAsync(string methodId, int version, CancellationToken ct = default)
    {
        var m = await GetAsync(methodId, version, ct) ?? throw new NotFoundException("method", $"{methodId} v{version}");
        return m.IsFrozen ? m : throw new ValidationFailedException("method_not_frozen");
    }

    public async Task<MethodVersion> SetParameterAsync(
        string actor, string methodId, int version, string key, string? value, string status, string source, string? note = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(key) || !KeyShape().IsMatch(key)) errors.Add("invalid_key");
        if (!ParameterStatus.All.Contains(status)) errors.Add("invalid_status");
        else if (status != ParameterStatus.Undecided && string.IsNullOrWhiteSpace(value)) errors.Add("value_required");
        if (string.IsNullOrWhiteSpace(source)) errors.Add("source_required");
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var m = await Require(methodId, version, ct);
        if (m.IsFrozen) throw new ValidationFailedException("method_frozen");

        var p = m.Parameters.FirstOrDefault(x => x.Key == key) ?? new MethodParameter { Key = key };
        if (!m.Parameters.Contains(p)) m.Parameters.Add(p);
        p.Status = status;
        p.Value = status == ParameterStatus.Undecided ? null : value!.Trim();
        p.Source = source.Trim();
        p.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await store.MethodVersions.ReplaceAsync(PlatformTenant.Scope, m, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actor, "method.parameter_set", "method", m.Id, purpose: $"{methodId} v{version}: {key}", ct: ct);
        return m;
    }

    /// <summary>
    /// Freezes a draft. Every parameter must be settled, and every starting rule must be approved by name in
    /// <paramref name="approvedKeys"/>; approved rules become "specified" with the approver recorded beside their source.
    /// </summary>
    /// <exception cref="ValidationFailedException">approver_required, method_frozen, undecided:{key}, unapproved:{key}, unknown_approval:{key}.</exception>
    public async Task<MethodVersion> FreezeAsync(string actor, string methodId, int version, string approvedBy, IReadOnlyList<string> approvedKeys, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ValidationFailedException("approver_required");
        var m = await Require(methodId, version, ct);
        if (m.IsFrozen) throw new ValidationFailedException("method_frozen");

        var undecided = m.Parameters.Where(p => p.Status == ParameterStatus.Undecided).Select(p => "undecided:" + p.Key).ToList();
        if (undecided.Count > 0) throw new ValidationFailedException(undecided);

        var provisional = m.Parameters.Where(p => p.Status == ParameterStatus.StartingRule).Select(p => p.Key).ToHashSet();
        var approved = approvedKeys.ToHashSet();
        var errors = approved.Except(provisional).Select(k => "unknown_approval:" + k).Concat(provisional.Except(approved).Select(k => "unapproved:" + k)).ToList();
        if (errors.Count > 0) throw new ValidationFailedException(errors);

        var now = Now();
        foreach (var p in m.Parameters.Where(p => provisional.Contains(p.Key)))
        {
            p.Status = ParameterStatus.Specified;
            p.Source = $"{p.Source} | approved by {approvedBy.Trim()} on {now:yyyy-MM-dd}";
        }

        m.State = MethodState.Frozen;
        m.FrozenAt = now;
        m.FrozenBy = actor;
        m.ApprovedBy = approvedBy.Trim();
        m.ParametersSha256 = m.ComputeSeal();
        await store.MethodVersions.ReplaceAsync(PlatformTenant.Scope, m, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actor, "method.frozen", "method", m.Id, purpose: $"{methodId} v{version}, approved by {approvedBy.Trim()}", ct: ct);
        return m;
    }

    /// <summary>Opens a new draft version as a copy of an existing one. Only one draft per method at a time.</summary>
    public async Task<MethodVersion> NewDraftFromAsync(string actor, string methodId, int fromVersion, CancellationToken ct = default)
    {
        var source = await Require(methodId, fromVersion, ct);
        var all = await store.MethodVersions.FindAsync(PlatformTenant.Scope, m => m.MethodId == methodId, ct: ct);
        if (all.Any(m => m.State == MethodState.Draft)) throw new ValidationFailedException("draft_exists");

        var draft = new MethodVersion
        {
            MethodId = methodId,
            Name = source.Name,
            Version = all.Max(m => m.Version) + 1,
            State = MethodState.Draft,
            Supersedes = source.Id,
            CreatedAt = Now(),
            CreatedBy = actor,
            Parameters = [.. source.Parameters.Select(p => new MethodParameter { Key = p.Key, Value = p.Value, Unit = p.Unit, Status = p.Status, Source = p.Source, Note = p.Note })],
        };
        await store.MethodVersions.InsertAsync(PlatformTenant.Scope, draft, ct);
        await audit.RecordAsync(PlatformTenant.Scope, actor, "method.drafted", "method", draft.Id, purpose: $"{methodId} v{draft.Version} from v{fromVersion}", ct: ct);
        return draft;
    }

    /// <summary>True if a frozen version still matches its seal: nothing in its parameters has been altered since it was frozen.</summary>
    public async Task<bool> VerifyAsync(string methodId, int version, CancellationToken ct = default)
    {
        var m = await Require(methodId, version, ct);
        return m.IsFrozen && m.ParametersSha256 == m.ComputeSeal();
    }

    private async Task<MethodVersion> Require(string methodId, int version, CancellationToken ct) =>
        await GetAsync(methodId, version, ct) ?? throw new NotFoundException("method", $"{methodId} v{version}");

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;
}
