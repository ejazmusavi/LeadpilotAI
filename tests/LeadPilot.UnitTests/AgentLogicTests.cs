using LeadPilot.Application.Agents;
using LeadPilot.Application.Ai;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Agents;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.UnitTests;

public class AgentToolArgumentTests
{
    private static ToolDefinition Tool(string n) => AgentToolCatalog.Find(n)!;

    [Fact]
    public void Valid_arguments_are_typed_and_neutralised()
    {
        var r = ToolArguments.Validate(Tool(AgentTools.ScheduleFollowUp), """{"dueInHours": 24, "note": "Call the principal​ about fees"}""");
        Assert.True(r.Ok); Assert.Equal(24, r.Int("dueInHours")); Assert.Equal("Call the principal about fees", r.Str("note"));
    }

    [Theory]
    [InlineData("""{"dueInHours": 0, "note": "x"}""", "out_of_range:dueInHours")]
    [InlineData("""{"dueInHours": 1000, "note": "x"}""", "out_of_range:dueInHours")]
    [InlineData("""{"dueInHours": "soon", "note": "x"}""", "wrong_type:dueInHours")]
    [InlineData("""{"dueInHours": 2}""", "missing_argument:note")]
    [InlineData("""{"dueInHours": 2, "note": "   "}""", "missing_argument:note")]
    [InlineData("""{"dueInHours": 2, "note": "x", "extra": 1}""", "unknown_argument:extra")]
    public void Invalid_arguments_are_rejected_with_a_reason(string json, string error)
    {
        var r = ToolArguments.Validate(Tool(AgentTools.ScheduleFollowUp), json);
        Assert.False(r.Ok); Assert.Contains(error, r.Errors); Assert.False(r.TenantArgument);
    }

    [Theory]
    [InlineData("""{"tenantId": "11111111-1111-1111-1111-111111111111", "dueInHours": 2, "note": "x"}""")]
    [InlineData("""{"tenant_id": "x", "dueInHours": 2, "note": "x"}""")]
    [InlineData("""{"leadId": "x", "dueInHours": 2, "note": "x"}""")]
    [InlineData("""{"conversationId": "x", "dueInHours": 2, "note": "x"}""")]
    [InlineData("""{"userId": "x", "dueInHours": 2, "note": "x"}""")]
    [InlineData("""{"sql": "select * from Leads", "dueInHours": 2, "note": "x"}""")]
    public void Arguments_that_try_to_choose_the_tenant_a_lead_or_run_a_query_are_forbidden_not_just_unknown(string json)
    {
        var r = ToolArguments.Validate(Tool(AgentTools.ScheduleFollowUp), json);
        Assert.False(r.Ok); Assert.True(r.TenantArgument); Assert.Contains(r.Errors, e => e.StartsWith("forbidden_argument"));
    }

    [Fact]
    public void Not_json_or_not_an_object_or_too_large_is_rejected()
    {
        Assert.Contains("arguments_not_json", ToolArguments.Validate(Tool(AgentTools.GetLeadProfile), "{nope").Errors);
        Assert.Contains("arguments_not_an_object", ToolArguments.Validate(Tool(AgentTools.GetLeadProfile), "[1]").Errors);
        Assert.Contains("arguments_too_large", ToolArguments.Validate(Tool(AgentTools.GetLeadProfile), "{\"a\":\"" + new string('x', 5000) + "\"}").Errors);
        Assert.True(ToolArguments.Validate(Tool(AgentTools.GetLeadProfile), null).Ok);   // a tool without parameters accepts nothing
        Assert.False(ToolArguments.Validate(Tool(AgentTools.GetLeadProfile), """{"id": 5}""").Ok);
    }

    [Fact]
    public void Enumerated_strings_must_match_and_are_canonicalised_and_maps_are_bounded()
    {
        var ok = ToolArguments.Validate(Tool(AgentTools.QualifyLead), """{"answers": {"student_count": 450, "role": "Principal"}, "fit": "GOOD"}""");
        Assert.True(ok.Ok); Assert.Equal("good", ok.Str("fit")); Assert.Equal("450", ok.Map("answers")!["student_count"]);
        Assert.False(ToolArguments.Validate(Tool(AgentTools.QualifyLead), """{"answers": {"a": "b"}, "fit": "excellent"}""").Ok);
        Assert.False(ToolArguments.Validate(Tool(AgentTools.QualifyLead), """{"answers": {"a": {"nested": 1}}}""").Ok);
        Assert.False(ToolArguments.Validate(Tool(AgentTools.QualifyLead), """{"answers": {}}""").Ok);
        var many = string.Join(",", Enumerable.Range(0, 30).Select(i => $"\"k{i}\":\"v\""));
        Assert.False(ToolArguments.Validate(Tool(AgentTools.QualifyLead), "{\"answers\":{" + many + "}}").Ok);
    }

