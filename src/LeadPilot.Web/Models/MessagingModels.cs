using System.ComponentModel.DataAnnotations;
using LeadPilot.Application.Common;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.Web.Models;

public class AccountFormModel
{
    [Required, StringLength(100)] public string DisplayName { get; set; } = string.Empty;
    [Required, StringLength(500), Display(Name = "WAHA base URL")] public string BaseUrl { get; set; } = string.Empty;
    [Required, StringLength(500), Display(Name = "API key")] public string ApiKey { get; set; } = string.Empty;
    [Required, StringLength(63), Display(Name = "Session name")] public string SessionName { get; set; } = string.Empty;
    [StringLength(200), Display(Name = "Webhook secret")] public string? WebhookSecret { get; set; }
    [Display(Name = "Hourly cap")] public int HourlyCap { get; set; } = ChannelAccount.DefaultHourlyCap;
    [Display(Name = "Daily cap")] public int DailyCap { get; set; } = ChannelAccount.DefaultDailyCap;
    [Display(Name = "Seconds between messages")] public int MinDelaySeconds { get; set; } = ChannelAccount.DefaultMinDelaySeconds;
}

public class LimitsFormModel
{
    [Required, StringLength(100)] public string DisplayName { get; set; } = string.Empty;
    public int HourlyCap { get; set; }
    public int DailyCap { get; set; }
    public int MinDelaySeconds { get; set; }
}

public class CredentialsFormModel
{
    [Required, StringLength(500), Display(Name = "WAHA base URL")] public string BaseUrl { get; set; } = string.Empty;
    [Required, StringLength(500), Display(Name = "API key")] public string ApiKey { get; set; } = string.Empty;
    [StringLength(200), Display(Name = "Webhook secret (leave empty to keep the current one)")] public string? WebhookSecret { get; set; }
}

public class AccountsViewModel
{
    public IReadOnlyList<ChannelAccountDto> Accounts { get; set; } = [];
    public bool CanManage { get; set; }
    public MessagingStatusDto? Status { get; set; }
}

public class AccountDetailsViewModel
{
    public ChannelAccountDto Account { get; set; } = default!;
    public ChannelAccountSetupDto? Setup { get; set; }
    public IReadOnlyList<AccountNotificationDto> Notifications { get; set; } = [];
    public bool CanManage { get; set; }
    public LimitsFormModel Limits { get; set; } = new();
    public string? PublicWebhookBase { get; set; }
}

public class ConversationsViewModel
{
    public PagedResult<ConversationListItemDto> Result { get; set; } = default!;
    public ConversationQuery Query { get; set; } = new();
    public IReadOnlyList<ChannelAccountDto> Accounts { get; set; } = [];
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
}

public class ThreadViewModel
{
    public ConversationDto Conversation { get; set; } = default!;
    public PagedResult<MessageDto> Messages { get; set; } = default!;
    public IReadOnlyList<TemplateDto> Templates { get; set; } = [];
    public bool CanReply { get; set; }
    public ReplyFormModel Reply { get; set; } = new();
}

public class ReplyFormModel
{
    [StringLength(Message.MaxBodyLength)] public string? Text { get; set; }
    public Guid? TemplateId { get; set; }
    /// <summary>Generated when the form is rendered: a double click or a browser resubmit sends the same key and cannot create a second message.</summary>
    public string IdempotencyKey { get; set; } = Guid.NewGuid().ToString("N");
}

public class TemplateFormModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(Message.MaxBodyLength)] public string Body { get; set; } = string.Empty;
    public TemplateCategory Category { get; set; } = TemplateCategory.Service;
}

public class TemplatesViewModel
{
    public IReadOnlyList<TemplateDto> Templates { get; set; } = [];
    public bool CanManage { get; set; }
    public IReadOnlyList<string> KnownVariables { get; set; } = TemplateRenderer.KnownVariables;
}

public class WebhooksViewModel
{
    public PagedResult<WebhookEventDto> Result { get; set; } = default!;
    public WebhookEventQuery Query { get; set; } = new();
    public WebhookEventStatsDto Stats { get; set; } = default!;
    public MessagingStatusDto Status { get; set; } = default!;
    public IDictionary<string, string?> QueryValues { get; set; } = new Dictionary<string, string?>();
}

public class DeadLettersViewModel
{
    public IReadOnlyList<DeadLetterQueueDto> Queues { get; set; } = [];
    public string? Selected { get; set; }
    public IReadOnlyList<DeadLetterMessageDto> Messages { get; set; } = [];
}
