using System.Text.Json;
using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using LeadPilot.Domain.Tenancy;
using LeadPilot.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LeadPilot.Infrastructure.Services.Crm;

public sealed class LeadImportService(CrmContext crm, DuplicateDetector duplicates, ILeadScorer scorer, ITabularFileReader reader, ITenantFileStore files, IOptions<CrmOptions> options, ILogger<LeadImportService> logger) : ILeadImportService
{
    private const int FlushEvery = 50;

    public async Task<PagedResult<ImportJobDto>> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId; var (p, size) = CrmFlow.Page(page, pageSize);
        var q = crm.Db.ImportJobs.AsNoTracking().Where(j => j.TenantId == tenantId);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderByDescending(j => j.CreatedAtUtc).ThenBy(j => j.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return new PagedResult<ImportJobDto>(rows.Select(ToDto).ToList(), p, size, total);
    }

    public async Task<Result<ImportJobDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var job = await crm.Db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
        return job is null ? Result.Failure<ImportJobDto>(Error.NotFound("Import")) : Result.Success(ToDto(job));
    }

    private static ImportJobDto ToDto(ImportJob j) => new(j.Id, j.FileName, j.FileType, j.FileSizeBytes, j.Status, j.DuplicatePolicy, j.TotalRows, j.CreatedCount, j.MergedCount,
        j.SkippedDuplicateCount, j.PendingDuplicateCount, j.ErrorCount, j.FailureReason, j.CreatedAtUtc, j.CreatedBy, j.CompletedAtUtc);

    // ---------- upload ----------
    public Task<Result<ImportPreviewDto>> UploadAsync(UploadedFile file, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var o = options.Value; var tenantId = crm.TenantId;
            var name = Path.GetFileName(file.FileName ?? string.Empty);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            var type = ext switch { ".csv" => ImportFileType.Csv, ".xlsx" => ImportFileType.Xlsx, _ => (ImportFileType?)null };
            if (type is null) return Result.Failure<ImportPreviewDto>(Error.Validation("Only .csv and .xlsx files can be imported. (Older .xls files must be saved as .xlsx or CSV.)"));
            if (file.Length <= 0) return Result.Failure<ImportPreviewDto>(Error.Validation("The file is empty."));
            if (file.Length > o.MaxImportFileBytes) return Result.Failure<ImportPreviewDto>(Error.TooLarge($"The file is larger than the {o.MaxImportFileBytes / (1024 * 1024)} MB limit."));

            var ms = new MemoryStream();
            var buffer = new byte[81920]; int n;
            while ((n = await file.Content.ReadAsync(buffer, ct)) > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, n), ct);
                if (ms.Length > o.MaxImportFileBytes) return Result.Failure<ImportPreviewDto>(Error.TooLarge($"The file is larger than the {o.MaxImportFileBytes / (1024 * 1024)} MB limit."));
            }
            ms.Position = 0;
            var isZip = TabularFileReader.LooksLikeZip(ms.GetBuffer().AsSpan(0, (int)Math.Min(ms.Length, 4)));
            if (type == ImportFileType.Xlsx && !isZip) return Result.Failure<ImportPreviewDto>(Error.Validation("The file is not a real .xlsx workbook."));
            if (type == ImportFileType.Csv && isZip) return Result.Failure<ImportPreviewDto>(Error.Validation("The file is a workbook, not CSV text. Rename it to .xlsx or export it as CSV."));

            List<string[]> rows;
            try { rows = reader.ReadRows(ms, type.Value).Take(o.MaxImportRows + 2).ToList(); }
            catch (SpreadsheetFormatException ex) { return Result.Failure<ImportPreviewDto>(Error.Validation(ex.Message)); }
            if (rows.Count == 0) return Result.Failure<ImportPreviewDto>(Error.Validation("The file has no rows."));
            if (rows.Count == 1) return Result.Failure<ImportPreviewDto>(Error.Validation("The file has a header row but no data rows."));
            if (rows.Count - 1 > o.MaxImportRows) return Result.Failure<ImportPreviewDto>(Error.Validation($"The file has more than {o.MaxImportRows} data rows. Split it into smaller files."));

            ms.Position = 0;
            var key = await files.SaveAsync("imports", name, ms, ct);
            var job = ImportJob.Create(tenantId, name, type.Value, ms.Length, key, rows.Count - 1, crm.Now, crm.Actor);
            crm.Db.ImportJobs.Add(job);
            crm.Record("Import.Uploaded", "ImportJob", job.Id, $"File '{job.FileName}' uploaded for import ({job.TotalRows} rows).");
            await crm.Db.SaveChangesAsync(ct);
            return Result.Success(BuildPreview(job, rows));
        }, logger);

    public async Task<Result<ImportPreviewDto>> PreviewAsync(Guid id, CancellationToken ct = default)
    {
        var tenantId = crm.TenantId;
        var job = await crm.Db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
        if (job is null) return Result.Failure<ImportPreviewDto>(Error.NotFound("Import"));
        if (job.Status != ImportStatus.Uploaded) return Result.Failure<ImportPreviewDto>(Error.Conflict("This import has already been run."));
        try
        {
            var rows = (await ReadAll(job, ct)).Take(7).ToList();
            return Result.Success(BuildPreview(job, rows));
        }
        catch (SpreadsheetFormatException ex) { return Result.Failure<ImportPreviewDto>(Error.Validation(ex.Message)); }
    }

    private async Task<IEnumerable<string[]>> ReadAll(ImportJob job, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(job.StoredFileKey!, ct);
        var ms = new MemoryStream(); await stream.CopyToAsync(ms, ct); ms.Position = 0;
        return reader.ReadRows(ms, job.FileType).ToList();
    }

    private ImportPreviewDto BuildPreview(ImportJob job, List<string[]> rows)
    {
        var headers = rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
        var guess = ImportRowMapper.Guess(headers);
        var columns = headers.Select((h, i) => new ImportColumnDto(i, h, guess[i].Field)).ToList();
        var sample = rows.Skip(1).Take(5).Select(r => (IReadOnlyList<string>)Enumerable.Range(0, headers.Count).Select(i => i < r.Length ? r[i] : "").ToList()).ToList();
        return new ImportPreviewDto(ToDto(job), columns, sample);
    }

    // ---------- run ----------
    private sealed class RunState
    {
        public Dictionary<string, Guid> Categories = new();
        public Dictionary<string, Guid> Tags = new();
        public Dictionary<string, Guid> Owners = new();
        public Dictionary<string, PipelineStage> Stages = new();
        public PipelineStage FirstStage = null!;
        public Guid StatusId;
        public Guid PipelineId;
    }

    public Task<Result<ImportJobDto>> RunAsync(Guid id, RunImportRequest r, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            await crm.EnsureDefaultsAsync(ct);
            var db = crm.Db; var tenantId = crm.TenantId;
            var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
            if (job is null) return Result.Failure<ImportJobDto>(Error.NotFound("Import"));
            if (job.Status != ImportStatus.Uploaded) return Result.Failure<ImportJobDto>(Error.Conflict("This import has already been run."));
            if (!Enum.IsDefined(r.DuplicatePolicy)) return Result.Failure<ImportJobDto>(Error.Validation("Choose how duplicates are handled."));
            var mapping = (r.Mapping ?? []).ToList();

            List<string[]> rows;
            try { rows = (await ReadAll(job, ct)).ToList(); }
            catch (SpreadsheetFormatException ex) { return Result.Failure<ImportJobDto>(Error.Validation(ex.Message)); }
            var headers = rows[0].Select((h, i) => string.IsNullOrWhiteSpace(h) ? $"Column {i + 1}" : h.Trim()).ToList();
            if (ImportRowMapper.ValidateMapping(mapping, headers.Count) is { } mapError) return Result.Failure<ImportJobDto>(Error.Validation(mapError));

            // owner assignment needs the Assign permission, same as everywhere else
            var assigns = r.DefaultOwnerUserId is not null || mapping.Any(m => m.Field == ImportField.OwnerEmail);
            if (assigns && !await crm.Permissions.HasAsync(PermissionCodes.LeadsAssign, ct)) return Result.Failure<ImportJobDto>(Error.Forbidden("Assigning owners during import requires the Assign leads permission."));
            if (r.DefaultOwnerUserId is { } defOwner && !await crm.IsActiveMemberAsync(defOwner, ct)) return Result.Failure<ImportJobDto>(Error.Validation("The default owner must be an active member of this workspace."));
            if (r.DefaultCategoryId is { } defCat && !await db.LeadCategories.AnyAsync(c => c.Id == defCat && c.TenantId == tenantId && c.IsActive, ct)) return Result.Failure<ImportJobDto>(Error.Validation("Default category not found."));

            var state = await LoadState(ct);
            job.Start(r.DuplicatePolicy, JsonSerializer.Serialize(mapping.Where(m => m.Field is not null)), crm.Now);
            await db.SaveChangesAsync(ct);

            int created = 0, merged = 0, skipped = 0, pending = 0, errors = 0;
            var touched = new List<Guid>();
            var country = await crm.DefaultCountryCodeAsync(ct);
            var sourceName = string.IsNullOrWhiteSpace(r.SourceName) ? job.FileName : r.SourceName.Trim();
            var unsaved = 0;
            var pendingKeys = new HashSet<string>();

            async Task FlushAsync()
            {
                if (unsaved == 0 && !db.ChangeTracker.HasChanges()) return;
                await db.SaveChangesAsync(ct);
                foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is not ImportJob).ToList()) e.State = EntityState.Detached;
                unsaved = 0; pendingKeys.Clear();
            }

            try
            {
                for (var i = 1; i < rows.Count; i++)
                {
                    var rowNumber = i + 1; // spreadsheet row number (header is row 1)
                    var mapped = ImportRowMapper.Map(rowNumber, rows[i], headers, mapping, country);
                    if (mapped.Data is null)
                    {
                        foreach (var issue in mapped.Issues) { db.ImportRowErrors.Add(ImportRowError.Validation(tenantId, id, rowNumber, issue.Column, issue.Message, JsonSerializer.Serialize(rows[i]))); errors++; unsaved++; }
                        continue;
                    }
                    var data = mapped.Data;
                    var resolveError = ResolveReferences(data, r, state, out var categoryId, out var ownerId, out var stage);
                    if (resolveError is not null)
                    {
                        if (resolveError.Value.Create is { } newCategory)
                        {
                            var cat = LeadCategory.Create(tenantId, newCategory, crm.Now);
                            if (state.Categories.TryAdd(cat.NormalizedName, cat.Id)) { db.LeadCategories.Add(cat); unsaved++; }
                            ResolveReferences(data, r, state, out categoryId, out ownerId, out stage);
                        }
                        else { db.ImportRowErrors.Add(ImportRowError.Validation(tenantId, id, rowNumber, resolveError.Value.Column, resolveError.Value.Message, JsonSerializer.Serialize(rows[i]))); errors++; unsaved++; continue; }
                    }

                    // duplicates: flush first when an earlier unsaved row of this file shares a key, so the database check sees it
                    var keys = KeysOf(data);
                    if (keys.Any(pendingKeys.Contains)) await FlushAsync();
                    var found = await duplicates.FindAsync(new DuplicateProbe(data.Name, data.City, data.Website, data.PhoneE164 is null ? [] : [data.PhoneE164]), ct);
                    try
                    {
                        if (found.Count > 0)
                        {
                            var first = found[0];
                            var message = $"Possible duplicate of '{first.LeadName}' ({string.Join(", ", first.Reasons)}).";
                            switch (r.DuplicatePolicy)
                            {
                                case DuplicatePolicy.Skip:
                                    db.ImportRowErrors.Add(ImportRowError.Duplicate(tenantId, id, rowNumber, message + " Skipped.", data.ToJson(), first.LeadId, DuplicateResolution.Skipped)); skipped++; unsaved++; break;
                                case DuplicatePolicy.Review:
                                    db.ImportRowErrors.Add(ImportRowError.Duplicate(tenantId, id, rowNumber, message + " Awaiting your decision.", data.ToJson(), first.LeadId, DuplicateResolution.Pending)); pending++; unsaved++; break;
                                default:
                                    await FlushAsync();
                                    if (first.IsArchived) { db.ImportRowErrors.Add(ImportRowError.Duplicate(tenantId, id, rowNumber, message + " The existing lead is archived, so nothing was merged.", data.ToJson(), first.LeadId, DuplicateResolution.Skipped)); skipped++; unsaved++; break; }
                                    if (await MergeAsync(first.LeadId, data, job, rowNumber, stage, ct)) { merged++; touched.Add(first.LeadId); unsaved++; }
                                    else { db.ImportRowErrors.Add(ImportRowError.Duplicate(tenantId, id, rowNumber, message + " Nothing new to merge.", data.ToJson(), first.LeadId, DuplicateResolution.Skipped)); skipped++; unsaved++; }
                                    break;
                            }
                        }
                        else
                        {
                            var leadId = await CreateLeadAsync(data, job, rowNumber, sourceName, categoryId ?? r.DefaultCategoryId, ownerId ?? r.DefaultOwnerUserId, stage ?? state.FirstStage, state, ct);
                            touched.Add(leadId); created++; unsaved++;
                            foreach (var k in keys) pendingKeys.Add(k);
                        }
                    }
                    catch (DomainException ex)
                    {
                        db.ImportRowErrors.Add(ImportRowError.Validation(tenantId, id, rowNumber, null, ex.Message, JsonSerializer.Serialize(rows[i]))); errors++; unsaved++;
                    }
                    if (unsaved >= FlushEvery) await FlushAsync();
                }
                await FlushAsync();
                await scorer.RescoreAsync(touched, ct);
                job.Complete(created, merged, skipped, pending, errors, crm.Now);
                crm.Record("Import.Completed", "ImportJob", id, $"Import '{job.FileName}': {created} created, {merged} merged, {skipped} skipped, {pending} awaiting review, {errors} error(s).");
                await db.SaveChangesAsync(ct);
                return Result.Success(ToDto(job));
            }
            catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
            {
                logger.LogError(ex, "Import {ImportId} failed", id);
                db.ChangeTracker.Clear();
                var failed = await db.ImportJobs.FirstAsync(j => j.Id == id && j.TenantId == tenantId, ct);
                failed.Fail("The database rejected a batch; rows saved before that point were kept.", crm.Now);
                crm.Record("Import.Failed", "ImportJob", id, $"Import '{failed.FileName}' failed after {created} created lead(s).");
                await db.SaveChangesAsync(ct);
                return Result.Failure<ImportJobDto>(Error.Validation("The import stopped because the database rejected a batch. Rows saved before that point were kept; see the import history."));
            }
        }, logger);

    private static List<string> KeysOf(ImportRowData d)
    {
        var keys = new List<string> { "k:" + Normalizer.DedupKey(d.Name, d.City) };
        if (Normalizer.Domain(d.Website) is { } dom) keys.Add("d:" + dom);
        if (d.PhoneE164 is not null) keys.Add("p:" + d.PhoneE164);
        return keys;
    }

    private async Task<RunState> LoadState(CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var st = new RunState();
        st.Categories = await db.LeadCategories.AsNoTracking().Where(c => c.TenantId == tenantId && c.IsActive).ToDictionaryAsync(c => c.NormalizedName, c => c.Id, ct);
        st.Tags = await db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.NormalizedName, t => t.Id, ct);
        var members = await (from m in db.UserTenants.AsNoTracking() join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.TenantId == tenantId && m.Status == MembershipStatus.Active && u.IsActive && u.Email != null select new { u.Id, u.Email }).ToListAsync(ct);
        st.Owners = members.GroupBy(m => m.Email!.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First().Id);
        var pipeline = await db.Pipelines.AsNoTracking().Where(p => p.TenantId == tenantId && p.IsDefault && p.IsActive).FirstAsync(ct);
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.TenantId == tenantId && s.PipelineId == pipeline.Id).OrderBy(s => s.Position).ToListAsync(ct);
        st.PipelineId = pipeline.Id; st.FirstStage = stages.First();
        st.Stages = stages.GroupBy(s => s.Name.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
        st.StatusId = (await db.LeadStatuses.AsNoTracking().Where(s => s.TenantId == tenantId && s.IsActive).OrderByDescending(s => s.IsDefault).ThenBy(s => s.SortOrder).FirstAsync(ct)).Id;
        return st;
    }

    private static (string? Column, string Message, string? Create)? ResolveReferences(ImportRowData d, RunImportRequest r, RunState st, out Guid? categoryId, out Guid? ownerId, out PipelineStage? stage)
    {
        categoryId = null; ownerId = null; stage = null;
        if (d.Category is { } cat)
        {
            if (st.Categories.TryGetValue(Normalizer.Name(cat), out var cid)) categoryId = cid;
            else if (r.CreateMissingCategories) return ("Category", "", cat);
            else return ("Category", $"Unknown category '{Trunc(cat)}'. Create it first, or tick \"create missing categories\".", null);
        }
        if (d.OwnerEmail is { } email)
        {
            if (st.Owners.TryGetValue(email, out var oid)) ownerId = oid;
            else return ("Owner", $"'{Trunc(email)}' is not an active member of this workspace.", null);
        }
        if (d.Stage is { } sn)
        {
            if (st.Stages.TryGetValue(sn.ToLowerInvariant(), out var s)) stage = s;
            else return ("Stage", $"Unknown stage '{Trunc(sn)}' in the default pipeline.", null);
        }
        return null;
    }

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "…";

    private async Task<Guid> CreateLeadAsync(ImportRowData d, ImportJob job, int row, string sourceName, Guid? categoryId, Guid? ownerId, PipelineStage stage, RunState st, CancellationToken ct)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var lead = Lead.Create(tenantId, new LeadProfile(d.Name, categoryId, d.Country, d.Province, d.City, d.District, d.Area, d.Address, d.Website, 50), st.PipelineId, stage.Id, st.StatusId, ownerId, LeadSourceType.Import, now, crm.Actor);
        db.Leads.Add(lead);
        db.LeadSources.Add(LeadSource.Create(tenantId, lead.Id, LeadSourceType.Import, $"{job.Id:N}:{row}", sourceName, null, $"Imported from {job.FileName}, row {row}", now));
        AddContact(lead.Id, d, primary: true);
        await AddTagsAsync(lead.Id, d, existing: [], st);
        db.LeadActivities.Add(LeadActivity.Create(tenantId, lead.Id, LeadActivityType.Imported, $"Imported from '{job.FileName}', row {row}.", null, crm.UserId, now));
        return lead.Id;
    }

    private void AddContact(Guid leadId, ImportRowData d, bool primary)
    {
        if (d.ContactName is null && d.PhoneE164 is null && d.Email is null) return;
        crm.Db.Contacts.Add(Contact.Create(crm.TenantId, leadId, d.ContactName, d.ContactRole, d.PhoneRaw, d.PhoneE164, d.Email, DataOrigin.Public, primary, crm.Now, crm.Actor));
    }

    private async Task AddTagsAsync(Guid leadId, ImportRowData d, HashSet<Guid> existing, RunState st)
    {
        foreach (var name in d.Tags)
        {
            var key = name.ToLowerInvariant();
            if (!st.Tags.TryGetValue(key, out var tagId))
            {
                var tag = LeadTag.Create(crm.TenantId, name, crm.Now);
                crm.Db.LeadTags.Add(tag); st.Tags[key] = tagId = tag.Id;
            }
            if (existing.Add(tagId)) crm.Db.LeadTagMaps.Add(LeadTagMap.Create(crm.TenantId, leadId, tagId, crm.Now));
        }
        await Task.CompletedTask;
    }

    /// <summary>Fills blank fields of an existing lead (never overwrites), adds a new contact and tags, and records the source. Returns false when there was nothing new.</summary>
    private async Task<bool> MergeAsync(Guid leadId, ImportRowData d, ImportJob job, int row, PipelineStage? stage, CancellationToken ct, RunState? state = null)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var now = crm.Now;
        var lead = await db.Leads.FirstOrDefaultAsync(l => l.Id == leadId && l.TenantId == tenantId, ct);
        if (lead is null || lead.IsArchived) return false;
        var changed = false;
        Guid? categoryId = lead.CategoryId;
        if (categoryId is null && d.Category is { } cat)
        {
            var cid = await db.LeadCategories.Where(c => c.TenantId == tenantId && c.IsActive && c.NormalizedName == Normalizer.Name(cat)).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
            categoryId = cid;
        }
        var profile = new LeadProfile(lead.Name, categoryId, lead.Country ?? d.Country, lead.Province ?? d.Province, lead.City ?? d.City, lead.District ?? d.District, lead.Area ?? d.Area,
            lead.Address ?? d.Address, lead.Website ?? d.Website, lead.Confidence);
        if (lead.UpdateProfile(profile, now, crm.Actor).Count > 0) changed = true;

        var contacts = await db.Contacts.Where(c => c.TenantId == tenantId && c.LeadId == leadId && !c.IsDeleted).ToListAsync(ct);
        var hasContactData = d.PhoneE164 is not null || d.Email is not null || d.ContactName is not null;
        var already = contacts.Any(c => (d.PhoneE164 is not null && c.PhoneE164 == d.PhoneE164) || (d.Email is not null && c.Email == d.Email));
        if (hasContactData && !already && (d.PhoneE164 is not null || d.Email is not null))
        {
            AddContact(leadId, d, primary: contacts.Count == 0);
            changed = true;
        }

        if (d.Tags.Count > 0)
        {
            var tagState = state ?? new RunState
            {
                Tags = await db.LeadTags.AsNoTracking().Where(t => t.TenantId == tenantId).ToDictionaryAsync(t => t.NormalizedName, t => t.Id, ct)
            };
            var existing = (await db.LeadTagMaps.AsNoTracking().Where(m => m.TenantId == tenantId && m.LeadId == leadId).Select(m => m.TagId).ToListAsync(ct)).ToHashSet();
            var before = existing.Count;
            await AddTagsAsync(leadId, d, existing, tagState);
            if (existing.Count > before) changed = true;
        }

        if (!changed) return false;
        db.LeadSources.Add(LeadSource.Create(tenantId, leadId, LeadSourceType.Import, $"{job.Id:N}:{row}", job.FileName, null, $"Merged from {job.FileName}, row {row}", now));
        db.LeadActivities.Add(LeadActivity.Create(tenantId, leadId, LeadActivityType.Merged, $"Data merged from import '{job.FileName}', row {row}.", null, crm.UserId, now));
        return true;
    }

    // ---------- results ----------
    public async Task<Result<PagedResult<ImportRowErrorDto>>> ErrorsAsync(Guid id, ImportErrorKind? kind, int page, int pageSize, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        if (!await db.ImportJobs.AnyAsync(j => j.Id == id && j.TenantId == tenantId, ct)) return Result.Failure<PagedResult<ImportRowErrorDto>>(Error.NotFound("Import"));
        var (p, size) = CrmFlow.Page(page, pageSize, 200);
        var q = db.ImportRowErrors.AsNoTracking().Where(e => e.TenantId == tenantId && e.ImportJobId == id);
        if (kind is { } k) q = q.Where(e => e.Kind == k);
        var total = await q.LongCountAsync(ct);
        var rows = await q.OrderBy(e => e.RowNumber).ThenBy(e => e.Id).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        var leadIds = rows.Where(r => r.DuplicateLeadId != null).Select(r => r.DuplicateLeadId!.Value).Distinct().ToList();
        var names = await db.Leads.AsNoTracking().Where(l => l.TenantId == tenantId && leadIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        return Result.Success(new PagedResult<ImportRowErrorDto>(rows.Select(e => new ImportRowErrorDto(e.Id, e.RowNumber, e.Kind, e.Column, e.Message, e.DuplicateLeadId,
            e.DuplicateLeadId is { } d ? names.GetValueOrDefault(d) : null, e.Resolution)).ToList(), p, size, total));
    }

    public async Task<Result<ExportFile>> ErrorReportAsync(Guid id, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId;
        var job = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.TenantId == tenantId, ct);
        if (job is null) return Result.Failure<ExportFile>(Error.NotFound("Import"));
        var rows = await db.ImportRowErrors.AsNoTracking().Where(e => e.TenantId == tenantId && e.ImportJobId == id).OrderBy(e => e.RowNumber).ThenBy(e => e.Id).Take(options.Value.MaxImportRows * 2).ToListAsync(ct);
        var data = rows.Select(e => (IReadOnlyList<string?>)[e.RowNumber.ToString(), e.Kind.ToString(), e.Column, e.Message, e.Resolution?.ToString()]).ToList();
        var bytes = SpreadsheetWriter.Csv(["Row", "Type", "Column", "Message", "Resolution"], data);
        crm.Record("Import.ErrorReportDownloaded", "ImportJob", id, $"Error report of import '{job.FileName}' downloaded.");
        await db.SaveChangesAsync(ct);
        return Result.Success(new ExportFile($"import-{job.Id:N}-errors.csv", SpreadsheetWriter.CsvType, bytes, rows.Count));
    }

    public Task<Result<ImportJobDto>> ResolveDuplicateAsync(Guid jobId, Guid errorId, ResolveDuplicateRequest request, CancellationToken ct = default) =>
        CrmFlow.GuardAsync(async () =>
        {
            var db = crm.Db; var tenantId = crm.TenantId;
            var job = await db.ImportJobs.FirstOrDefaultAsync(j => j.Id == jobId && j.TenantId == tenantId, ct);
            var row = await db.ImportRowErrors.FirstOrDefaultAsync(e => e.Id == errorId && e.ImportJobId == jobId && e.TenantId == tenantId, ct);
            if (job is null || row is null) return Result.Failure<ImportJobDto>(Error.NotFound("Import row"));
            if (row.Kind != ImportErrorKind.Duplicate || row.Resolution != DuplicateResolution.Pending) return Result.Failure<ImportJobDto>(Error.Conflict("This row is not awaiting a decision."));
            var resolution = DuplicateResolution.Skipped;
            var touched = new List<Guid>();
            if (request.Merge)
            {
                var data = ImportRowData.FromJson(row.RowData);
                if (data is null || row.DuplicateLeadId is not { } leadId) return Result.Failure<ImportJobDto>(Error.Validation("The row data is no longer available."));
                if (!await MergeAsync(leadId, data, job, row.RowNumber, null, ct)) return Result.Failure<ImportJobDto>(Error.Conflict("Nothing could be merged (the lead is archived or already has this data). Skip the row instead."));
                resolution = DuplicateResolution.Merged; touched.Add(leadId);
            }
            row.Resolve(resolution);
            job.ApplyResolution(resolution);
            crm.Record(resolution == DuplicateResolution.Merged ? "Import.DuplicateMerged" : "Import.DuplicateSkipped", "ImportJob", jobId, $"Import '{job.FileName}' row {row.RowNumber}: {resolution.ToString().ToLowerInvariant()}.");
            await db.SaveChangesAsync(ct);
            await scorer.RescoreAsync(touched, ct);
            return Result.Success(ToDto(job));
        }, logger);
}