    [Fact]
    public void A_discount_must_be_a_number_in_range()
    {
        Assert.True(ToolArguments.Validate(Tool(AgentTools.CreatePaymentRequest), """{"plan":"Standard","discountPercent":10}""").Ok);
        Assert.False(ToolArguments.Validate(Tool(AgentTools.CreatePaymentRequest), """{"plan":"Standard","discountPercent":150}""").Ok);
        Assert.False(ToolArguments.Validate(Tool(AgentTools.CreatePaymentRequest), """{"plan":"Standard","discountPercent":-5}""").Ok);
    }
}

public class AgentToolCatalogTests
{
    [Fact]
    public void Names_resolve_exactly_and_through_the_phase_7_aliases_only()
    {
        Assert.Equal(AgentTools.TransferToHuman, AgentToolCatalog.Find("escalate_to_human")!.Name);
        Assert.Equal(AgentTools.AddLeadNote, AgentToolCatalog.Find("add_lead_note")!.Name);
        Assert.Null(AgentToolCatalog.Find("gettransfertohuman")); Assert.Null(AgentToolCatalog.Find("")); Assert.Null(AgentToolCatalog.Find(null)); Assert.Null(AgentToolCatalog.Find("getproductinformation"));
        Assert.Null(AgentToolCatalog.Find("ActivateSubscription")); Assert.Null(AgentToolCatalog.Find("RunSql"));
    }

    [Fact]
    public void An_agent_gets_its_enabled_tools_plus_the_two_safety_tools_and_nothing_else()
    {
        var tools = AgentToolCatalog.AllowedFor([AgentTools.GetPricingPlans]).Select(t => t.Name).Order().ToArray();
        Assert.Equal(new[] { AgentTools.GetPricingPlans, AgentTools.StopMarketingMessages, AgentTools.TransferToHuman }.Order().ToArray(), tools);
        Assert.DoesNotContain(AgentTools.CreatePaymentRequest, AgentToolCatalog.AllowedFor([]).Select(t => t.Name));
        Assert.Equal(["StopMarketingMessages", "TransferToHuman"], AgentToolCatalog.AllowedFor(["NotATool"]).Select(t => t.Name).Order());
    }

    [Fact]
    public void The_prompt_description_lists_each_tool_with_its_parameters_and_no_internal_ids()
    {
        var text = AgentToolCatalog.DescribeForPrompt(AgentToolCatalog.All);
        Assert.Contains("BookDemo(slotId:string, notes?:string)", text); Assert.Contains("GetLeadProfile()", text);
        Assert.DoesNotContain("tenant", text, StringComparison.OrdinalIgnoreCase);
    }
}

public class AgentOutputParsingTests
{
    [Fact]
    public void Tool_calls_may_come_with_an_empty_answer()
    {
        var o = ModelOutput.ParseAgent("""{"answer":"","tool_calls":[{"name":"GetPricingPlans","arguments":{"product":"ERP"}}],"confidence":0.9}""")!;
        Assert.Equal("GetPricingPlans", Assert.Single(o.ToolCalls).Name); Assert.Contains("ERP", o.ToolCalls[0].ArgumentsJson);
    }

    [Fact]
    public void The_legacy_actions_key_is_read_as_tool_calls_and_non_object_arguments_become_empty()
    {
        var o = ModelOutput.ParseAgent("""{"answer":"Hi","actions":[{"name":"escalate_to_human","arguments":"oops"}]}""")!;
        Assert.Equal("{}", Assert.Single(o.ToolCalls).ArgumentsJson);
    }

    [Theory]
    [InlineData("")] [InlineData("not json")] [InlineData("{}")] [InlineData("""{"confidence":0.5}""")]
    public void Nothing_usable_is_null(string text) => Assert.Null(ModelOutput.ParseAgent(text));

    [Fact]
    public void Citations_are_normalised_and_calls_are_capped()
    {
        var calls = string.Join(",", Enumerable.Range(0, 10).Select(i => $"{{\"name\":\"GetLeadProfile{i}\"}}"));
        var o = ModelOutput.ParseAgent("{\"answer\":\"x\",\"citations\":[\"t1\",\"[K2]\",\"T1\"],\"tool_calls\":[" + calls + "]}")!;
        Assert.Equal(["T1", "K2"], o.Citations); Assert.Equal(6, o.ToolCalls.Count);
    }
}

