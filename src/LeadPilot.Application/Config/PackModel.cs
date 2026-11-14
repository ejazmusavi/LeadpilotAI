using System.Text.Json;

namespace LeadPilot.Application.Config;

// ---------------------------------------------------------------------------------------------------------------------------------
// The industry template pack format (format "leadpilot.pack", formatVersion 1). A pack is DATA: JSON that is parsed against this
// schema and never executed. Everything below is created as ordinary tenant configuration by PackEngine.
// ---------------------------------------------------------------------------------------------------------------------------------

public sealed record PackCategory(string Key, string Name, string? Parent);
public sealed record PackStage(string Name, string Kind);
public sealed record PackPipeline(string Key, string Name, IReadOnlyList<PackStage> Stages);
public sealed record PackCustomField(string Key, string Entity, string Label, string Type, IReadOnlyList<string> Options, bool Required, decimal? Min, decimal? Max, string? Pattern, string? Help, bool Filterable);
public sealed record PackScoringRule(string Dimension, string Target, int Points, string? Description);
public sealed record PackTemplate(string Key, string Name, string Body, string Category);
public sealed record PackQuestion(string Key, string Question, string Type, IReadOnlyList<string> Choices, bool Required);
public sealed record PackAgent(string Key, string Name, string? Description, string Instructions, string? OffTopicReply, IReadOnlyList<PackQuestion> Questions);
public sealed record PackPlan(string Key, string Name, decimal? Price, string Currency, string? Period, string? Details);
public sealed record PackProduct(string Key, string Kind, string Name, string? Summary, IReadOnlyList<string> Features, IReadOnlyList<PackPlan> Plans);
public sealed record PackKnowledge(string Key, string Name);
public sealed record PackStep(string Key, string Type, string Name, JsonElement? Config, JsonElement? Condition, string OnFailure, int MaxRetries);
public sealed record PackWorkflow(string Key, string Name, string? Description, string TriggerEvent, JsonElement? TriggerCondition, int? InactiveDays, IReadOnlyList<PackStep> Steps, string ReEntry, int CooldownHours, int MaxStartsPer30Days);
/// <summary>Maps the pack category <see cref="Category"/> (a pack category key) to provider search phrases.</summary>
public sealed record PackDiscoveryCategory(string Key, string Category, IReadOnlyList<string> SearchPhrases, string? PlaceType);
public sealed record PackDiscoveryLocation(string Key, string Country, string? Province, string? City);
public sealed record PackDiscovery(IReadOnlyList<PackDiscoveryCategory> Categories, IReadOnlyList<PackDiscoveryLocation> Locations);

public sealed record PackDocument(
    string Key, int Version, string Name, string? Description, string? Industry, bool Sample, string? Notice,
    IReadOnlyList<PackCategory> Categories, IReadOnlyList<PackPipeline> Pipelines, IReadOnlyList<PackCustomField> CustomFields, IReadOnlyList<PackScoringRule> ScoringRules,
    IReadOnlyList<PackTemplate> MessageTemplates, IReadOnlyList<PackAgent> Agents, IReadOnlyList<PackProduct> Catalog, IReadOnlyList<PackKnowledge> Knowledge,
    IReadOnlyList<PackWorkflow> Workflows, PackDiscovery? Discovery)
{
    public int ItemCount => Categories.Count + Pipelines.Count + Pipelines.Sum(p => p.Stages.Count) + CustomFields.Count + ScoringRules.Count + MessageTemplates.Count + Agents.Count
        + Agents.Sum(a => a.Questions.Count) + Catalog.Count + Catalog.Sum(c => c.Plans.Count) + Knowledge.Count + Workflows.Count + (Discovery?.Categories.Count ?? 0) + (Discovery?.Locations.Count ?? 0);
}

public sealed record PackProblem(string Path, string Message);
public sealed record PackParseResult(PackDocument? Pack, string? CanonicalJson, string? Hash, IReadOnlyList<PackProblem> Problems) { public bool IsValid => Pack is not null && Problems.Count == 0; }
