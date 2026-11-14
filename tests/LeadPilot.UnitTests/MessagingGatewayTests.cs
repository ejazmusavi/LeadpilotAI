using System.Net;
using System.Text;
using System.Text.Json;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;
using LeadPilot.Infrastructure.Discovery.Website;
using LeadPilot.Infrastructure.Messaging.Waha;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LeadPilot.UnitTests;

public class WahaSignatureTests
{
    // The worked example from https://waha.devlike.pro/docs/how-to/events/ (HMAC authentication)
    private const string DocBody = "{\"event\":\"message\",\"session\":\"default\",\"engine\":\"WEBJS\"}";
    private const string DocKey = "my-secret-key";
    private const string DocHmac = "208f8a55dde9e05519e898b10b89bf0d0b3b0fdf11fdbf09b6b90476301b98d8097c462b2b17a6ce93b6b47a136cf2e78a33a63f6752c2c1631777076153fa89";

    [Fact] public void Computes_the_documented_example_vector() => Assert.Equal(DocHmac, WahaSignature.Compute(DocKey, Encoding.UTF8.GetBytes(DocBody)));
    [Fact] public void A_valid_signature_verifies() => Assert.True(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody), DocHmac));
    [Fact] public void Hex_case_does_not_matter() => Assert.True(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody), DocHmac.ToUpperInvariant()));
    [Fact] public void A_signature_surrounded_by_whitespace_is_trimmed() => Assert.True(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody), "  " + DocHmac + " "));

    [Fact]
    public void A_changed_body_or_secret_fails()
    {
        Assert.False(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody + " "), DocHmac));
        Assert.False(WahaSignature.Verify(DocKey + "x", Encoding.UTF8.GetBytes(DocBody), DocHmac));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("abc")] [InlineData("zz")]
    public void Missing_short_or_garbled_signatures_fail(string? sig) => Assert.False(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody), sig));

    [Fact] public void A_correct_length_but_non_hex_signature_fails_without_throwing() => Assert.False(WahaSignature.Verify(DocKey, Encoding.UTF8.GetBytes(DocBody), new string('g', 128)));
    [Fact] public void An_empty_secret_never_verifies() => Assert.False(WahaSignature.Verify("", Encoding.UTF8.GetBytes(DocBody), WahaSignature.Compute("", Encoding.UTF8.GetBytes(DocBody))));

    [Fact]
    public void A_signature_wrong_only_in_the_first_or_the_last_byte_is_rejected_alike()
    {
        var body = Encoding.UTF8.GetBytes(DocBody);
        var first = (DocHmac[0] == '0' ? '1' : '0') + DocHmac[1..];
        var last = DocHmac[..^1] + (DocHmac[^1] == '0' ? '1' : '0');
        Assert.False(WahaSignature.Verify(DocKey, body, first));
        Assert.False(WahaSignature.Verify(DocKey, body, last));
    }

    [Fact]
    public void The_comparison_is_constant_time_by_construction()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "LeadPilot.Infrastructure", "Messaging", "Waha", "WahaSignature.cs"));
        Assert.Contains("CryptographicOperations.FixedTimeEquals", src);
        Assert.DoesNotContain("SequenceEqual", src);
        Assert.DoesNotContain("string.Equals", src);
        Assert.DoesNotContain("== sig", src);
    }
}

public class WahaWebhookProcessingTests
{
    private const string Secret = "unit-test-webhook-secret";
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private static WahaGateway Gateway(int toleranceMinutes = 15)
    {
        var o = Options.Create(new WahaOptions { WebhookToleranceMinutes = toleranceMinutes });
        var clients = new WahaHttpClientFactory(new SystemDnsResolver(), new WahaAddressPolicy(o, new PublicOnlyAddressPolicy()), o);
        return new WahaGateway(clients, new WahaCircuitBreaker(o, TimeProvider.System), o, NullLogger<WahaGateway>.Instance);
    }

