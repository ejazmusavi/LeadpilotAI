using System.Security.Cryptography;
using System.Text.Json;
using LeadPilot.Application.Messaging;
using LeadPilot.Domain.Messaging;

namespace LeadPilot.Infrastructure.Messaging.Waha;

/// <summary>
/// Turns a WAHA webhook body into a provider-independent <see cref="GatewayEvent"/>. Pure (no I/O). Shapes follow
/// https://waha.devlike.pro/docs/how-to/events/ and /receive-messages/; fields the docs mark as engine specific are probed defensively and never trusted blindly.
/// </summary>
public static class WahaWebhookNormalizer
{
    public const int MaxBodyBytes = 256 * 1024;

    public static string ExternalEventId(JsonElement root, ReadOnlySpan<byte> body)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 and <= 128 } s) return s;
        // Older payload samples carry no event id: a redelivery has an identical body, hence an identical hash.
        return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(body));
    }

    public static WebhookProcessingResult Normalize(ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || body.Length > MaxBodyBytes) return WebhookProcessingResult.Rejected(WebhookRejection.MalformedPayload, "empty or too large");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = false }); }
        catch (JsonException) { return WebhookProcessingResult.Rejected(WebhookRejection.MalformedPayload, "not valid JSON"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return WebhookProcessingResult.Rejected(WebhookRejection.MalformedPayload, "not an object");
            var eventType = Str(root, "event");
            var session = Str(root, "session");
            if (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 60 || string.IsNullOrWhiteSpace(session) || session.Length > 100)
                return WebhookProcessingResult.Rejected(WebhookRejection.MalformedPayload, "event/session missing");
            var eventId = ExternalEventId(root, body);
            var occurred = FromUnixMs(Num(root, "timestamp"));
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                return WebhookProcessingResult.Accepted(new IgnoredGatewayEvent(eventId, eventType, session, "no payload"));

            return eventType switch
            {
                "message" or "message.any" => Accepted(NormalizeMessage(eventId, eventType, session, payload)),
                "message.ack" => Accepted(NormalizeAck(eventId, eventType, session, payload, occurred)),
                "session.status" => Accepted(NormalizeSession(eventId, eventType, session, root, payload, occurred)),
                _ => Accepted(new IgnoredGatewayEvent(eventId, eventType, session, "event type is not handled")),
            };
        }
    }

    private static WebhookProcessingResult Accepted(GatewayEvent e) => WebhookProcessingResult.Accepted(e);

    private static GatewayEvent NormalizeMessage(string eventId, string eventType, string session, JsonElement p)
    {
        // "message" is documented as incoming, but the samples show fromMe=true too; message.any includes our own messages. Only fromMe=false is inbound.
        if (!p.TryGetProperty("fromMe", out var fm) || fm.ValueKind != JsonValueKind.False) return new IgnoredGatewayEvent(eventId, eventType, session, "not an incoming message (fromMe)");
        var id = Str(p, "id");
        var from = Str(p, "from");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200 || string.IsNullOrWhiteSpace(from) || from.Length > 100) return new IgnoredGatewayEvent(eventId, eventType, session, "message without id or sender");
        if (!ChatIds.IsDirectChat(from)) return new IgnoredGatewayEvent(eventId, eventType, session, "group, channel or broadcast chats are not handled");

        var phone = ChatIds.ToE164(from) ?? ProbeAlternatePhone(p);
        var ts = FromUnixSeconds(Num(p, "timestamp")) ?? DateTime.UtcNow;
        var body = Str(p, "body") ?? string.Empty;
        var attachments = new List<AttachmentInfo>();
        if (p.TryGetProperty("hasMedia", out var hm) && hm.ValueKind == JsonValueKind.True)
        {
            string? name = null, mime = null; long? size = null; var hasUrl = false;
            if (p.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object)
            {
                name = Str(media, "filename"); mime = Str(media, "mimetype"); hasUrl = !string.IsNullOrEmpty(Str(media, "url"));
                if (Num(media, "filesize") is { } fs) size = (long)fs;
            }
            attachments.Add(new AttachmentInfo(name, mime, size, hasUrl));
        }
        return new InboundMessageEvent(eventId, eventType, session, ts, id, from, phone, PushName(p), body, attachments);
    }

    private static GatewayEvent NormalizeAck(string eventId, string eventType, string session, JsonElement p, DateTime? occurred)
    {
        if (!p.TryGetProperty("fromMe", out var fm) || fm.ValueKind != JsonValueKind.True) return new IgnoredGatewayEvent(eventId, eventType, session, "ack of a message we did not send");
        var id = Str(p, "id");
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) return new IgnoredGatewayEvent(eventId, eventType, session, "ack without message id");
        if (id.Contains("@g.us", StringComparison.Ordinal) || !p.TryGetProperty("ack", out var ackEl) || ackEl.ValueKind != JsonValueKind.Number || !ackEl.TryGetInt32(out var ack)) return new IgnoredGatewayEvent(eventId, eventType, session, "group ack or ack without a numeric value");
        var chat = Str(p, "from") ?? ChatFromMessageId(id);
        if (chat is null || !ChatIds.IsDirectChat(chat)) return new IgnoredGatewayEvent(eventId, eventType, session, "ack for a chat that is not a direct chat");
        return new MessageAckEvent(eventId, eventType, session, occurred, id, chat, ack, AckRules.ToDelivery(ack), AckRules.IsError(ack));
    }

    private static GatewayEvent NormalizeSession(string eventId, string eventType, string session, JsonElement root, JsonElement p, DateTime? occurred)
    {
        var raw = Str(p, "status");
        if (raw is null) return new IgnoredGatewayEvent(eventId, eventType, session, "session.status without status");
        var status = MapSessionStatus(raw);
        string? phone = null, push = null;
        if (root.TryGetProperty("me", out var me) && me.ValueKind == JsonValueKind.Object) { phone = ChatIds.ToE164(Str(me, "id")); push = Str(me, "pushName"); }

        var restricted = false; DateTime? until = null; string? kind = null;
        if (p.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("reachoutTimelock", out var tl) && tl.ValueKind == JsonValueKind.Object && tl.TryGetProperty("isActive", out var act) && act.ValueKind == JsonValueKind.True)
            { restricted = true; kind = "reachout_timelock"; until = FromUnixSeconds(Num(tl, "timeEnforcementEnds")); }
            if (data.TryGetProperty("messageCapping", out var cap) && cap.ValueKind == JsonValueKind.Object && string.Equals(Str(cap, "cappingStatus"), "CAPPED", StringComparison.OrdinalIgnoreCase))
            { restricted = true; kind = "message_capping"; until = FromUnixSeconds(Num(cap, "cycleEnd")) ?? until; }
        }
        return new SessionStatusEvent(eventId, eventType, session, occurred, status, phone, push, restricted, until, kind);
    }

    public static GatewaySessionStatus MapSessionStatus(string? raw) => raw?.ToUpperInvariant() switch
    {
        "STOPPED" => GatewaySessionStatus.Stopped,
        "STARTING" => GatewaySessionStatus.Starting,
        "SCAN_QR_CODE" or "PASSKEY_REQUIRED" or "PASSKEY_CONFIRMATION_REQUIRED" => GatewaySessionStatus.ScanQrCode,   // a person must act on the phone/browser
        "WORKING" => GatewaySessionStatus.Working,
        "FAILED" => GatewaySessionStatus.Failed,
        _ => GatewaySessionStatus.Unknown,
    };

    /// <summary>"true_923001234567@c.us_ABC123" → "923001234567@c.us".</summary>
    internal static string? ChatFromMessageId(string id)
    {
        var first = id.IndexOf('_');
        if (first < 0) return null;
        var rest = id[(first + 1)..];
        var second = rest.IndexOf('_');
        return second > 0 ? rest[..second] : null;
    }

    /// <summary>Hidden (@lid) senders carry no phone in the documented fields. Some engines add the phone-based id under engine specific keys of _data; that is probed, never required, and an unparseable value is ignored.</summary>
    private static string? ProbeAlternatePhone(JsonElement p)
    {
        if (!p.TryGetProperty("_data", out var d) || d.ValueKind != JsonValueKind.Object) return null;
        string?[] candidates =
        [
            d.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.Object ? Str(k, "remoteJidAlt") : null,
            d.TryGetProperty("Info", out var i) && i.ValueKind == JsonValueKind.Object ? Str(i, "SenderAlt") : null,
        ];
        return candidates.Select(ChatIds.ToE164).FirstOrDefault(x => x is not null);
    }

    private static string? PushName(JsonElement p)
    {
        var name = Str(p, "pushName");
        if (name is null && p.TryGetProperty("_data", out var d) && d.ValueKind == JsonValueKind.Object) name = Str(d, "notifyName") ?? Str(d, "pushName");
        return name is { Length: > 200 } ? name[..200] : name;
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    public static DateTime? FromUnixSeconds(double? s) => s is { } v && v > 0 && v < 4_102_444_800 ? DateTime.UnixEpoch.AddSeconds(v) : null;
    public static DateTime? FromUnixMs(double? ms) => ms is { } v && v > 0 && v < 4_102_444_800_000 ? DateTime.UnixEpoch.AddMilliseconds(v) : null;
}
