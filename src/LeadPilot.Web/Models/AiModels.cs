using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Common;
using LeadPilot.Domain.Ai;

namespace LeadPilot.Web.Models;

public class KnowledgeIndexViewModel
{
    public IReadOnlyList<KnowledgeSourceDto> Sources { get; set; } = [];
    public PagedResult<KnowledgeDocumentListItemDto> Result { get; set; } = default!;
    public KnowledgeDocumentQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanManage { get; set; }
    public AiSettingsSummary Ai { get; set; } = new();
}

/// <summary>What the knowledge pages need to tell people whether the AI can use their documents yet (no secrets).</summary>
public class AiSettingsSummary { public bool HasEmbeddingKey { get; set; } public bool HasKey { get; set; } public bool Enabled { get; set; } public int NeedingReindex { get; set; } public bool CanSeeSettings { get; set; } }

public class KnowledgeAddViewModel
{
    public string Tab { get; set; } = "text";
    public IReadOnlyList<KnowledgeSourceDto> Sources { get; set; } = [];
    public KnowledgeTextFormModel Text { get; set; } = new();
    public KnowledgeUploadFormModel Upload { get; set; } = new();
    public KnowledgeWebsiteFormModel Website { get; set; } = new();
    public IReadOnlyList<string> SupportedExtensions { get; set; } = [];
    public int MaxUploadMb { get; set; } = 10;
}

public class KnowledgeTextFormModel
{
    [Required, StringLength(200)] public string Title { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; } = DocumentKind.Faq;
    [Required] public string Content { get; set; } = string.Empty;
    public Guid? SourceId { get; set; }
}

public class KnowledgeUploadFormModel
{
    [StringLength(200)] public string? Title { get; set; }
    public DocumentKind Kind { get; set; } = DocumentKind.Product;
    public IFormFile? File { get; set; }
    public Guid? SourceId { get; set; }
    public Guid? ReplaceDocumentId { get; set; }
}

public class KnowledgeWebsiteFormModel
{
    [Required, StringLength(150)] public string Name { get; set; } = string.Empty;
    [Required, Url, StringLength(500)] public string Url { get; set; } = string.Empty;
    public DocumentKind Kind { get; set; } = DocumentKind.Website;
}

public class KnowledgeDocumentViewModel
{
    public KnowledgeDocumentDto Document { get; set; } = default!;
    public PagedResult<ChunkDto> Chunks { get; set; } = default!;
    public Guid? ShownVersionId { get; set; }
    public bool CanManage { get; set; }
    public KnowledgeTextFormModel Edit { get; set; } = new();
    public KnowledgeUploadFormModel Replace { get; set; } = new();
    public int MaxUploadMb { get; set; } = 10;
}

public class AiTesterViewModel
{
    public AskQuestionRequest Request { get; set; } = new("");
    public AiTestResultDto? Result { get; set; }
    public string? Error { get; set; }
    public bool Configured { get; set; }
    [Required, StringLength(2000)] public string Question { get; set; } = string.Empty;
    public bool RetrieveOnly { get; set; }
    public int IndexedDocuments { get; set; }
}

public class AiSettingsViewModel
{
    public AiSettingsDto Settings { get; set; } = default!;
    public AiSettingsFormModel Form { get; set; } = new();
    public bool CanManageKnowledge { get; set; }
}

public class AiSettingsFormModel
{
    [Required, StringLength(100)] public string ChatModel { get; set; } = string.Empty;
    [Required, StringLength(100)] public string ClassifierModel { get; set; } = string.Empty;
    [Required, StringLength(100)] public string SummaryModel { get; set; } = string.Empty;
    [Required, StringLength(100)] public string EmbeddingModel { get; set; } = string.Empty;
    [Range(1, 12)] public int TopK { get; set; } = 5;
    [Range(0, 1)] public decimal MinRetrievalScore { get; set; } = 0.35m;
    [Range(0, 1)] public decimal MinConfidence { get; set; } = 0.6m;
    [Range(2, 50)] public int HistoryMessages { get; set; } = 12;
    [Range(1000, 40000)] public int MaxContextChars { get; set; } = 6000;
    [Range(0, long.MaxValue)] public long? MonthlyTokenLimit { get; set; }
    [Range(0, 10000000)] public decimal? MonthlyBudgetUsd { get; set; }
    [Required, StringLength(500)] public string UnsupportedReply { get; set; } = string.Empty;
    [StringLength(1000)] public string? ToneInstructions { get; set; }
    [StringLength(2000)] public string? EscalationKeywords { get; set; }
}

public class AiKeyFormModel { [Required, StringLength(400, MinimumLength = 12)] public string ApiKey { get; set; } = string.Empty; }

public class AiUsageViewModel
{
    public AiUsageSummaryDto Summary { get; set; } = default!;
    public PagedResult<AiUsageEntryDto> Entries { get; set; } = default!;
    public IReadOnlyList<(int Key, string Label)> Periods { get; set; } = [];
    public int Period { get; set; }
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
}

public class AiActivityViewModel
{
    public PagedResult<AiActivityItemDto> Result { get; set; } = default!;
    public AiActivityQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
}