    private static WebhookRequest Signed(string json, DateTime? sentAt = null, string? algorithm = "sha512", string? secret = Secret, bool omitSignature = false, bool omitTimestamp = false)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!omitSignature) h["X-Webhook-Hmac"] = WahaSignature.Compute(secret!, body);
        if (algorithm is not null) h["X-Webhook-Hmac-Algorithm"] = algorithm;
        if (!omitTimestamp) h["X-Webhook-Timestamp"] = new DateTimeOffset(sentAt ?? Now).ToUnixTimeMilliseconds().ToString();
        return new WebhookRequest(body, h);
    }

    private const string Msg = """{"id":"evt_01","timestamp":1741249702485,"event":"message","session":"s1","payload":{"id":"false_923001234567@c.us_AAAA1111BBBB2222","timestamp":1741249702,"from":"923001234567@c.us","fromMe":false,"body":"hello","hasMedia":false}}""";

    [Fact] public async Task A_correctly_signed_fresh_message_is_accepted_and_normalised()
    {
        var r = await Gateway().ProcessWebhookAsync(Signed(Msg), Secret, Now);
        Assert.True(r.IsAccepted);
        var m = Assert.IsType<InboundMessageEvent>(r.Event);
        Assert.Equal("evt_01", m.ExternalEventId); Assert.Equal("s1", m.AccountId); Assert.Equal("+923001234567", m.PeerPhoneE164); Assert.Equal("hello", m.Body);
    }

    [Fact] public async Task A_missing_signature_is_rejected() => Assert.Equal(WebhookRejection.MissingSignature, (await Gateway().ProcessWebhookAsync(Signed(Msg, omitSignature: true), Secret, Now)).Rejection);
    [Fact] public async Task A_signature_made_with_another_secret_is_rejected() => Assert.Equal(WebhookRejection.InvalidSignature, (await Gateway().ProcessWebhookAsync(Signed(Msg, secret: "someone-else"), Secret, Now)).Rejection);
    [Fact] public async Task A_tampered_body_is_rejected()
    {
        var req = Signed(Msg);
        var tampered = new WebhookRequest(Encoding.UTF8.GetBytes(Msg.Replace("hello", "HELLO")), req.Headers);
        Assert.Equal(WebhookRejection.InvalidSignature, (await Gateway().ProcessWebhookAsync(tampered, Secret, Now)).Rejection);
    }
    [Fact] public async Task An_unsupported_algorithm_header_is_rejected() => Assert.Equal(WebhookRejection.UnsupportedAlgorithm, (await Gateway().ProcessWebhookAsync(Signed(Msg, algorithm: "sha1"), Secret, Now)).Rejection);
    [Fact] public async Task A_missing_algorithm_header_assumes_the_documented_sha512() => Assert.True((await Gateway().ProcessWebhookAsync(Signed(Msg, algorithm: null), Secret, Now)).IsAccepted);
    [Fact] public async Task A_stale_timestamp_is_rejected() => Assert.Equal(WebhookRejection.StaleTimestamp, (await Gateway().ProcessWebhookAsync(Signed(Msg, sentAt: Now.AddMinutes(-16)), Secret, Now)).Rejection);
    [Fact] public async Task A_far_future_timestamp_is_rejected() => Assert.Equal(WebhookRejection.StaleTimestamp, (await Gateway().ProcessWebhookAsync(Signed(Msg, sentAt: Now.AddHours(2)), Secret, Now)).Rejection);
    [Fact] public async Task A_timestamp_just_inside_the_tolerance_is_accepted() => Assert.True((await Gateway().ProcessWebhookAsync(Signed(Msg, sentAt: Now.AddMinutes(-14)), Secret, Now)).IsAccepted);
    [Fact] public async Task An_unreadable_timestamp_is_rejected()
    {
        var req = Signed(Msg); ((Dictionary<string, string>)req.Headers)["X-Webhook-Timestamp"] = "yesterday";
        Assert.Equal(WebhookRejection.StaleTimestamp, (await Gateway().ProcessWebhookAsync(req, Secret, Now)).Rejection);
    }
    [Fact] public async Task A_missing_timestamp_is_tolerated_because_the_signature_is_what_authenticates() => Assert.True((await Gateway().ProcessWebhookAsync(Signed(Msg, omitTimestamp: true), Secret, Now)).IsAccepted);

    [Fact]
    public async Task A_replayed_delivery_verifies_again_and_carries_the_same_event_id_so_storage_can_drop_it()
    {
        var req = Signed(Msg);
        var a = await Gateway().ProcessWebhookAsync(req, Secret, Now); var b = await Gateway().ProcessWebhookAsync(req, Secret, Now);
        Assert.True(a.IsAccepted && b.IsAccepted);
        Assert.Equal(a.Event!.ExternalEventId, b.Event!.ExternalEventId);   // idempotency, not rejection, is the replay defence (the scheme signs no nonce)
    }

    [Fact] public async Task A_signed_but_malformed_body_is_a_malformed_payload_not_a_crash()
        => Assert.Equal(WebhookRejection.MalformedPayload, (await Gateway().ProcessWebhookAsync(Signed("{not json"), Secret, Now)).Rejection);
}