public class AgentValidationTests
{
    private static IReadOnlyList<ValidationIssue> Check(string answer, IReadOnlyDictionary<string, string> ctx, string[] cites, params string[] verified) =>
        ResponseValidator.Validate(new ValidationInput(answer, cites, ctx, [], [], null, false, VerifiedCompletions: verified.ToHashSet()));

    private static readonly Dictionary<string, string> Plans = new() { ["T1"] = """{"plans":[{"plan":"Standard","price":500.00,"currency":"USD"}]}""" };

    [Fact]
    public void A_price_quoted_from_a_tool_result_is_allowed_and_any_other_is_not()
    {
        Assert.Empty(Check("The Standard plan costs 500 USD.", Plans, ["T1"]));
        Assert.Contains(Check("The Standard plan costs 450 USD.", Plans, ["T1"]), i => i.Code == ResponseValidator.UnsupportedAmount);
        Assert.Contains(Check("The Standard plan costs 500 USD.", Plans, []), i => i.Code == ResponseValidator.UnsupportedAmount);   // not cited
    }

    [Fact]
    public void A_booking_may_be_claimed_only_when_the_book_tool_really_succeeded_in_this_turn()
    {
        Assert.Contains(Check("Your demo has been booked for Monday.", Plans, ["T1"]), i => i.Code == ResponseValidator.CompletionClaim);
        Assert.Empty(Check("Your demo has been booked for Monday.", Plans, ["T1"], "demo"));
        Assert.Empty(Check("I've booked the demo for you.", Plans, ["T1"], "demo"));
    }

    [Fact]
    public void A_verified_demo_never_licenses_claims_about_payments_trials_or_accounts()
    {
        Assert.Contains(Check("Your trial account has been created.", Plans, ["T1"], "demo"), i => i.Code == ResponseValidator.CompletionClaim);
        Assert.Empty(Check("Your trial account has been created.", Plans, ["T1"], "trial"));
        Assert.Contains(Check("Your payment has been received.", Plans, ["T1"], "demo", "trial"), i => i.Code == ResponseValidator.CompletionClaim);
        Assert.Contains(Check("Payment received, your account is active.", Plans, ["T1"], "demo", "trial"), i => i.Code == ResponseValidator.CompletionClaim);
        Assert.Contains(Check("Your subscription has been activated.", Plans, ["T1"], "demo", "trial"), i => i.Code == ResponseValidator.CompletionClaim);
    }
}

public class MultilingualSensitivityTests
{
    [Theory]
    [InlineData("Mujhe refund chahiye", "payment_dispute")]
    [InlineData("mere paise wapas karo", "payment_dispute")]
    [InlineData("میرے پیسے واپس چاہئیں", "payment_dispute")]
    [InlineData("mujhe kisi insan se baat karni hai", "human_requested")]
    [InlineData("manager se baat karni hai", "human_requested")]
    [InlineData("مجھے کسی سے بات کرنی ہے", "human_requested")]
    [InlineData("yeh dhoka hai, shikayat karunga", "complaint")]
    [InlineData("میں شکایت کروں گا", "complaint")]
    [InlineData("main wakeel se baat karunga", "legal_or_privacy")]
    public void Urdu_and_roman_urdu_requests_for_a_person_money_or_complaints_go_to_a_person(string text, string reason) => Assert.Contains(reason, SensitivityRules.Detect(text));

    [Theory]
    [InlineData("School ERP ki fees kitni hai?")]
    [InlineData("Hamare school mein 400 students hain")]
    [InlineData("مجھے ڈیمو دیکھنا ہے")]
    public void Ordinary_questions_in_those_languages_are_not_escalated(string text) => Assert.Empty(SensitivityRules.Detect(text));
}

