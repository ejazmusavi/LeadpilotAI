namespace LeadPilot.Application.Abstractions;

/// <summary>Raised when tenant-owned data is touched without any tenant context. Always a bug or an attack: the system fails closed.</summary>
public sealed class TenantContextMissingException(string message = "No tenant context is available; tenant-owned data cannot be accessed.") : InvalidOperationException(message);

/// <summary>Raised when code tries to write or reference data that belongs to a different tenant than the active one.</summary>
public sealed class CrossTenantViolationException(string message) : InvalidOperationException(message);

/// <summary>The only source of tenant identity inside Application/Infrastructure. Populated by middleware, job filters and consumers.</summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    /// <summary>True only inside an explicit platform scope (provisioning, seeding, audited cross-tenant operations).</summary>
    bool IsPlatformScope { get; }
    /// <summary>Returns the active tenant or throws <see cref="TenantContextMissingException"/>.</summary>
    Guid RequireTenantId();
}

/// <summary>Mutation side of <see cref="ITenantContext"/>. Used only by the composition root (middleware, job runner), never by feature code.</summary>
public interface ITenantContextAccessor
{
    void SetTenant(Guid tenantId);
    void Clear();
    IDisposable BeginPlatformScope();
}

public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Email { get; }
}

/// <summary>Request metadata used for audit trails.</summary>
public interface IRequestInfo
{
    string? IpAddress { get; }
    string? CorrelationId { get; }
}

/// <summary>Runs background-job / consumer code inside a validated tenant scope. Fails closed without a usable tenant.</summary>
public interface ITenantScopeRunner
{
    Task RunAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default);
    /// <summary>For jobs that are explicitly cross-tenant (maintenance). Feature jobs must use <see cref="RunAsync"/>.</summary>
    Task RunPlatformJobAsync(Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default);
    /// <summary>
    /// For a platform operator acting ON a tenant (verify its payment, grant a plan, reactivate it). Unlike <see cref="RunAsync"/> it also works for a suspended tenant (that is the point: reactivation), but the
    /// tenant must exist and the work then runs inside that tenant's scope with the usual tenant predicates and save guard. Callers must have checked a Platform.* permission.
    /// </summary>
    Task RunForOperatorAsync(Guid tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken ct = default);
}

public sealed class TenantUnavailableException(string message) : InvalidOperationException(message);

/// <summary>Protects secret values (integration credentials, secret tenant settings) at rest.</summary>
public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedValue);
}

public interface IInvitationLinkBuilder
{
    string Build(string rawToken);
}

/// <summary>Marker for request models that may carry a client-supplied TenantId. The value is never trusted: it must be absent or equal the authorized tenant.</summary>
public interface ITenantScopedRequest
{
    Guid? TenantId { get; }
}