public class WahaNormalizerTests
{
    private static GatewayEvent N(string json) => Assert.IsAssignableFrom<GatewayEvent>(WahaWebhookNormalizer.Normalize(Encoding.UTF8.GetBytes(json)).Event);
    private static WebhookProcessingResult R(string json) => WahaWebhookNormalizer.Normalize(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void An_incoming_text_message_maps_every_documented_field()
    {
        var e = Assert.IsType<InboundMessageEvent>(N("""{"id":"evt_a","timestamp":1,"event":"message","session":"default","payload":{"id":"false_923001234567@c.us_ABC","timestamp":1667561485,"from":"923001234567@c.us","fromMe":false,"to":"1@c.us","body":"Hi there!","hasMedia":false,"ack":1,"_data":{"notifyName":"Ali"}}}"""));
        Assert.Equal("evt_a", e.ExternalEventId); Assert.Equal("default", e.AccountId); Assert.Equal("false_923001234567@c.us_ABC", e.ExternalMessageId); Assert.Equal("923001234567@c.us", e.ChatId);
        Assert.Equal("+923001234567", e.PeerPhoneE164); Assert.Equal("Ali", e.PeerName); Assert.Equal("Hi there!", e.Body); Assert.Equal(DateTime.UnixEpoch.AddSeconds(1667561485), e.OccurredAt); Assert.Empty(e.Attachments);
    }

    [Fact] public void message_any_with_fromMe_false_is_also_an_inbound_message() => Assert.IsType<InboundMessageEvent>(N("""{"event":"message.any","session":"s","payload":{"id":"false_1@c.us_X","from":"923001234567@c.us","fromMe":false,"body":"x","timestamp":10}}"""));
    [Fact] public void Our_own_messages_are_never_inbound() => Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message.any","session":"s","payload":{"id":"true_1@c.us_X","from":"923001234567@c.us","fromMe":true,"body":"x","source":"api"}}"""));
    [Fact] public void A_message_event_without_the_fromMe_flag_is_not_trusted_as_inbound() => Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_1@c.us_X","from":"923001234567@c.us","body":"x"}}"""));
    [Fact] public void Group_chats_are_ignored() => Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_1@g.us_X","from":"1203630@g.us","fromMe":false,"body":"x"}}"""));
    [Fact] public void Channels_and_status_broadcasts_are_ignored()
    {
        Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_1@newsletter_X","from":"123@newsletter","fromMe":false,"body":"x"}}"""));
        Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_status@broadcast_X","from":"status@broadcast","fromMe":false,"body":"x"}}"""));
    }

    [Fact]
    public void A_hidden_lid_sender_has_no_phone_number_unless_the_engine_gives_one()
    {
        var plain = Assert.IsType<InboundMessageEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_99@lid_X","from":"99887766@lid","fromMe":false,"body":"x"}}"""));
        Assert.Null(plain.PeerPhoneE164);
        var alt = Assert.IsType<InboundMessageEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_99@lid_Y","from":"99887766@lid","fromMe":false,"body":"x","_data":{"key":{"remoteJidAlt":"923001234567@s.whatsapp.net"}}}}"""));
        Assert.Equal("+923001234567", alt.PeerPhoneE164);
        var garbage = Assert.IsType<InboundMessageEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_99@lid_Z","from":"99887766@lid","fromMe":false,"body":"x","_data":{"key":{"remoteJidAlt":"not a number"}}}}"""));
        Assert.Null(garbage.PeerPhoneE164);
    }

    [Fact]
    public void Media_is_described_but_never_fetched()
    {
        var e = Assert.IsType<InboundMessageEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_1@c.us_X","from":"923001234567@c.us","fromMe":false,"body":"","hasMedia":true,"media":{"url":"http://waha/api/files/x.jpg","mimetype":"image/jpeg","filename":"x.jpg"}}}"""));
        var a = Assert.Single(e.Attachments); Assert.Equal("image/jpeg", a.MimeType); Assert.True(a.HasUrl); Assert.Equal("x.jpg", a.FileName);
    }

    [Fact] public void Media_without_a_url_is_still_described() => Assert.Single(Assert.IsType<InboundMessageEvent>(N("""{"event":"message","session":"s","payload":{"id":"false_1@c.us_X","from":"923001234567@c.us","fromMe":false,"hasMedia":true,"media":{"url":null,"mimetype":"audio/ogg"}}}""")).Attachments);

    [Theory]
    [InlineData(-1, null, true)] [InlineData(0, null, false)] [InlineData(1, MessageStatus.Sent, false)] [InlineData(2, MessageStatus.Delivered, false)] [InlineData(3, MessageStatus.Read, false)] [InlineData(4, MessageStatus.Read, false)]
    public void Every_documented_ack_value_maps(int ack, MessageStatus? expected, bool error)
    {
        var e = Assert.IsType<MessageAckEvent>(N($$$"""{"event":"message.ack","session":"s","payload":{"id":"true_923001234567@c.us_AAA","from":"923001234567@c.us","participant":null,"fromMe":true,"ack":{{{ack}}},"ackName":"X"}}"""));
        Assert.Equal(expected, e.Delivery); Assert.Equal(error, e.IsError); Assert.Equal("true_923001234567@c.us_AAA", e.ExternalMessageId); Assert.Equal("923001234567@c.us", e.ChatId);
    }

    [Fact] public void An_ack_without_a_from_uses_the_chat_inside_the_message_id() => Assert.Equal("923001234567@c.us", Assert.IsType<MessageAckEvent>(N("""{"event":"message.ack","session":"s","payload":{"id":"true_923001234567@c.us_AAA","fromMe":true,"ack":2}}""")).ChatId);
    [Fact] public void Acks_of_messages_we_did_not_send_or_of_groups_are_ignored()
    {
        Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message.ack","session":"s","payload":{"id":"false_1@c.us_A","from":"1@c.us","fromMe":false,"ack":3}}"""));
        Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message.ack","session":"s","payload":{"id":"true_1@g.us_A_9@lid","from":"1@g.us","fromMe":true,"ack":3}}"""));
        Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message.ack","session":"s","payload":{"id":"true_1@c.us_A","from":"1@c.us","fromMe":true,"ack":"read"}}"""));
    }

    [Theory]
    [InlineData("STOPPED", GatewaySessionStatus.Stopped)] [InlineData("STARTING", GatewaySessionStatus.Starting)] [InlineData("SCAN_QR_CODE", GatewaySessionStatus.ScanQrCode)] [InlineData("WORKING", GatewaySessionStatus.Working)]
    [InlineData("FAILED", GatewaySessionStatus.Failed)] [InlineData("PASSKEY_REQUIRED", GatewaySessionStatus.ScanQrCode)] [InlineData("PASSKEY_CONFIRMATION_REQUIRED", GatewaySessionStatus.ScanQrCode)] [InlineData("SOMETHING_NEW", GatewaySessionStatus.Unknown)]
    public void Every_documented_session_status_maps(string raw, GatewaySessionStatus expected)
    {
        var e = Assert.IsType<SessionStatusEvent>(N($$$"""{"event":"session.status","session":"default","me":{"id":"923001234567@c.us","pushName":"WAHA"},"payload":{"status":"{{{raw}}}","statuses":[],"data":null}}"""));
        Assert.Equal(expected, e.Status); Assert.Equal("+923001234567", e.PhoneE164); Assert.Equal("WAHA", e.PushName); Assert.False(e.NewContactsRestricted);
    }

    [Fact]
    public void A_reachout_timelock_and_message_capping_are_reported_as_restrictions()
    {
        var tl = Assert.IsType<SessionStatusEvent>(N("""{"event":"session.status","session":"s","payload":{"status":"WORKING","data":{"reachoutTimelock":{"enforcementType":"RESTRICT_ALL_COMPANIONS","isActive":true,"timeEnforcementEnds":1784477333}}}}"""));
        Assert.True(tl.NewContactsRestricted); Assert.Equal("reachout_timelock", tl.RestrictionKind); Assert.Equal(DateTime.UnixEpoch.AddSeconds(1784477333), tl.RestrictedUntilUtc);
        var cap = Assert.IsType<SessionStatusEvent>(N("""{"event":"session.status","session":"s","payload":{"status":"WORKING","data":{"messageCapping":{"cappingStatus":"CAPPED","cycleEnd":1785553199}}}}"""));
        Assert.True(cap.NewContactsRestricted); Assert.Equal("message_capping", cap.RestrictionKind);
        var warn = Assert.IsType<SessionStatusEvent>(N("""{"event":"session.status","session":"s","payload":{"status":"WORKING","data":{"messageCapping":{"cappingStatus":"FIRST_WARNING"}}}}"""));
        Assert.False(warn.NewContactsRestricted);
        var lifted = Assert.IsType<SessionStatusEvent>(N("""{"event":"session.status","session":"s","payload":{"status":"WORKING","data":{"reachoutTimelock":{"isActive":false}}}}"""));
        Assert.False(lifted.NewContactsRestricted);
    }

    [Theory]
    [InlineData("message.reaction")] [InlineData("message.edited")] [InlineData("message.revoked")] [InlineData("group.v2.join")] [InlineData("presence.update")] [InlineData("engine.event")] [InlineData("label.upsert")] [InlineData("call.received")]
    public void Valid_events_we_do_not_act_on_are_ignored_not_rejected(string type) => Assert.IsType<IgnoredGatewayEvent>(N($$$"""{"event":"{{{type}}}","session":"s","payload":{"id":"x"}}"""));

    [Theory]
    [InlineData("")] [InlineData("not json")] [InlineData("[]")] [InlineData("\"text\"")] [InlineData("{}")] [InlineData("""{"event":"message"}""")] [InlineData("""{"session":"s"}""")]
    [InlineData("""{"event":"","session":"s"}""")] [InlineData("""{"event":"message","session":""}""")]
    public void Malformed_payloads_are_rejected_without_throwing(string json) => Assert.Equal(WebhookRejection.MalformedPayload, R(json).Rejection);

    [Fact] public void An_oversized_body_is_rejected() => Assert.Equal(WebhookRejection.MalformedPayload, WahaWebhookNormalizer.Normalize(new byte[WahaWebhookNormalizer.MaxBodyBytes + 1]).Rejection);
    [Fact] public void Absurdly_deep_json_is_rejected_not_a_stack_overflow() => Assert.Equal(WebhookRejection.MalformedPayload, R(new string('[', 5000) + new string(']', 5000)).Rejection);
    [Fact] public void A_payload_of_the_wrong_type_is_ignored_safely() => Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":"oops"}"""));
    [Fact] public void Wrongly_typed_fields_never_throw() => Assert.IsType<IgnoredGatewayEvent>(N("""{"event":"message","session":"s","payload":{"id":5,"from":["x"],"fromMe":"false","body":{}}}"""));

    [Fact] public void The_gateway_event_id_is_used_when_present_and_a_deterministic_hash_otherwise()
    {
        var withId = N("""{"id":"evt_xyz","event":"message.reaction","session":"s","payload":{}}""");
        Assert.Equal("evt_xyz", withId.ExternalEventId);
        var body = """{"event":"message.reaction","session":"s","payload":{"a":1}}""";
        var h1 = N(body).ExternalEventId; var h2 = N(body).ExternalEventId; var h3 = N(body.Replace("\"a\":1", "\"a\":2")).ExternalEventId;
        Assert.StartsWith("sha256:", h1); Assert.Equal(h1, h2); Assert.NotEqual(h1, h3);
    }

    [Fact] public void Unix_timestamps_are_seconds_for_messages_and_milliseconds_for_the_envelope()
    {
        Assert.Equal(new DateTime(2025, 3, 6, 8, 28, 22, DateTimeKind.Utc), WahaWebhookNormalizer.FromUnixSeconds(1741249702));
        Assert.Equal(new DateTime(2025, 3, 6, 8, 28, 22, 485, DateTimeKind.Utc), WahaWebhookNormalizer.FromUnixMs(1741249702485));
        Assert.Null(WahaWebhookNormalizer.FromUnixSeconds(0)); Assert.Null(WahaWebhookNormalizer.FromUnixSeconds(-5)); Assert.Null(WahaWebhookNormalizer.FromUnixSeconds(1e15));
    }
}

