using System.Text.Json;
using LeadPilot.Application.Agents;
using LeadPilot.Application.Common;
using LeadPilot.Application.Messaging;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Domain.Workflows;
using LeadPilot.Infrastructure.Services.Crm;
using LeadPilot.Infrastructure.Services.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Workflows;

internal static class DefinitionMapper
{
    public static string? Raw(JsonElement? e) => e is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } v ? v.GetRawText() : null;
    public static JsonElement? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { using var d = JsonDocument.Parse(json); return d.RootElement.Clone(); } catch (JsonException) { return null; }
    }
    public static string TriggerConfig(TriggerDefinition t) => t.InactiveDays is { } d ? JsonSerializer.Serialize(new { inactiveDays = d }) : "{}";
}

internal sealed class WorkflowService(CrmContext crm, ILogger<WorkflowService> logger, IDemoScheduler demos, IMessageTemplateService templates) : IWorkflowService
{
    // ---------------------------------------------------------------- read

    public async Task<PagedResult<WorkflowListItemDto>> ListAsync(WorkflowQuery query, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var (page, size) = CrmFlow.Page(query.Page, query.PageSize);
        var q = db.Workflows.AsNoTracking().Where(w => w.TenantId == tenantId && !w.IsDeleted);
        if (query.Status is { } st) q = q.Where(w => w.Status == st);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var s = query.Search.Trim(); q = q.Where(w => w.Name.Contains(s)); }
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(w => w.UpdatedAtUtc ?? w.CreatedAtUtc).ThenBy(w => w.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        var ids = rows.Select(w => w.Id).ToList();
        var versions = await db.WorkflowVersions.AsNoTracking().Where(v => v.TenantId == tenantId && ids.Contains(v.WorkflowId)).Select(v => new { v.Id, v.WorkflowId, v.Number, v.State }).ToListAsync(ct);
        var versionIds = versions.Select(v => v.Id).ToList();
        var triggers = await db.WorkflowTriggers.AsNoTracking().Where(t => t.TenantId == tenantId && versionIds.Contains(t.WorkflowVersionId)).Select(t => new { t.WorkflowVersionId, t.EventType }).ToListAsync(ct);
        var running = await db.WorkflowExecutions.AsNoTracking().Where(e => e.TenantId == tenantId && ids.Contains(e.WorkflowId) && e.ActiveSlot != null).GroupBy(e => e.WorkflowId).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, ct);
        return new PagedResult<WorkflowListItemDto>(rows.Select(w =>
        {
            var vs = versions.Where(v => v.WorkflowId == w.Id).ToList();
            var shown = vs.FirstOrDefault(v => v.Id == w.ActiveVersionId) ?? vs.OrderByDescending(v => v.Number).FirstOrDefault();
            var titles = shown is null ? [] : triggers.Where(t => t.WorkflowVersionId == shown.Id).Select(t => TriggerCatalog.Find(t.EventType)?.Title ?? t.EventType).ToList();
            return new WorkflowListItemDto(w.Id, w.Name, w.Description, w.Status, w.IsKilled, vs.FirstOrDefault(v => v.Id == w.ActiveVersionId)?.Number, w.LatestVersionNumber, vs.Any(v => v.State == VersionState.Draft), titles, running.GetValueOrDefault(w.Id), w.UpdatedAtUtc ?? w.CreatedAtUtc);
        }).ToList(), page, size, total);
    }

    public async Task<Result<WorkflowDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var w = await LoadAsync(id, false, ct);
        return w is null ? Result.Failure<WorkflowDto>(Error.NotFound("Workflow")) : Result.Success(await MapAsync(w, ct));
    }

    public async Task<Result<WorkflowVersionDto>> GetVersionAsync(Guid workflowId, int number, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        if (await LoadAsync(workflowId, false, ct) is null) return Result.Failure<WorkflowVersionDto>(Error.NotFound("Workflow"));
        var v = await crm.Db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkflowId == workflowId && x.Number == number, ct);
        return v is null ? Result.Failure<WorkflowVersionDto>(Error.NotFound("Version")) : Result.Success(await MapVersionAsync(v, ct));
    }

    public async Task<WorkflowCatalogDto> CatalogAsync(CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var tpl = (await templates.ListAsync(true, ct)).Select(t => new CatalogOption(t.Id.ToString(), t.Name, t.Body)).ToList();
        var accounts = (await db.ChannelAccounts.AsNoTracking().Where(a => a.TenantId == tenantId && !a.IsDeleted).OrderBy(a => a.DisplayName).Select(a => new { a.Id, a.DisplayName }).ToListAsync(ct)).Select(a => new CatalogOption(a.Id.ToString(), a.DisplayName)).ToList();
        var users = await (from m in db.UserTenants.AsNoTracking() join u in db.Users.AsNoTracking() on m.UserId equals u.Id where m.TenantId == tenantId && m.Status == MembershipStatus.Active && u.IsActive orderby u.DisplayName select new { u.Id, u.DisplayName }).ToListAsync(ct);
        var statuses = await db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive && !s.IsClosed).OrderBy(s => s.SortOrder).Select(s => s.Name).ToListAsync(ct);
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId).OrderBy(s => s.Position).Select(s => s.Name).Distinct().ToListAsync(ct);
        var ops = Enum.GetValues<FieldKind>().ToDictionary(k => k.ToString(), k => ConditionFields.OperatorsFor(k));
        var demo = (await demos.AvailabilityAsync(crm.Now, crm.Now.AddDays(1), ct)).Available;
        return new WorkflowCatalogDto(TriggerCatalog.All, StepCatalog.All, ConditionFields.All, ops, tpl, accounts, users.Select(u => new CatalogOption(u.Id.ToString(), u.DisplayName)).ToList(),
            statuses.Select(s => new CatalogOption(s, s)).ToList(), stages.Select(s => new CatalogOption(s, s)).ToList(), TemplateRenderer.KnownVariables, WorkflowDefinitionValidator.LeadTextFields, demo);
    }

    // ---------------------------------------------------------------- write

    public Task<Result<WorkflowDto>> CreateAsync(CreateWorkflowRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            if (r.Definition is null) return Result.Failure<WorkflowDto>(Error.Validation("A workflow needs triggers and steps."));
            if (string.IsNullOrWhiteSpace(r.Name)) return Result.Failure<WorkflowDto>(Error.Validation("Workflow name is required."));
            if (await db.Workflows.CountAsync(w => w.TenantId == tenantId && !w.IsDeleted, ct) >= 200) return Result.Failure<WorkflowDto>(Error.Validation("A workspace can have at most 200 workflows. Delete workflows you no longer need."));
            var bad = await CheckDefinitionAsync(r.Definition, ct);
            if (bad is not null) return Result.Failure<WorkflowDto>(bad);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var w = Workflow.Create(tenantId, r.Name, r.Description, now, crm.Actor);
            db.Workflows.Add(w);
            await db.SaveChangesAsync(ct);
            await AddVersionAsync(w, r.Definition, ct);
            crm.Record("Workflow.Created", "Workflow", w.Id, $"Workflow '{w.Name}' created.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success(await MapAsync(w, ct));
        }, logger);

    public Task<Result<WorkflowDto>> UpdateAsync(Guid id, UpdateWorkflowRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var w = await LoadAsync(id, true, ct); if (w is null) return Result.Failure<WorkflowDto>(Error.NotFound("Workflow"));
            if (r.ExpectedVersion is { } v && v != w.Version) return Result.Failure<WorkflowDto>(Error.Conflict("The workflow was changed by someone else. Reload it and try again."));
            w.Describe(r.Name, r.Description, crm.Now, crm.Actor);
            crm.Record("Workflow.Updated", "Workflow", id, $"Workflow '{w.Name}' renamed or described.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(w, ct));
        }, logger);

    public Task<Result<WorkflowVersionDto>> SaveDraftAsync(Guid workflowId, SaveDraftRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure<WorkflowVersionDto>(Error.NotFound("Workflow"));
            if (r.Definition is null) return Result.Failure<WorkflowVersionDto>(Error.Validation("A workflow needs triggers and steps."));
            var bad = await CheckDefinitionAsync(r.Definition, ct);
            if (bad is not null) return Result.Failure<WorkflowVersionDto>(bad);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var draft = await db.WorkflowVersions.FirstOrDefaultAsync(v => v.TenantId == tenantId && v.WorkflowId == workflowId && v.State == VersionState.Draft, ct);
            if (draft is null) draft = await AddVersionAsync(w, r.Definition, ct);
            else
            {
                draft.Configure(r.Definition.ReEntry, r.Definition.CooldownHours, r.Definition.MaxStartsPerLeadPer30Days, r.Definition.Note);   // throws for a non-draft: immutability
                db.WorkflowTriggers.RemoveRange(await db.WorkflowTriggers.Where(t => t.WorkflowVersionId == draft.Id && t.TenantId == tenantId).ToListAsync(ct));
                db.WorkflowSteps.RemoveRange(await db.WorkflowSteps.Where(s => s.WorkflowVersionId == draft.Id && s.TenantId == tenantId).ToListAsync(ct));
                await db.SaveChangesAsync(ct);   // the unique (version, key) indexes need the old rows gone first
                AddRows(draft, r.Definition);
            }
            crm.Record("Workflow.DraftSaved", "WorkflowVersion", draft.Id, $"Draft v{draft.Number} of '{w.Name}' saved.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success(await MapVersionAsync(draft, ct));
        }, logger);

    public Task<Result<WorkflowVersionDto>> NewDraftFromAsync(Guid workflowId, int number, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure<WorkflowVersionDto>(Error.NotFound("Workflow"));
            var src = await db.WorkflowVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.WorkflowId == workflowId && v.Number == number, ct);
            if (src is null) return Result.Failure<WorkflowVersionDto>(Error.NotFound("Version"));
            if (await db.WorkflowVersions.AnyAsync(v => v.TenantId == tenantId && v.WorkflowId == workflowId && v.State == VersionState.Draft, ct)) return Result.Failure<WorkflowVersionDto>(Error.Conflict("This workflow already has a draft. Edit or activate it first."));
            var def = await LoadDefinitionAsync(src.Id, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var draft = await AddVersionAsync(w, def, ct);
            crm.Record("Workflow.DraftCreated", "WorkflowVersion", draft.Id, $"Draft v{draft.Number} of '{w.Name}' created from v{src.Number}.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success(await MapVersionAsync(draft, ct));
        }, logger);

    public Task<Result<ValidationReport>> ValidateAsync(WorkflowDefinition definition, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            if (definition is null) return Result.Failure<ValidationReport>(Error.Validation("A workflow needs triggers and steps."));
            var report = WorkflowDefinitionValidator.Validate(definition);
            var refs = report.IsValid ? await CheckReferencesAsync(definition, ct) : [];
            return Result.Success(new ValidationReport([.. report.Errors, .. refs], report.Warnings));
        }, logger);

    public Task<Result<WorkflowDto>> ActivateAsync(Guid workflowId, int versionNumber, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure<WorkflowDto>(Error.NotFound("Workflow"));
            var v = await db.WorkflowVersions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.WorkflowId == workflowId && x.Number == versionNumber, ct);
            if (v is null) return Result.Failure<WorkflowDto>(Error.NotFound("Version"));
            if (v.State != VersionState.Draft) return Result.Failure<WorkflowDto>(Error.Conflict(v.State == VersionState.Active ? "That version is already active." : "A retired version cannot be activated again. Create a new draft from it."));

            var def = await LoadDefinitionAsync(v.Id, ct);
            var bad = await CheckDefinitionAsync(def, ct);
            if (bad is not null) return Result.Failure<WorkflowDto>(bad);
            if (def.Steps.Any(s => s.Type == StepType.ScheduleDemo) && !(await demos.AvailabilityAsync(now, now.AddDays(1), ct)).Available)
                return Result.Failure<WorkflowDto>(Error.Validation("This workflow contains a 'Schedule demo' step, but no demo slot can be read right now. Try again."));

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (w.ActiveVersionId is { } prev && await db.WorkflowVersions.FirstOrDefaultAsync(x => x.Id == prev && x.TenantId == tenantId, ct) is { State: VersionState.Active } old) old.Retire(now);
            foreach (var t in await db.WorkflowTriggers.Where(t => t.WorkflowVersionId == v.Id && t.TenantId == tenantId).ToListAsync(ct)) t.Seal();
            foreach (var s in await db.WorkflowSteps.Where(s => s.WorkflowVersionId == v.Id && s.TenantId == tenantId).ToListAsync(ct)) s.Seal();
            v.Activate(now);
            w.Activate(v.Id, now, crm.Actor);
            crm.Record("Workflow.Activated", "Workflow", w.Id, $"Workflow '{w.Name}' activated with version {v.Number}.");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Result.Success(await MapAsync(w, ct));
        }, logger);

    public Task<Result<WorkflowDto>> DeactivateAsync(Guid workflowId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure<WorkflowDto>(Error.NotFound("Workflow"));
            if (w.Status != WorkflowStatus.Active) return Result.Failure<WorkflowDto>(Error.Conflict("The workflow is not active."));
            w.Deactivate(crm.Now, crm.Actor);
            crm.Record("Workflow.Deactivated", "Workflow", w.Id, $"Workflow '{w.Name}' deactivated: nothing new starts; runs in progress finish (use cancel or the kill switch to stop them).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(w, ct));
        }, logger);

    public Task<Result<WorkflowDto>> SetKillSwitchAsync(Guid workflowId, bool on, string? reason, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure<WorkflowDto>(Error.NotFound("Workflow"));
            if (on) w.Kill(reason, crm.Now, crm.Actor); else w.Unkill(crm.Now, crm.Actor);
            crm.Record(on ? "Workflow.Killed" : "Workflow.Unkilled", "Workflow", w.Id, on ? $"Kill switch ON for '{w.Name}'." : $"Kill switch OFF for '{w.Name}'.");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(await MapAsync(w, ct));
        }, logger);

    public Task<Result> DeleteAsync(Guid workflowId, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var tenantId = crm.TenantId;
            var w = await LoadAsync(workflowId, true, ct); if (w is null) return Result.Failure(Error.NotFound("Workflow"));
            if (await crm.Db.WorkflowExecutions.AnyAsync(e => e.TenantId == tenantId && e.WorkflowId == workflowId && e.ActiveSlot != null, ct)) return Result.Failure(Error.Conflict("Runs of this workflow are still in progress. Cancel them first."));
            w.Delete(crm.Now, crm.Actor);
            crm.Record("Workflow.Deleted", "Workflow", w.Id, $"Workflow '{w.Name}' deleted (its run history stays).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success();
        }, logger);

    // ---------------------------------------------------------------- internals

    private async Task<Workflow?> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var tenantId = crm.TenantId;
        var q = track ? crm.Db.Workflows : crm.Db.Workflows.AsNoTracking();
        return await q.FirstOrDefaultAsync(w => w.Id == id && w.TenantId == tenantId && !w.IsDeleted, ct);
    }

    private async Task<WorkflowVersion> AddVersionAsync(Workflow w, WorkflowDefinition def, CancellationToken ct)
    {
        var v = WorkflowVersion.Create(crm.TenantId, w.Id, w.NextVersionNumber(), def.ReEntry, def.CooldownHours, def.MaxStartsPerLeadPer30Days, def.Note, crm.Now, crm.Actor);
        crm.Db.WorkflowVersions.Add(v);
        AddRows(v, def);
        await crm.Db.SaveChangesAsync(ct);
        return v;
    }

    private void AddRows(WorkflowVersion v, WorkflowDefinition def)
    {
        foreach (var t in def.Triggers) crm.Db.WorkflowTriggers.Add(WorkflowTrigger.Create(crm.TenantId, v.Id, t.EventType, DefinitionMapper.Raw(t.Condition), DefinitionMapper.TriggerConfig(t)));
        var pos = 0;
        foreach (var s in def.Steps) crm.Db.WorkflowSteps.Add(WorkflowStep.Create(crm.TenantId, v.Id, s.Key, ++pos, s.Type, s.Name.Trim(), DefinitionMapper.Raw(s.Config) ?? "{}", DefinitionMapper.Raw(s.Condition), s.OnFailure, s.MaxRetries));
    }

    /// <summary>Pure rules, then references against the database. Returns the failure to report, or null.</summary>
    private async Task<Error?> CheckDefinitionAsync(WorkflowDefinition def, CancellationToken ct)
    {
        var report = WorkflowDefinitionValidator.Validate(def);
        if (report.IsValid) report = new ValidationReport(await CheckReferencesAsync(def, ct), report.Warnings);
        return report.IsValid ? null : Error.Validation(report.Summary);
    }

    private async Task<List<ValidationProblem>> CheckReferencesAsync(WorkflowDefinition def, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var errors = new List<ValidationProblem>();
        for (var i = 0; i < def.Steps.Count; i++)
        {
            var s = def.Steps[i]; var path = $"steps[{i}].config"; var cfg = WorkflowDefinitionValidator.ConfigOf(s);
            string? Str(string k) => cfg[k] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var x) ? x?.Trim() : null;
            Guid? Id(string k) => Guid.TryParse(Str(k), out var g) ? g : null;
            switch (s.Type)
            {
                case StepType.SendMessage:
                    if (Id("templateId") is { } t && !await db.MessageTemplates.AnyAsync(x => x.Id == t && x.TenantId == tenantId && x.IsActive, ct)) errors.Add(new(path, "The message template does not exist or is switched off."));
                    if (Id("channelAccountId") is { } a && !await db.ChannelAccounts.AnyAsync(x => x.Id == a && x.TenantId == tenantId && !x.IsDeleted, ct)) errors.Add(new(path, "The WhatsApp account does not exist."));
                    break;
                case StepType.AssignLead:
                    if (Id("userId") is { } u && !await crm.IsActiveMemberAsync(u, ct)) errors.Add(new(path, "The person is not an active member of this workspace."));
                    break;
                case StepType.NotifyUser:
                    if (Str("target") != "owner" && Id("target") is { } n && !await crm.IsActiveMemberAsync(n, ct)) errors.Add(new(path, "The person is not an active member of this workspace."));
                    break;
                case StepType.ChangeLeadStatus:
                    var statusName = Str("status");
                    var st = await db.LeadStatuses.AsNoTracking().FirstOrDefaultAsync(x => x.TenantId == tenantId && x.IsActive && x.Name == statusName, ct);
                    if (st is null) errors.Add(new(path, $"There is no status '{Str("status")}'."));
                    else if (st.IsClosed) errors.Add(new(path, "Closing or disqualifying a lead is a decision for a person; a workflow cannot do it."));
                    break;
                case StepType.ChangeLeadStage:
                    var stageName = Str("stage");
                    if (!await db.PipelineStages.AnyAsync(x => x.TenantId == tenantId && x.Name == stageName, ct)) errors.Add(new(path, $"There is no stage '{Str("stage")}'."));
                    break;
            }
        }
        return errors;
    }

    private async Task<WorkflowDefinition> LoadDefinitionAsync(Guid versionId, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var v = await db.WorkflowVersions.AsNoTracking().FirstAsync(x => x.Id == versionId && x.TenantId == tenantId, ct);
        var triggers = await db.WorkflowTriggers.AsNoTracking().Where(t => t.WorkflowVersionId == versionId && t.TenantId == tenantId).ToListAsync(ct);
        var steps = await db.WorkflowSteps.AsNoTracking().Where(s => s.WorkflowVersionId == versionId && s.TenantId == tenantId).OrderBy(s => s.Position).ToListAsync(ct);
        return new WorkflowDefinition(
            triggers.Select(t => new TriggerDefinition(t.EventType, DefinitionMapper.Parse(t.ConditionJson), InactiveDays(t.ConfigJson))).ToList(),
            steps.Select(s => new StepDefinition(s.Key, s.Type, s.Name, DefinitionMapper.Parse(s.ConfigJson), DefinitionMapper.Parse(s.ConditionJson), s.OnFailure, s.MaxRetries)).ToList(),
            v.ReEntry, v.CooldownHours, v.MaxStartsPerLeadPer30Days, v.Note);
    }

    private static int? InactiveDays(string? config)
    {
        try { return config is not null && JsonDocument.Parse(config).RootElement.TryGetProperty("inactiveDays", out var d) && d.TryGetInt32(out var n) ? n : null; } catch (JsonException) { return null; }
    }

    private async Task<WorkflowVersionDto> MapVersionAsync(WorkflowVersion v, CancellationToken ct)
    {
        var def = await LoadDefinitionAsync(v.Id, ct);
        var report = WorkflowDefinitionValidator.Validate(def);
        if (report.IsValid && v.State == VersionState.Draft) report = new ValidationReport(await CheckReferencesAsync(def, ct), report.Warnings);
        var triggers = def.Triggers.Select(t => new TriggerDto(t.EventType, TriggerCatalog.Find(t.EventType)?.Title ?? t.EventType, TriggerCatalog.Find(t.EventType)?.Available ?? false, t.Condition, t.InactiveDays)).ToList();
        var steps = def.Steps.Select((s, i) => new StepDto(s.Key, i + 1, s.Type, StepCatalog.Find(s.Type)?.Title ?? s.Type.ToString(), s.Name, s.Config, s.Condition, s.OnFailure, s.MaxRetries)).ToList();
        return new WorkflowVersionDto(v.Id, v.WorkflowId, v.Number, v.State, v.ReEntry, v.CooldownHours, v.MaxStartsPerLeadPer30Days, v.Note, v.CreatedAtUtc, v.ActivatedAtUtc, triggers, steps, report);
    }

    private async Task<WorkflowDto> MapAsync(Workflow w, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var versions = await db.WorkflowVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.WorkflowId == w.Id).OrderByDescending(v => v.Number).ToListAsync(ct);
        var stepCounts = await db.WorkflowSteps.AsNoTracking().Where(s => s.TenantId == tenantId && versions.Select(v => v.Id).Contains(s.WorkflowVersionId)).GroupBy(s => s.WorkflowVersionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var execCounts = await db.WorkflowExecutions.AsNoTracking().Where(e => e.TenantId == tenantId && e.WorkflowId == w.Id).GroupBy(e => e.WorkflowVersionId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var summaries = versions.Select(v => new WorkflowVersionSummaryDto(v.Id, v.Number, v.State, v.Note, v.CreatedAtUtc, v.CreatedBy, v.ActivatedAtUtc, v.RetiredAtUtc, stepCounts.GetValueOrDefault(v.Id), execCounts.GetValueOrDefault(v.Id))).ToList();
        var draft = versions.FirstOrDefault(v => v.State == VersionState.Draft);
        var active = versions.FirstOrDefault(v => v.Id == w.ActiveVersionId);
        return new WorkflowDto(w.Id, w.Name, w.Description, w.Status, w.IsKilled, w.KillReason, w.KilledAtUtc, w.ActiveVersionId, active?.Number, w.LatestVersionNumber, w.Version, w.CreatedAtUtc, w.UpdatedAtUtc,
            summaries, draft is null ? null : await MapVersionAsync(draft, ct), active is null ? null : await MapVersionAsync(active, ct));
    }
}
