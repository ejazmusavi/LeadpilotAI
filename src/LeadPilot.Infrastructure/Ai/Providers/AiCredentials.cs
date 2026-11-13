using LeadPilot.Application.Abstractions;
using LeadPilot.Application.Ai;
using LeadPilot.Domain.Ai;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeadPilot.Infrastructure.Ai.Providers;

/// <summary>
/// The only code that decrypts a tenant's AI provider keys. Scoped: one tenant per scope. Keys are returned to the provider adapters only; they are never logged, never put into an exception message and
/// never leave the Infrastructure/Ai namespace (an architecture test checks that nothing else reads the protected columns).
/// </summary>
public sealed class AiCredentials(AppDbContext db, ITenantContext tenant, ISecretProtector secrets)
{
    private AiTenantSettings? _settings; private bool _loaded;

    public async Task<AiTenantSettings?> SettingsAsync(CancellationToken ct)
    {
        if (_loaded) return _settings;
        var tenantId = tenant.RequireTenantId();
        _settings = await db.AiTenantSettings.AsNoTracking().FirstOrDefaultAsync(s => s.TenantId == tenantId, ct);
        _loaded = true;
        return _settings;
    }

    public void Invalidate() { _loaded = false; _settings = null; }

    public async Task<string?> ChatKeyAsync(CancellationToken ct) => Decrypt((await SettingsAsync(ct))?.ProtectedApiKey);
    public async Task<string?> EmbeddingKeyAsync(CancellationToken ct) => Decrypt((await SettingsAsync(ct))?.ProtectedEmbeddingKey);

    private string? Decrypt(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return secrets.Unprotect(protectedValue); }
        catch (Exception) { return null; }   // unreadable (key ring changed): treated as "not configured", the settings page asks for the key again
    }
}