public class WahaErrorClassificationTests
{
    [Theory]
    [InlineData(401, GatewayFailureKind.Unauthorized)] [InlineData(403, GatewayFailureKind.Unauthorized)] [InlineData(404, GatewayFailureKind.NotFound)] [InlineData(429, GatewayFailureKind.RateLimited)]
    [InlineData(400, GatewayFailureKind.Rejected)] [InlineData(422, GatewayFailureKind.Rejected)] [InlineData(500, GatewayFailureKind.OutcomeUnknown)] [InlineData(502, GatewayFailureKind.OutcomeUnknown)]
    [InlineData(503, GatewayFailureKind.OutcomeUnknown)] [InlineData(504, GatewayFailureKind.OutcomeUnknown)] [InlineData(408, GatewayFailureKind.OutcomeUnknown)]
    public void Http_status_decides_the_kind(int status, GatewayFailureKind kind) => Assert.Equal(kind, WahaGateway.Classify(status, "").Kind);

    [Theory]
    [InlineData(500, "server returned error 463")] [InlineData(422, "Error: server returned error 475")] [InlineData(400, "reachout timelock active")] [InlineData(500, "message capping reached")]
    public void Whatsapps_463_and_475_refusals_mean_nothing_was_sent_whatever_the_status(int status, string body)
    {
        var ex = WahaGateway.Classify(status, body);
        Assert.Equal(GatewayFailureKind.NewContactRestricted, ex.Kind); Assert.False(ex.OutcomeUnknown); Assert.False(ex.SafeToRetry);
    }

