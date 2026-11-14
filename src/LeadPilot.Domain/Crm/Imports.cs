using LeadPilot.Domain.Common;

namespace LeadPilot.Domain.Crm;

public class ImportJob : AuditableEntity, ITenantOwned
{
    private ImportJob() { }
    public Guid TenantId { get; private set; }
    public string FileName { get; private set; } = string.Empty;
    public ImportFileType FileType { get; private set; }
    public long FileSizeBytes { get; private set; }
    /// <summary>Tenant-relative storage key of the uploaded file (never a client-supplied path).</summary>
    public string? StoredFileKey { get; private set; }
    public ImportStatus Status { get; private set; } = ImportStatus.Uploaded;
    public DuplicatePolicy DuplicatePolicy { get; private set; } = DuplicatePolicy.Skip;
    public int TotalRows { get; private set; }
    public int CreatedCount { get; private set; }
    public int MergedCount { get; private set; }
    public int SkippedDuplicateCount { get; private set; }
    public int PendingDuplicateCount { get; private set; }
    public int ErrorCount { get; private set; }
    /// <summary>JSON of the column mapping used for the run.</summary>
    public string? Mapping { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTime? StartedAtUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }

    public static ImportJob Create(Guid tenantId, string fileName, ImportFileType type, long size, string storedKey, int totalRows, DateTime nowUtc, string? actor)
    {
        var name = Path.GetFileName(fileName ?? string.Empty).Trim();
        if (name.Length == 0) throw new DomainException("A file name is required.");
        return new ImportJob { TenantId = tenantId, FileName = name.Length <= 255 ? name : name[^255..], FileType = type, FileSizeBytes = size, StoredFileKey = storedKey, TotalRows = totalRows, CreatedAtUtc = nowUtc, CreatedBy = actor };
    }

    public void Start(DuplicatePolicy policy, string mappingJson, DateTime nowUtc)
    {
        if (Status != ImportStatus.Uploaded) throw new DomainException("This import has already been run.");
        Status = ImportStatus.Processing; DuplicatePolicy = policy; Mapping = mappingJson; StartedAtUtc = nowUtc;
    }

    public void Complete(int created, int merged, int skipped, int pending, int errors, DateTime nowUtc)
    {
        CreatedCount = created; MergedCount = merged; SkippedDuplicateCount = skipped; PendingDuplicateCount = pending; ErrorCount = errors;
        Status = ImportStatus.Completed; CompletedAtUtc = nowUtc;
    }

    public void Fail(string reason, DateTime nowUtc) { Status = ImportStatus.Failed; FailureReason = reason.Length <= 500 ? reason : reason[..500]; CompletedAtUtc = nowUtc; }

    public void ApplyResolution(DuplicateResolution resolution)
    {
        if (PendingDuplicateCount > 0) PendingDuplicateCount--;
        if (resolution == DuplicateResolution.Merged) MergedCount++; else SkippedDuplicateCount++;
    }
}

/// <summary>A row that was not imported: either invalid, or a duplicate awaiting review/skipped.</summary>
public class ImportRowError : Entity, ITenantOwned
{
    private ImportRowError() { }
    public Guid TenantId { get; private set; }
    public Guid ImportJobId { get; private set; }
    public int RowNumber { get; private set; }
    public ImportErrorKind Kind { get; private set; }
    public string? Column { get; private set; }
    public string Message { get; private set; } = string.Empty;
    /// <summary>The row as mapped (JSON), kept so a pending duplicate can be merged later.</summary>
    public string? RowData { get; private set; }
    public Guid? DuplicateLeadId { get; private set; }
    public DuplicateResolution? Resolution { get; private set; }

    public static ImportRowError Validation(Guid tenantId, Guid jobId, int row, string? column, string message, string? rowData) =>
        new() { TenantId = tenantId, ImportJobId = jobId, RowNumber = row, Kind = ImportErrorKind.Validation, Column = column, Message = Trim(message, 500)!, RowData = Trim(rowData, 8000) };

    public static ImportRowError Duplicate(Guid tenantId, Guid jobId, int row, string message, string rowData, Guid duplicateLeadId, DuplicateResolution resolution) =>
        new() { TenantId = tenantId, ImportJobId = jobId, RowNumber = row, Kind = ImportErrorKind.Duplicate, Message = Trim(message, 500)!, RowData = Trim(rowData, 8000), DuplicateLeadId = duplicateLeadId, Resolution = resolution };

    public void Resolve(DuplicateResolution resolution)
    {
        if (Kind != ImportErrorKind.Duplicate || Resolution != DuplicateResolution.Pending) throw new DomainException("This row is not awaiting a decision.");
        Resolution = resolution;
    }

    private static string? Trim(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
