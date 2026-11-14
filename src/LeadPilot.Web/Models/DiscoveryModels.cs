using LeadPilot.Application.Common;
using LeadPilot.Application.Crm;
using LeadPilot.Application.Discovery;
using LeadPilot.Domain.Discovery;

namespace LeadPilot.Web.Models;

public sealed class DiscoveryListViewModel
{
    public PagedResult<DiscoveryJobDto> Result { get; set; } = new([], 1, 25, 0);
    public DiscoveryJobStatus? Status { get; set; }
    public bool CanRun { get; set; }
    public bool HasReadyConnector { get; set; }
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
}

public sealed class NewSearchViewModel
{
    public string? ConnectorType { get; set; }
    public string? Country { get; set; }
    public string? Province { get; set; }
    public string? City { get; set; }
    public string? District { get; set; }
    public string? Area { get; set; }
    public Guid? CategoryId { get; set; }
    public string? Keywords { get; set; }
    public int MaxResults { get; set; } = 40;
    public bool EnrichWebsites { get; set; } = true;
    public string? SeedUrls { get; set; }
    public Guid? PresetId { get; set; }
    public IReadOnlyList<ConnectorStatusDto> Connectors { get; set; } = [];
    public IReadOnlyList<CategoryMappingDto> Mappings { get; set; } = [];
    public IReadOnlyList<LookupItem> Categories { get; set; } = [];
    public IReadOnlyList<LocationPresetDto> Presets { get; set; } = [];
    public bool CanSetupConnectors { get; set; }
    public int MaxResultsCap { get; set; } = 200;
}

public sealed class DiscoveryDetailsViewModel
{
    public DiscoveryJobDto Job { get; set; } = default!;
    public bool CanRun { get; set; }
    public bool CanReview { get; set; }
}

public sealed class ResultsViewModel
{
    public PagedResult<DiscoveryResultDto> Result { get; set; } = new([], 1, 25, 0);
    public DiscoveryResultQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyList<DiscoveryJobDto> RecentJobs { get; set; } = [];
    public IReadOnlyList<OwnerDto> Owners { get; set; } = [];
    public bool CanReview { get; set; }
    public bool CanAssign { get; set; }
}

public sealed class ResultDetailsViewModel
{
    public DiscoveryResultDto Result { get; set; } = default!;
    public IReadOnlyList<LookupItem> Categories { get; set; } = [];
    public IReadOnlyList<OwnerDto> Owners { get; set; } = [];
    public bool CanReview { get; set; }
    public bool CanAssign { get; set; }
}

public sealed class DiscoverySetupViewModel
{
    public IReadOnlyList<CategoryMappingDto> Mappings { get; set; } = [];
    public IReadOnlyList<LookupItem> UnmappedCategories { get; set; } = [];
    public IReadOnlyList<LocationPresetDto> Locations { get; set; } = [];
    public IReadOnlyList<StarterPackDto> Packs { get; set; } = [];
    public bool CanManage { get; set; }
}

public sealed class ConnectorsViewModel
{
    public UsageSummaryDto Usage { get; set; } = default!;
    public IReadOnlyList<ConnectorStatusDto> All { get; set; } = [];
    public int Page { get; set; } = 1;
}

public sealed class HistoryViewModel
{
    public PagedResult<SearchHistoryItemDto> Result { get; set; } = new([], 1, 25, 0);
    public bool CanRun { get; set; }
}