    [Fact] public void Only_unreachable_and_rate_limited_are_safe_to_retry_and_only_unknown_is_dangerous()
    {
        foreach (var k in Enum.GetValues<GatewayFailureKind>())
        {
            var ex = new GatewayException(k, "x");
            Assert.Equal(k is GatewayFailureKind.Unreachable or GatewayFailureKind.RateLimited, ex.SafeToRetry);
            Assert.Equal(k == GatewayFailureKind.OutcomeUnknown, ex.OutcomeUnknown);
            Assert.False(ex.SafeToRetry && ex.OutcomeUnknown);
        }
    }

    [Fact] public void Failures_that_blame_the_account_count_toward_auto_pause()
    {
        Assert.True(new GatewayException(GatewayFailureKind.Unauthorized, "x").CountsAgainstAccount);
        Assert.True(new GatewayException(GatewayFailureKind.OutcomeUnknown, "x").CountsAgainstAccount);
        Assert.False(new GatewayException(GatewayFailureKind.Rejected, "x").CountsAgainstAccount);
        Assert.False(new GatewayException(GatewayFailureKind.NewContactRestricted, "x").CountsAgainstAccount);
    }

    [Fact] public void Error_text_from_the_gateway_is_sanitised_and_bounded()
    {
        var ex = WahaGateway.Classify(400, "bad\r\nrequest\u0000" + new string('x', 1000));
        Assert.DoesNotContain('\n', ex.Message); Assert.DoesNotContain('\u0000', ex.Message); Assert.True(ex.Message.Length < 400);
    }

