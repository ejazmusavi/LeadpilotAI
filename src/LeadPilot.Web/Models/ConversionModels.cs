using LeadPilot.Application.Common;
using LeadPilot.Application.Conversion;
using LeadPilot.Application.Crm;

namespace LeadPilot.Web.Models;

public class CalendarViewModel
{
    public string View { get; set; } = "week";
    public DateTime Date { get; set; }
    public DateTime FromLocal { get; set; }
    public int Days { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public Guid? RepUserId { get; set; }
    public IReadOnlyList<DemoDto> Demos { get; set; } = [];
    public IReadOnlyList<SlotDto> Slots { get; set; } = [];
    public IReadOnlyList<DemoRepDto> Reps { get; set; } = [];
    public bool CanBook { get; set; }
    public bool Configured { get; set; }
}

public class BookDemoViewModel
{
    public Guid? LeadId { get; set; }
    public string? LeadName { get; set; }
    public string? Search { get; set; }
    public IReadOnlyList<LeadListItemDto> Leads { get; set; } = [];
    public DateTime Date { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public IReadOnlyList<SlotDto> Slots { get; set; } = [];
    public string? SlotId { get; set; }
    public string? Notes { get; set; }
    public string? MeetingLink { get; set; }
    public Guid? PreferredRepUserId { get; set; }
    public IReadOnlyList<DemoRepDto> Reps { get; set; } = [];
}

public class DemoDetailsViewModel
{
    public DemoDto Demo { get; set; } = default!;
    public bool CanBook { get; set; }
    public DateTime Date { get; set; }
    public IReadOnlyList<SlotDto> Slots { get; set; } = [];
    public IReadOnlyList<DemoRepDto> Reps { get; set; } = [];
    public bool Rescheduling { get; set; }
}

public class DemoSettingsViewModel
{
    public ConversionSettingsDto Settings { get; set; } = default!;
    public IReadOnlyList<DemoRepDto> Reps { get; set; } = [];
    public IReadOnlyList<LookupItem> Candidates { get; set; } = [];
    public IReadOnlyList<LookupItem> Accounts { get; set; } = [];
    public bool CanManage { get; set; }
}

public class RepHoursViewModel
{
    public DemoRepDto Rep { get; set; } = default!;
    public bool CanManage { get; set; }
}

public class TrialsViewModel
{
    public PagedResult<TrialDto> Result { get; set; } = default!;
    public TrialQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanManage { get; set; }
}

public class TrialCreateViewModel
{
    public Guid? LeadId { get; set; }
    public string? LeadName { get; set; }
    public string? Search { get; set; }
    public IReadOnlyList<LeadListItemDto> Leads { get; set; } = [];
    public Guid? PlanId { get; set; }
    public int? DurationDays { get; set; }
    public string? Notes { get; set; }
    public IReadOnlyList<PlanOption> Plans { get; set; } = [];
    public int DefaultDays { get; set; }
}

public sealed record PlanOption(Guid Id, string Label, decimal? Price, string Currency);

public class TrialDetailsViewModel { public TrialDto Trial { get; set; } = default!; public bool CanManage { get; set; } }

public class CustomersViewModel
{
    public PagedResult<CustomerListItemDto> Result { get; set; } = default!;
    public CustomerQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public IReadOnlyList<WonOpportunityDto> Won { get; set; } = [];
    public bool CanManage { get; set; }
}

public class ConvertViewModel
{
    public Guid OpportunityId { get; set; }
    public string? LeadName { get; set; }
    public string? OpportunityName { get; set; }
    public decimal OpportunityValue { get; set; }
    public string OpportunityCurrency { get; set; } = "USD";
    public Guid PlanId { get; set; }
    public string? Terms { get; set; }
    public LeadPilot.Domain.Conversion.BillingCycle BillingCycle { get; set; } = LeadPilot.Domain.Conversion.BillingCycle.Monthly;
    public decimal? Price { get; set; }
    public string? Currency { get; set; }
    public Guid? TrialId { get; set; }
    public IReadOnlyList<PlanOption> Plans { get; set; } = [];
    public IReadOnlyList<TrialDto> Trials { get; set; } = [];
}

public class CustomerDetailsViewModel { public CustomerDto Customer { get; set; } = default!; public bool CanManage { get; set; } public bool CanRequest { get; set; } public bool CanVerify { get; set; } }

public class PaymentsViewModel
{
    public PagedResult<PaymentRequestDto> Result { get; set; } = default!;
    public PaymentRequestQuery Query { get; set; } = new();
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
    public bool CanRequest { get; set; }
    public bool CanVerify { get; set; }
}

public class ProvidersViewModel
{
    public IReadOnlyList<(string Key, string Name)> Installed { get; set; } = [];
    public IReadOnlyList<ProviderConfigDto> Configs { get; set; } = [];
    public string WebhookBase { get; set; } = string.Empty;
}
