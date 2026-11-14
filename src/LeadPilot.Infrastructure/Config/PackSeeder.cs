using System.Reflection;
using LeadPilot.Application.Config;
using LeadPilot.Domain.Config;
using LeadPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LeadPilot.Infrastructure.Config;

/// <summary>Loads the packs shipped in the repository (src/LeadPilot.Infrastructure/Packs/Builtin/*.json, embedded) into the database. Idempotent; a changed file replaces its stored copy (tenants keep their own applied fingerprints).</summary>
public static class PackSeeder
{
    public static IEnumerable<(string Name, string Json)> Embedded()
    {
        var asm = typeof(PackSeeder).Assembly;
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.Contains(".Packs.Builtin.") && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n))
        {
            using var s = asm.GetManifestResourceStream(name)!; using var r = new StreamReader(s);
            yield return (name, r.ReadToEnd());
        }
    }

    public static async Task EnsureBuiltinAsync(AppDbContext db, DateTime now, ILogger logger, CancellationToken ct)
    {
        foreach (var (name, json) in Embedded())
        {
            var parsed = PackParser.Parse(json);
            if (!parsed.IsValid) { logger.LogError("Builtin pack {Name} is invalid and was not loaded: {Problem}", name, parsed.Problems[0].Message); continue; }
            var p = parsed.Pack!;
            var row = await db.PackDefinitions.FirstOrDefaultAsync(x => x.Scope == PackScope.Builtin && x.Key == p.Key && x.VersionNumber == p.Version, ct);
            if (row is null) { db.PackDefinitions.Add(PackDefinition.Create(PackScope.Builtin, null, p.Key, p.Version, p.Name, p.Description, p.Industry, p.Sample, parsed.CanonicalJson!, parsed.Hash!, true, now, "system")); logger.LogInformation("Builtin pack {Key} v{Version} loaded", p.Key, p.Version); }
            else if (row.ContentHash != parsed.Hash) { db.PackDefinitions.Remove(row); await db.SaveChangesAsync(ct); db.PackDefinitions.Add(PackDefinition.Create(PackScope.Builtin, null, p.Key, p.Version, p.Name, p.Description, p.Industry, p.Sample, parsed.CanonicalJson!, parsed.Hash!, true, now, "system")); logger.LogWarning("Builtin pack {Key} v{Version} changed without a version bump; the stored copy was replaced", p.Key, p.Version); }
        }
        await db.SaveChangesAsync(ct);
    }
}
