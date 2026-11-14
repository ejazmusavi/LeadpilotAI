using System.Text.Json;
using LeadPilot.Application.Workflows;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Workflows;

namespace LeadPilot.UnitTests;

public class WorkflowLogicTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly DateTime Now = new(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);   // a Wednesday
    private static Dictionary<string, object?> Facts(string city = "Lahore", decimal score = 50) => new()
    { ["lead.city"] = city, ["lead.score"] = score, ["lead.tags"] = (IReadOnlyList<string>)["vip", "school"], ["contact.has_phone"] = true, ["message.text"] = "What is the PRICE?", ["lead.name"] = "{{7*7}} <script>" };
    private static bool Eval(string json, Dictionary<string, object?>? f = null, DateTime? now = null, TimeZoneInfo? tz = null)
    { var p = ConditionParser.Parse(json); Assert.True(p.IsValid, string.Join(";", p.Errors)); return ConditionEvaluator.Evaluate(p.Condition!, f ?? Facts(), now ?? Now, tz ?? Utc); }

    [Theory]
    [InlineData("""{"field":"lead.city","op":"eq","value":"lahore"}""", true)]
    [InlineData("""{"field":"lead.city","op":"neq","value":"Lahore"}""", false)]
    [InlineData("""{"field":"lead.score","op":"gte","value":50}""", true)]
    [InlineData("""{"field":"lead.score","op":"gt","value":50}""", false)]
    [InlineData("""{"field":"lead.tags","op":"contains","value":"VIP"}""", true)]
    [InlineData("""{"field":"lead.tags","op":"not_contains","value":"vip"}""", false)]
    [InlineData("""{"field":"message.text","op":"contains","value":"price"}""", true)]
    [InlineData("""{"field":"lead.city","op":"in","value":["Karachi","Lahore"]}""", true)]
    [InlineData("""{"field":"lead.city","op":"in","value":"a, b"}""", false)]
    [InlineData("""{"field":"contact.has_phone","op":"eq","value":true}""", true)]
    [InlineData("""{"field":"lead.province","op":"is_empty"}""", true)]
    [InlineData("""{"all":[{"field":"lead.city","op":"eq","value":"Lahore"},{"field":"lead.score","op":"lt","value":10}]}""", false)]
    [InlineData("""{"any":[{"field":"lead.city","op":"eq","value":"x"},{"field":"lead.score","op":"lt","value":100}]}""", true)]
    [InlineData("""{"not":{"field":"lead.city","op":"eq","value":"Lahore"}}""", false)]
    [InlineData("""{"window":{"days":["wed"],"from":"09:00","to":"17:00"}}""", true)]
    [InlineData("""{"window":{"days":["tue","thu"],"from":"09:00","to":"17:00"}}""", false)]
    public void Conditions_evaluate_against_facts(string json, bool expected) => Assert.Equal(expected, Eval(json));

    [Fact]
    public void Business_hours_use_the_tenant_time_zone_and_overnight_windows_belong_to_the_day_they_start_on()
    {
        var karachi = TimeZoneInfo.CreateCustomTimeZone("pk", TimeSpan.FromHours(5), "pk", "pk");
        // 10:00 UTC is 15:00 in Karachi
        Assert.True(Eval("""{"window":{"days":["wed"],"from":"14:00","to":"16:00"}}""", tz: karachi));
        Assert.False(Eval("""{"window":{"days":["wed"],"from":"14:00","to":"16:00"}}"""));
        var night = """{"window":{"days":["wed"],"from":"22:00","to":"06:00"}}""";
        Assert.True(Eval(night, now: new DateTime(2026, 3, 4, 23, 0, 0, DateTimeKind.Utc)));    // Wed 23:00
        Assert.True(Eval(night, now: new DateTime(2026, 3, 5, 5, 0, 0, DateTimeKind.Utc)));     // Thu 05:00 still Wed's window
        Assert.False(Eval(night, now: new DateTime(2026, 3, 4, 5, 0, 0, DateTimeKind.Utc)));    // Wed 05:00 belongs to Tue's window
    }

    [Theory]
    [InlineData("""{"field":"lead.password","op":"eq","value":"x"}""")]                                  // unknown field
    [InlineData("""{"field":"lead.city","op":"regex","value":".*"}""")]                                  // no regular expressions
    [InlineData("""{"field":"lead.score","op":"contains","value":"1"}""")]                               // operator not valid for numbers
    [InlineData("""{"field":"lead.score","op":"gt","value":"abc"}""")]
    [InlineData("""{"field":"lead.city","op":"eq","value":"x","script":"1+1"}""")]                       // extra property
    [InlineData("""{"eval":"System.IO.File.ReadAllText('/etc/passwd')"}""")]
    [InlineData("""{"all":[]}""")]
    [InlineData("""{"all":"x"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"window":{"days":["funday"],"from":"09:00","to":"17:00"}}""")]
    [InlineData("""{"window":{"days":["mon"],"from":"09:00","to":"09:00"}}""")]
    [InlineData("not json {{")]
    [InlineData("")]
    public void Hostile_or_malformed_conditions_are_rejected_when_parsed(string json) => Assert.False(ConditionParser.Parse(json).IsValid);

    [Fact]
    public void Size_and_depth_are_bounded_and_values_are_data_never_code()
    {
        var deep = """{"field":"lead.city","op":"eq","value":"x"}""";
        for (var i = 0; i < 8; i++) deep = $$"""{"not":{{deep}}}""";
        Assert.False(ConditionParser.Parse(deep).IsValid);
        var wide = "{\"all\":[" + string.Join(",", Enumerable.Repeat("""{"field":"lead.city","op":"eq","value":"x"}""", 30)) + "]}";
        Assert.False(ConditionParser.Parse(wide).IsValid);
        Assert.False(ConditionParser.Parse($$"""{"field":"lead.city","op":"eq","value":"{{new string('a', 500)}}"}""").IsValid);
        Assert.False(ConditionParser.Parse(new string(' ', 20_000) + "{}").IsValid);
        // a value that looks like a template or script is just text
        Assert.True(Eval("""{"field":"lead.name","op":"eq","value":"{{7*7}} <script>"}"""));
        Assert.False(Eval("""{"field":"lead.name","op":"eq","value":"49"}"""));
        Assert.False(ConditionEvaluator.EvaluateJson("""{"field":"nope","op":"eq","value":1}""", Facts(), Now, Utc));   // a damaged stored condition never lets a step run
    }

    private static WorkflowDefinition Def(object[] steps, params string[] triggers) => new(triggers.Select(t => new TriggerDefinition(t, null, t == "lead.inactive" ? 7 : null)).ToList(),
        steps.Select(s => (StepDefinition)s).ToList());
    private static StepDefinition S(string key, StepType type, string? cfg = null, FailurePolicy p = FailurePolicy.Retry) => new(key, type, key, cfg is null ? null : J(cfg), null, p);
    private static ValidationReport V(params StepDefinition[] steps) => WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("lead.created")], steps));

    [Fact]
    public void Valid_definitions_pass_and_unknown_things_or_limits_fail()
    {
        Assert.True(V(S("a", StepType.AddTag, """{"tag":"x"}"""), S("w", StepType.Wait, """{"minutes":5}""")).IsValid);
        Assert.False(V().IsValid);
        Assert.False(V(S("a", StepType.AddTag, """{"tag":"x","code":"1"}""")).IsValid);                      // unknown setting
        Assert.False(V(S("a", StepType.Wait, """{"minutes":0}""")).IsValid);
        Assert.False(V(S("a", StepType.Wait, """{"minutes":999999}""")).IsValid);
        Assert.False(V(S("a", StepType.SendMessage, """{"templateId":"x"}""")).IsValid);
        Assert.False(V(S("a", StepType.UpdateLeadFields, """{"field":"name","value":"x"}""")).IsValid);       // only the allow-listed text fields
        Assert.False(V(S("a", StepType.UpdateLeadFields, """{"field":"city","value":"{{x}}"}""")).IsValid);
        Assert.False(V(S("a", StepType.CreateFollowUp, """{"dueInHours":1,"note":"hi {{nope}}"}""")).IsValid);   // unknown template variable
        Assert.False(V(S("A B", StepType.AddTag, """{"tag":"x"}""")).IsValid);
        Assert.False(V(S("a", StepType.AddTag, """{"tag":"x"}"""), S("a", StepType.AddTag, """{"tag":"y"}""")).IsValid);
        Assert.False(V(Enumerable.Range(0, 26).Select(i => S("s" + i, StepType.AddTag, """{"tag":"x"}""")).ToArray()).IsValid);   // max steps
        Assert.False(WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("nope")], [S("a", StepType.AddTag, """{"tag":"x"}""")])).IsValid);
        Assert.False(WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("lead.created"), new("lead.created")], [S("a", StepType.AddTag, """{"tag":"x"}""")])).IsValid);
        Assert.False(WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("lead.inactive")], [S("a", StepType.AddTag, """{"tag":"x"}""")])).IsValid);   // needs the number of days
        Assert.False(WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("lead.created")], [S("a", StepType.AddTag, """{"tag":"x"}""")], CooldownHours: -1)).IsValid);
        var phase10 = WorkflowDefinitionValidator.Validate(new WorkflowDefinition([new("demo.booked")], [S("a", StepType.AddTag, """{"tag":"x"}""")]));
        Assert.True(phase10.IsValid); Assert.DoesNotContain(phase10.Warnings, w => w.Message.Contains("Phase 10"));   // the event has a producer now
    }

    [Fact]
    public void Loops_missing_targets_and_too_many_branches_are_rejected()
    {
        const string c = """{"field":"lead.score","op":"gt","value":1}""";
        StepDefinition B(string k, string then, string els) => S(k, StepType.Branch, $$"""{"condition":{{c}},"then":"{{then}}","else":"{{els}}"}""");
        var loop = V(B("a", "b", "end"), B("b", "a", "end"));
        Assert.False(loop.IsValid); Assert.Contains("loop", loop.Summary);
        Assert.False(V(B("a", "a", "end")).IsValid);                                                         // self loop
        Assert.Contains("does not exist", V(B("a", "zzz", "end")).Summary);
        Assert.True(V(B("a", "c", "end"), S("b", StepType.AddTag, """{"tag":"x"}"""), S("c", StepType.Stop)).IsValid);   // forward jumps are fine
        var many = Enumerable.Range(0, 7).Select(i => B("b" + i, i == 6 ? "end" : "b" + (i + 1), i == 6 ? "end" : "b" + (i + 1))).ToArray();
        Assert.Contains("if/else", V(many).Summary);
    }

    [Fact]
    public void Idempotency_keys_are_stable_and_backoff_is_bounded()
    {
        var id = Guid.NewGuid();
        Assert.Equal(WorkflowIdempotency.StepKey(id, "send", 0), WorkflowIdempotency.StepKey(id, "send", 0));
        Assert.NotEqual(WorkflowIdempotency.StepKey(id, "send", 0), WorkflowIdempotency.StepKey(id, "send", 1));
        Assert.True(WorkflowIdempotency.StepKey(id, "abcdefghij0123456789", 99).Length <= 100);
        var lead = Guid.NewGuid(); var wf = Guid.NewGuid(); var t = DateTime.UtcNow;
        Assert.Equal(WorkflowIdempotency.SweepEventId(wf, lead, t), WorkflowIdempotency.SweepEventId(wf, lead, t));
        Assert.NotEqual(WorkflowIdempotency.SweepEventId(wf, lead, t), WorkflowIdempotency.SweepEventId(wf, lead, t.AddDays(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), WorkflowIdempotency.RetryBackoff(1)); Assert.Equal(TimeSpan.FromMinutes(60), WorkflowIdempotency.RetryBackoff(50));
    }

    [Fact]
    public void Execution_state_survives_a_round_trip_and_garbage_state_is_harmless()
    {
        var s = new ExecutionState(); s.Of("a").Status = "done"; s.Of("a").Generation = 2; s.Executed = 3;
        var back = ExecutionState.Parse(s.Serialize());
        Assert.Equal("done", back.Steps["a"].Status); Assert.Equal(2, back.Steps["a"].Generation); Assert.Equal(3, back.Executed);
        Assert.Empty(ExecutionState.Parse("not json").Steps); Assert.Empty(ExecutionState.Parse(null).Steps);
    }

    [Fact]
    public void Trigger_catalog_lists_the_phase_10_events_as_available_with_a_producer()
    {
        foreach (var k in new[] { "demo.booked", "demo.completed", "trial.created", "trial.expires", "payment.confirmed" }) { var t = TriggerCatalog.Find(k)!; Assert.True(t.Available); Assert.True(t.HasProducer); Assert.Null(t.UnavailableReason); }
        Assert.All(TriggerCatalog.All, t => Assert.True(t.HasProducer));
        Assert.Equal(12, TriggerCatalog.All.Count);
        Assert.DoesNotContain(WorkflowEventTypes.LeadInactive, TriggerCatalog.ConsumedEventTypes);     // evaluated by a Hangfire job, not a broker event
        Assert.Contains(WorkflowEventTypes.MessageReceived, TriggerCatalog.ConsumedEventTypes);
    }

    [Fact]
    public void A_version_is_immutable_once_active_and_executions_follow_their_state_machine()
    {
        var v = WorkflowVersion.Create(Guid.NewGuid(), Guid.NewGuid(), 1, ReEntryPolicy.Once, 0, 3, null, DateTime.UtcNow, null);
        v.Configure(ReEntryPolicy.Always, 1, 5, "ok"); v.Activate(DateTime.UtcNow);
        Assert.Throws<DomainException>(() => v.Configure(ReEntryPolicy.Once, 0, 0, null));
        Assert.Throws<DomainException>(() => v.Activate(DateTime.UtcNow));
        v.Retire(DateTime.UtcNow); Assert.Equal(VersionState.Retired, v.State);

        var e = WorkflowExecution.Create(Guid.NewGuid(), Guid.NewGuid(), v.Id, Guid.NewGuid(), "lead.created", Guid.NewGuid(), null, null, "a", true, null, DateTime.UtcNow);
        Assert.Equal("A", e.ActiveSlot); Assert.Throws<DomainException>(() => e.Reopen("a", true, DateTime.UtcNow));
        e.End(ExecutionStatus.Failed, "x", DateTime.UtcNow); Assert.Null(e.ActiveSlot);
        e.Reopen("a", true, DateTime.UtcNow); Assert.Equal("A", e.ActiveSlot); Assert.Equal(ExecutionStatus.Waiting, e.Status);
        e.End(ExecutionStatus.Cancelled, "y", DateTime.UtcNow); Assert.Throws<DomainException>(() => e.End(ExecutionStatus.Completed, null, DateTime.UtcNow));
        var overlapping = WorkflowExecution.Create(Guid.NewGuid(), Guid.NewGuid(), v.Id, Guid.NewGuid(), "x", Guid.NewGuid(), null, null, "a", false, null, DateTime.UtcNow);
        Assert.NotEqual("A", overlapping.ActiveSlot);
        var w = Workflow.Create(Guid.NewGuid(), "w", null, DateTime.UtcNow, null); w.Activate(v.Id, DateTime.UtcNow, null);
        Assert.Throws<DomainException>(() => w.Delete(DateTime.UtcNow, null)); w.Deactivate(DateTime.UtcNow, null); w.Delete(DateTime.UtcNow, null);
    }
}
