using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

/// <summary>Tenant-configurable business category (school, clinic, restaurant, …). The platform never hard-codes a vertical.</summary>
public class LeadCategory : AuditableEntity, ITenantOwned
{
    private LeadCategory() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    public static LeadCategory Create(Guid tenantId, string name, DateTime nowUtc)
    {
        var c = new LeadCategory { TenantId = tenantId, CreatedAtUtc = nowUtc };
        c.Rename(name, nowUtc, touch: false);
        return c;
    }

    public void Rename(string name, DateTime nowUtc, bool touch = true)
    {
        var n = Guard.Required(name, 100, "Category name");
        Name = n; NormalizedName = Normalizer.Name(n); if (touch) UpdatedAtUtc = nowUtc;
    }

    public void SetActive(bool active, DateTime nowUtc) { IsActive = active; UpdatedAtUtc = nowUtc; }
}

/// <summary>Tenant-configurable lifecycle status of a lead record (separate from the sales pipeline stage).</summary>
public class LeadStatus : AuditableEntity, ITenantOwned
{
    private LeadStatus() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int SortOrder { get; private set; }
    /// <summary>Closed statuses (e.g. Disqualified) are informational; they do not change contact permission.</summary>
    public bool IsClosed { get; private set; }
    public bool IsDefault { get; private set; }
    public bool IsActive { get; private set; } = true;

    public static LeadStatus Create(Guid tenantId, string name, int sortOrder, bool isClosed, bool isDefault, DateTime nowUtc) =>
        new() { TenantId = tenantId, Name = Guard.Required(name, 100, "Status name"), SortOrder = sortOrder, IsClosed = isClosed, IsDefault = isDefault, CreatedAtUtc = nowUtc };

    public void Update(string name, bool isClosed, DateTime nowUtc) { Name = Guard.Required(name, 100, "Status name"); IsClosed = isClosed; UpdatedAtUtc = nowUtc; }
    public void SetActive(bool active, DateTime nowUtc) { if (IsDefault && !active) throw new DomainException("The default status cannot be deactivated."); IsActive = active; UpdatedAtUtc = nowUtc; }
    public void SetDefault(bool value, DateTime nowUtc) { IsDefault = value; UpdatedAtUtc = nowUtc; }
}

/// <summary>Editable profile fields of a lead (everything a user types).</summary>
public sealed record LeadProfile(
    string Name, Guid? CategoryId, string? Country, string? Province, string? City, string? District, string? Area,
    string? Address, string? Website, int Confidence);