    [Theory]
    [InlineData("""{"id":"true_923001234567@c.us_ABCDEF1234567890"}""", "true_923001234567@c.us_ABCDEF1234567890")]
    [InlineData("""{"id":{"_serialized":"true_923001234567@c.us_ABCDEF12","id":"ABCDEF12"}}""", "true_923001234567@c.us_ABCDEF12")]
    [InlineData("""{"id":{"id":"ABCDEF1234567890"}}""", "true_923001234567@c.us_ABCDEF1234567890")]
    [InlineData("""{"_data":{"id":{"_serialized":"true_923001234567@c.us_9999AAAA"}}}""", "true_923001234567@c.us_9999AAAA")]
    [InlineData("""{"_data":{"key":{"id":"3EB0965B123D06E0B707"}}}""", "true_923001234567@c.us_3EB0965B123D06E0B707")]
    [InlineData("{}", null)] [InlineData("""{"id":""}""", null)] [InlineData("""{"id":"has space"}""", null)] [InlineData("""{"id":{"id":"zz"}}""", null)] [InlineData("[]", null)] [InlineData("""{"id":42}""", null)]
    public void The_message_id_is_found_in_every_plausible_response_shape_or_not_at_all(string json, string? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, WahaGateway.ExtractMessageId(doc.RootElement, "923001234567@c.us"));
    }
}