public class AgentDomainTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_plan_cannot_be_confirmed_without_a_price_and_prices_are_bounded()
    {
        Assert.Throws<DomainException>(() => PricingPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "Basic", null, "USD", null, null, null, confirmed: true, false, Now, null));
        Assert.Throws<DomainException>(() => PricingPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "Basic", -1, "USD", null, null, null, false, false, Now, null));
        Assert.Throws<DomainException>(() => PricingPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "Basic", 10, "US", null, null, null, false, false, Now, null));
        Assert.Throws<DomainException>(() => PricingPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "Basic", 10, "USD", null, null, 120, false, false, Now, null));
        Assert.True(PricingPlan.Create(Guid.NewGuid(), Guid.NewGuid(), "Basic", 10, "usd", null, null, null, true, false, Now, null).IsConfirmed);
    }

    [Fact]
    public void An_approval_can_be_decided_once_and_never_after_it_expired()
    {
        var a = ApprovalRequest.Create(Guid.NewGuid(), null, Guid.NewGuid(), null, "CreatePaymentRequest", "payment_request", "s", "{}", "k", Now, TimeSpan.FromHours(1));
        Assert.Throws<DomainException>(() => a.Approve(null, null, Now.AddHours(2)));
        var b = ApprovalRequest.Create(Guid.NewGuid(), null, Guid.NewGuid(), null, "CreatePaymentRequest", "payment_request", "s", "{}", "k", Now, TimeSpan.FromHours(1));
        b.Approve(Guid.NewGuid(), "ok", Now.AddMinutes(5));
        Assert.Equal(ApprovalStatus.Approved, b.Status);
        Assert.Throws<DomainException>(() => b.Reject(null, null, Now));
        Assert.Throws<DomainException>(() => b.Approve(null, null, Now));
    }

    [Fact]
    public void Switching_an_agent_off_also_removes_default_and_automatic_replies()
    {
        var a = SalesAgent.Create(Guid.NewGuid(), "A", "Do the job", true, false, Now, null);
        a.SetAutoReply(true, Now, null); Assert.True(a.AutoReplyEnabled);
        a.SetActive(false, Now, null);
        Assert.False(a.IsDefault); Assert.False(a.AutoReplyEnabled);
        Assert.Throws<DomainException>(() => a.SetAutoReply(true, Now, null)); Assert.Throws<DomainException>(() => a.SetDefault(true, Now, null));
        Assert.False(SalesAgent.Create(Guid.NewGuid(), "B", "x", false, false, Now, null).AutoReplyEnabled);   // opt-in: off by default
    }

    [Fact]
    public void Qualification_questions_validate_keys_and_choices()
    {
        Assert.Throws<DomainException>(() => AgentQualificationQuestion.Create(Guid.NewGuid(), Guid.NewGuid(), "Bad Key", "q", AnswerType.Text, null, false, 1));
        Assert.Throws<DomainException>(() => AgentQualificationQuestion.Create(Guid.NewGuid(), Guid.NewGuid(), "k", "q", AnswerType.Choice, ["only one"], false, 1));
        Assert.Equal(["a", "b"], AgentQualificationQuestion.Create(Guid.NewGuid(), Guid.NewGuid(), "K", "q", AnswerType.Choice, ["a", "b", "a"], true, 1).Choices);
    }

    [Fact]
    public void The_ai_epoch_changes_with_every_change_of_who_answers_and_escalation_records_why()
    {
        var c = Conversation.Create(Guid.NewGuid(), Guid.NewGuid(), "923001234567@c.us", "+923001234567", "A", Now);
        Assert.Equal(0, c.AiEpoch);
        c.SetAiMode(AiMode.Active, Now); Assert.Equal(1, c.AiEpoch);
        c.SetAiMode(AiMode.Active, Now); Assert.Equal(1, c.AiEpoch);          // no change, no new epoch
        c.Escalate("customer upset", Now); Assert.Equal(2, c.AiEpoch); Assert.Equal(AiMode.HumanControl, c.AiMode); Assert.Equal("customer upset", c.EscalationReason);
        c.SetAiMode(AiMode.Active, Now); Assert.Equal(3, c.AiEpoch); Assert.Null(c.EscalationReason);
    }

    [Fact]
    public void New_permissions_exist_with_least_privilege_defaults()
    {
        string[] codes = [PermissionCodes.AgentsView, PermissionCodes.CatalogView, PermissionCodes.CatalogManage, PermissionCodes.ApprovalsView, PermissionCodes.ApprovalsDecide];
        Assert.All(codes, c => Assert.True(PermissionCatalog.Exists(c)));
        var role = (string n) => SystemRoles.All.Single(r => r.Name == n).Permissions;
        Assert.All(codes, c => Assert.Contains(c, role(SystemRoles.TenantAdministrator)));
        Assert.Contains(PermissionCodes.ApprovalsDecide, role(SystemRoles.SalesManager));
        foreach (var n in new[] { SystemRoles.SalesAgent, SystemRoles.TenantStaff, SystemRoles.MarketingManager, SystemRoles.ReadOnly })
        { Assert.DoesNotContain(PermissionCodes.ApprovalsDecide, role(n)); Assert.DoesNotContain(PermissionCodes.CatalogManage, role(n)); Assert.DoesNotContain(PermissionCodes.AgentsManage, role(n)); }
    }
}