public sealed class LeadExportService(CrmContext crm, IOptions<CrmOptions> options) : ILeadExportService
{
    private static readonly string[] Headers =
    [
        "Name", "Category", "Country", "Province", "City", "District", "Area", "Address", "Website", "Stage", "Status", "Owner", "Score", "Confidence", "Tags",
        "Source", "Created (UTC)", "Primary contact", "Contact role", "Contact phone", "Contact e-mail", "Contacts"
    ];

    public async Task<Result<ExportFile>> ExportAsync(LeadQuery query, ExportFormat format, CancellationToken ct = default)
    {
        var db = crm.Db; var tenantId = crm.TenantId; var max = options.Value.MaxExportRows;
        if (!Enum.IsDefined(format)) return Result.Failure<ExportFile>(Error.Validation("Choose CSV or Excel."));
        var q = query.Clone();
        var filtered = LeadQueries.Filter(db, tenantId, q);
        var total = await filtered.LongCountAsync(ct);
        if (total > max) return Result.Failure<ExportFile>(Error.Validation($"{total} leads match; the export limit is {max}. Narrow the filters and try again."));

        var data = new List<IReadOnlyList<string?>>();
        var ordered = LeadQueries.Sort(db, filtered, q);
        for (var skip = 0; skip < total; skip += 500)
        {
            var page = await ordered.Skip(skip).Take(500).Select(l => new
            {
                l.Id, l.Name, l.Country, l.Province, l.City, l.District, l.Area, l.Address, l.Website, l.Score, l.Confidence, l.OwnerUserId, l.PrimarySourceType, l.CreatedAtUtc,
                Category = db.LeadCategories.Where(c => c.Id == l.CategoryId).Select(c => c.Name).FirstOrDefault(),
                Stage = db.PipelineStages.Where(s => s.Id == l.PipelineStageId).Select(s => s.Name).FirstOrDefault(),
                Status = db.LeadStatuses.Where(s => s.Id == l.StatusId).Select(s => s.Name).FirstOrDefault(),
            }).ToListAsync(ct);
            var ids = page.Select(p => p.Id).ToList();
            var owners = await crm.UserNamesAsync(page.Select(p => p.OwnerUserId), ct);
            var contacts = (await db.Contacts.AsNoTracking().Where(c => c.TenantId == tenantId && !c.IsDeleted && ids.Contains(c.LeadId)).ToListAsync(ct)).ToLookup(c => c.LeadId);
            var tags = (await (from m in db.LeadTagMaps.AsNoTracking() join t in db.LeadTags.AsNoTracking() on m.TagId equals t.Id where m.TenantId == tenantId && ids.Contains(m.LeadId) select new { m.LeadId, t.Name }).ToListAsync(ct)).ToLookup(x => x.LeadId, x => x.Name);
            foreach (var l in page)
            {
                var cs = contacts[l.Id].OrderByDescending(c => c.IsPrimary).ThenBy(c => c.CreatedAtUtc).ToList();
                var p = cs.FirstOrDefault();
                data.Add([l.Name, l.Category, l.Country, l.Province, l.City, l.District, l.Area, l.Address, l.Website, l.Stage, l.Status, l.OwnerUserId is { } o ? owners.GetValueOrDefault(o) : null,
                    l.Score.ToString(), l.Confidence.ToString(), string.Join("; ", tags[l.Id].OrderBy(t => t)), l.PrimarySourceType.ToString(), l.CreatedAtUtc.ToString("yyyy-MM-dd HH:mm"),
                    p?.Name, p?.Role, p?.PhoneE164, p?.Email, cs.Count.ToString()]);
            }
        }

        var bytes = format == ExportFormat.Csv ? SpreadsheetWriter.Csv(Headers, data) : SpreadsheetWriter.Xlsx("Leads", Headers, data);
        var fileName = $"leads-{crm.Now:yyyyMMdd-HHmmss}.{(format == ExportFormat.Csv ? "csv" : "xlsx")}";
        crm.Record("Leads.Exported", "Lead", Guid.Empty, $"{data.Count} lead(s) exported as {format} (archived: {q.Archived}{(string.IsNullOrWhiteSpace(q.Search) ? "" : ", search filter applied")}).");
        await db.SaveChangesAsync(ct);
        return Result.Success(new ExportFile(fileName, format == ExportFormat.Csv ? SpreadsheetWriter.CsvType : SpreadsheetWriter.XlsxType, bytes, data.Count));
    }
}