public class WahaEndpointAndPolicyTests
{
    [Theory]
    [InlineData("https://waha.example.com", "https://waha.example.com")] [InlineData("https://waha.example.com/", "https://waha.example.com")] [InlineData("http://10.0.0.5:3000/", "http://10.0.0.5:3000")]
    [InlineData("  https://waha.example.com/waha/  ", "https://waha.example.com/waha")]
    public void Valid_base_urls_are_normalised(string raw, string expected) { Assert.True(WahaEndpoint.TryNormalize(raw, out var url, out _)); Assert.Equal(expected, url); }

    [Theory]
    [InlineData("")] [InlineData("   ")] [InlineData("waha.example.com")] [InlineData("ftp://waha.example.com")] [InlineData("javascript:alert(1)")] [InlineData("https://user:pass@waha.example.com")]
    [InlineData("https://waha.example.com?x=1")] [InlineData("https://waha.example.com#frag")] [InlineData("file:///etc/passwd")]
    public void Unsafe_or_malformed_base_urls_are_refused(string raw) => Assert.False(WahaEndpoint.TryNormalize(raw, out _, out var error) || error.Length == 0);

    private static WahaAddressPolicy Policy(bool allowPrivate) => new(Options.Create(new WahaOptions { AllowPrivateHosts = allowPrivate }), new PublicOnlyAddressPolicy());

    [Theory]
    [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("192.168.1.1")] [InlineData("172.16.0.9")] [InlineData("169.254.169.254")] [InlineData("::1")] [InlineData("0.0.0.0")]
    public void By_default_a_tenant_supplied_url_cannot_reach_private_or_metadata_addresses(string ip) => Assert.False(Policy(false).IsAllowed(IPAddress.Parse(ip)));

    [Theory] [InlineData("93.184.216.34")] [InlineData("8.8.8.8")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(Policy(false).IsAllowed(IPAddress.Parse(ip)));

    [Theory] [InlineData("127.0.0.1")] [InlineData("10.1.2.3")] [InlineData("192.168.1.1")] [InlineData("::1")]
    public void Private_hosts_can_be_enabled_explicitly_for_a_waha_next_to_the_app(string ip) => Assert.True(Policy(true).IsAllowed(IPAddress.Parse(ip)));

    [Theory] [InlineData("169.254.169.254")] [InlineData("169.254.1.1")] [InlineData("0.0.0.0")] [InlineData("fe80::1")]
    public void Link_local_and_metadata_addresses_stay_blocked_even_when_private_hosts_are_allowed(string ip) => Assert.False(Policy(true).IsAllowed(IPAddress.Parse(ip)));
}

public class WahaCircuitBreakerTests
{
    private sealed class ManualTime(DateTimeOffset start) : TimeProvider { public DateTimeOffset Now = start; public override DateTimeOffset GetUtcNow() => Now; }

    [Fact]
    public void The_circuit_opens_after_the_threshold_and_closes_after_the_pause_or_a_success()
    {
        var time = new ManualTime(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var b = new WahaCircuitBreaker(Options.Create(new WahaOptions { BreakerFailureThreshold = 3, BreakerOpenSeconds = 30 }), time);
        b.Failure("h"); b.Failure("h"); Assert.False(b.IsOpen("h"));
        b.Failure("h"); Assert.True(b.IsOpen("h")); Assert.False(b.IsOpen("other"));
        time.Now = time.Now.AddSeconds(31); Assert.False(b.IsOpen("h"));
        b.Failure("h"); b.Failure("h"); b.Failure("h"); Assert.True(b.IsOpen("h"));
        b.Success("h"); Assert.False(b.IsOpen("h"));
    }
}