public class Lead : AuditableEntity, ITenantOwned
{
    private Lead() { }
    public Guid TenantId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string NormalizedName { get; private set; } = string.Empty;
    /// <summary>normalized name | normalized city (see <see cref="Normalizer.DedupKey"/>).</summary>
    public string DedupKey { get; private set; } = string.Empty;
    public Guid? CategoryId { get; private set; }
    public string? Country { get; private set; }
    public string? Province { get; private set; }
    public string? City { get; private set; }
    public string? District { get; private set; }
    public string? Area { get; private set; }
    public string? Address { get; private set; }
    public string? Website { get; private set; }
    public string? WebsiteDomain { get; private set; }
    public Guid PipelineId { get; private set; }
    public Guid PipelineStageId { get; private set; }
    public Guid StatusId { get; private set; }
    public Guid? OwnerUserId { get; private set; }
    public int Score { get; private set; }
    public int? ScoreRuleVersion { get; private set; }
    public DateTime? ScoredAtUtc { get; private set; }
    /// <summary>0–100: how much the team trusts the data of this lead (separate from Score, which ranks sales potential).</summary>
    public int Confidence { get; private set; }
    public LeadSourceType PrimarySourceType { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTime? ArchivedAtUtc { get; private set; }
    public DateTime? LastActivityAtUtc { get; private set; }
    /// <summary>Optimistic concurrency token, incremented on every change.</summary>
    public int Version { get; private set; } = 1;

    public static Lead Create(Guid tenantId, LeadProfile profile, Guid pipelineId, Guid stageId, Guid statusId, Guid? ownerUserId, LeadSourceType source, DateTime nowUtc, string? actor)
    {
        var lead = new Lead
        {
            TenantId = tenantId, PipelineId = pipelineId, PipelineStageId = stageId, StatusId = statusId, OwnerUserId = ownerUserId,
            PrimarySourceType = source, CreatedAtUtc = nowUtc, CreatedBy = actor, LastActivityAtUtc = nowUtc
        };
        lead.SetProfile(profile);
        return lead;
    }

    /// <summary>Applies the profile and recomputes the dedup keys. Returns the names of the fields that changed.</summary>
    public IReadOnlyList<string> UpdateProfile(LeadProfile p, DateTime nowUtc, string? actor)
    {
        if (IsArchived) throw new DomainException("Archived leads cannot be edited. Restore the lead first.");
        var before = (Name, CategoryId, Country, Province, City, District, Area, Address, Website, Confidence);
        SetProfile(p);
        var changed = new List<string>();
        if (before.Name != Name) changed.Add("Name");
        if (before.CategoryId != CategoryId) changed.Add("Category");
        if (before.Country != Country) changed.Add("Country");
        if (before.Province != Province) changed.Add("Province");
        if (before.City != City) changed.Add("City");
        if (before.District != District) changed.Add("District");
        if (before.Area != Area) changed.Add("Area");
        if (before.Address != Address) changed.Add("Address");
        if (before.Website != Website) changed.Add("Website");
        if (before.Confidence != Confidence) changed.Add("Confidence");
        if (changed.Count > 0) Touch(nowUtc, actor);
        return changed;
    }

    private void SetProfile(LeadProfile p)
    {
        Name = Guard.Required(p.Name, 300, "Lead name");
        NormalizedName = Normalizer.Name(Name);
        if (NormalizedName.Length == 0) throw new DomainException("Lead name must contain letters or digits.");
        CategoryId = p.CategoryId;
        Country = Guard.Optional(p.Country, 100, "Country");
        Province = Guard.Optional(p.Province, 100, "Province");
        City = Guard.Optional(p.City, 100, "City");
        District = Guard.Optional(p.District, 100, "District");
        Area = Guard.Optional(p.Area, 100, "Area");
        Address = Guard.Optional(p.Address, 500, "Address");
        Website = Guard.Optional(p.Website, 500, "Website");
        if (Website is not null && (WebsiteDomain = Normalizer.Domain(Website)) is null) throw new DomainException("Website is not a valid address (use e.g. https://example.com).");
        if (Website is null) WebsiteDomain = null;
        if (p.Confidence is < 0 or > 100) throw new DomainException("Confidence must be between 0 and 100.");
        Confidence = p.Confidence;
        DedupKey = Normalizer.DedupKey(Name, City);
    }

    public void MoveToStage(Guid pipelineId, Guid stageId, DateTime nowUtc, string? actor)
    {
        if (IsArchived) throw new DomainException("Archived leads cannot change stage. Restore the lead first.");
        PipelineId = pipelineId; PipelineStageId = stageId; Touch(nowUtc, actor);
    }

    public void SetStatus(Guid statusId, DateTime nowUtc, string? actor)
    {
        if (IsArchived) throw new DomainException("Archived leads cannot change status. Restore the lead first.");
        StatusId = statusId; Touch(nowUtc, actor);
    }

    public void AssignTo(Guid? ownerUserId, DateTime nowUtc, string? actor)
    {
        if (IsArchived) throw new DomainException("Archived leads cannot be reassigned. Restore the lead first.");
        OwnerUserId = ownerUserId; Touch(nowUtc, actor);
    }

    public void Archive(DateTime nowUtc, string? actor)
    {
        if (IsArchived) throw new DomainException("The lead is already archived.");
        IsArchived = true; ArchivedAtUtc = nowUtc; Touch(nowUtc, actor);
    }

    public void Restore(DateTime nowUtc, string? actor)
    {
        if (!IsArchived) throw new DomainException("The lead is not archived.");
        IsArchived = false; ArchivedAtUtc = null; Touch(nowUtc, actor);
    }

    public void ApplyScore(int score, int ruleVersion, DateTime nowUtc) { Score = Math.Clamp(score, 0, 100); ScoreRuleVersion = ruleVersion; ScoredAtUtc = nowUtc; }

    public void RecordActivity(DateTime nowUtc) => LastActivityAtUtc = nowUtc;

    private void Touch(DateTime nowUtc, string? actor) { UpdatedAtUtc = nowUtc; UpdatedBy = actor; LastActivityAtUtc = nowUtc; Version++; }
}

/// <summary>Where a lead (or a piece of its data) came from. (Type, ExternalId) is unique per tenant so connectors and imports are idempotent.</summary>
public class LeadSource : Entity, ITenantOwned
{
    private LeadSource() { }
    public Guid TenantId { get; private set; }
    public Guid LeadId { get; private set; }
    public LeadSourceType Type { get; private set; }
    public string? ExternalId { get; private set; }
    public string? Name { get; private set; }
    public string? Url { get; private set; }
    public string? Attribution { get; private set; }
    public DateTime RetrievedAtUtc { get; private set; }

    public static LeadSource Create(Guid tenantId, Guid leadId, LeadSourceType type, string? externalId, string? name, string? url, string? attribution, DateTime nowUtc) =>
        new()
        {
            TenantId = tenantId, LeadId = leadId, Type = type, ExternalId = Guard.Optional(externalId, 200, "Source reference"), Name = Guard.Optional(name, 200, "Source name"),
            Url = Guard.Optional(url, 500, "Source URL"), Attribution = Guard.Optional(attribution, 500, "Attribution"), RetrievedAtUtc = nowUtc
        };
}

internal static class Guard
{
    public static string Required(string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) throw new DomainException($"{label} is required.");
        if (v.Length > max) throw new DomainException($"{label} must be {max} characters or fewer.");
        return v;
    }

    public static string? Optional(string? value, int max, string label)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > max) throw new DomainException($"{label} must be {max} characters or fewer.");
        return v;
    }
}
