using System.Reflection;
using LeadPilot.Application.Security;
using LeadPilot.Domain.Common;
using LeadPilot.Domain.Crm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace LeadPilot.ArchitectureTests;

/// <summary>Rules that keep the Phase 3 CRM inside the tenancy, permission and compliance model.</summary>
public class CrmArchitectureTests
{
    public static readonly string[] CrmControllers =
    [
        "LeadsController", "ContactsController", "AudienceController", "NotesController", "TagsController", "PipelinesController", "CatalogController", "CategoriesController",
        "ScoringController", "ImportsController", "SuppressionController", "OpportunitiesController",
    ];

    private static IEnumerable<Type> Controllers(Assembly a) => a.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t) && !t.IsAbstract && CrmControllers.Contains(t.Name));
    private static readonly Assembly Web = typeof(LeadPilot.Web.Models.ErrorViewModel).Assembly;
    private static readonly Assembly Api = typeof(LeadPilot.Api.Controllers.AuthController).Assembly;

    [Fact]
    public void Every_domain_entity_in_the_crm_namespace_is_tenant_owned()
    {
        var entities = typeof(Lead).Assembly.GetTypes().Where(t => t.Namespace == typeof(Lead).Namespace && typeof(Entity).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        Assert.True(entities.Count >= 20, "expected the CRM entities to be discovered");
        Assert.All(entities, t => Assert.True(typeof(ITenantOwned).IsAssignableFrom(t), $"{t.Name} is a CRM entity without tenant ownership"));
    }

    [Fact]
    public void History_and_consent_entities_are_append_only()
    {
        foreach (var t in new[] { typeof(ConsentRecord), typeof(LeadActivity), typeof(LeadScore), typeof(OpportunityActivity) })
            Assert.True(typeof(IAppendOnly).IsAssignableFrom(t), $"{t.Name} must be append-only");
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(LeadPilot.Domain.Tenancy.AuditLog)));
    }

    [Fact]
    public void Append_only_entities_expose_no_mutators()
    {
        foreach (var t in typeof(Lead).Assembly.GetTypes().Where(t => typeof(IAppendOnly).IsAssignableFrom(t) && !t.IsInterface))
        {
            var mutators = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).ToList();
            Assert.True(mutators.Count == 0, $"{t.Name} is append-only but exposes instance methods: {string.Join(", ", mutators)}");
            Assert.All(t.GetProperties(BindingFlags.Public | BindingFlags.Instance), p => Assert.False(p.SetMethod?.IsPublic == true, $"{t.Name}.{p.Name} has a public setter"));
        }
    }

    [Fact]
    public void Crm_controller_actions_declare_a_catalog_permission_and_an_explicit_http_verb()
    {
        var problems = new List<string>();
        foreach (var asm in new[] { Web, Api })
            foreach (var c in Controllers(asm))
            {
                var classPolicies = c.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).ToList();
                foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => m.GetCustomAttribute<NonActionAttribute>() is null))
                {
                    var policies = classPolicies.Concat(m.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy)).Where(p => p is not null).ToList();
                    if (policies.Count == 0 || m.GetCustomAttributes<AllowAnonymousAttribute>().Any()) problems.Add($"{c.FullName}.{m.Name}: no permission policy");
                    foreach (var p in policies.Where(p => !PermissionCatalog.Exists(p!))) problems.Add($"{c.FullName}.{m.Name}: unknown policy '{p}'");
                    if (!m.GetCustomAttributes<HttpMethodAttribute>(true).Any()) problems.Add($"{c.FullName}.{m.Name}: no explicit HTTP verb");
                }
            }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Mutating_mvc_actions_only_answer_post()
    {
        string[] mutatingPrefixes = ["Archive", "Restore", "Delete", "Remove", "Bulk", "Assign", "ChangeStage", "SetTags", "AddNote", "AddContact", "RecordConsent", "Rescore", "Run", "Upload",
            "ResolveDuplicate", "ToggleRule", "AddRule", "Rename", "SetDefault", "SetActive", "AddStage", "UpdateStage", "MoveStage", "DeleteStage", "CreateCategory", "CreateStatus", "UpdateStatus", "SetCategoryActive", "SetStatusActive", "Add"];
        var problems = new List<string>();
        foreach (var c in Controllers(Web))
            foreach (var m in c.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var verbs = m.GetCustomAttributes<HttpMethodAttribute>(true).SelectMany(a => a.HttpMethods).ToList();
                var isCreateOrEdit = m.Name is "Create" or "Edit" or "EditContact";   // GET shows the form, POST saves: the POST overload is checked separately
                if (mutatingPrefixes.Any(p => m.Name == p || m.Name.StartsWith(p, StringComparison.Ordinal)) && !isCreateOrEdit && verbs.Any(v => v != "POST"))
                    problems.Add($"{c.Name}.{m.Name} changes data but answers {string.Join("/", verbs)}");
                if (m.GetParameters().Any(p => p.ParameterType == typeof(LeadPilot.Web.Models.LeadFormModel) || p.ParameterType == typeof(LeadPilot.Web.Models.ContactFormModel)) && verbs.Contains("GET"))
                    problems.Add($"{c.Name}.{m.Name} accepts a form model on GET");
            }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Web_actions_that_return_data_files_are_permissioned_exports()
    {
        // downloads of lead data must sit behind the Export / Import permissions
        var export = typeof(LeadPilot.Web.Controllers.LeadsController).GetMethod("Export")!;
        Assert.Contains(export.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PermissionCodes.LeadsExport);
        var apiExport = typeof(LeadPilot.Api.Controllers.LeadsController).GetMethod("Export")!;
        Assert.Contains(apiExport.GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PermissionCodes.LeadsExport);
        Assert.Contains(typeof(LeadPilot.Web.Controllers.ImportsController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PermissionCodes.LeadsImport);
        Assert.Contains(typeof(LeadPilot.Web.Controllers.SuppressionController).GetCustomAttributes<AuthorizeAttribute>(), a => a.Policy == PermissionCodes.SuppressionManage);
    }

    [Fact]
    public void Consent_cannot_be_edited_through_any_service_or_controller()
    {
        var service = typeof(LeadPilot.Application.Crm.IConsentService).GetMethods().Select(m => m.Name).ToList();
        Assert.DoesNotContain(service, n => n.Contains("Update") || n.Contains("Delete") || n.Contains("Remove") || n.Contains("Edit"));
        foreach (var asm in new[] { Web, Api })
            Assert.DoesNotContain(asm.GetTypes().Where(t => typeof(ControllerBase).IsAssignableFrom(t)).SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)),
                m => m.Name.Contains("Consent", StringComparison.OrdinalIgnoreCase) && (m.Name.Contains("Update") || m.Name.Contains("Delete") || m.Name.Contains("Edit") || m.Name.Contains("Remove")));
    }

    [Fact]
    public void The_new_permissions_are_in_the_catalog_and_default_roles()
    {
        foreach (var code in new[] { PermissionCodes.LeadsImport, PermissionCodes.LeadsExport, PermissionCodes.LeadsAssign, PermissionCodes.ConsentManage, PermissionCodes.SuppressionManage, PermissionCodes.PipelinesManage, PermissionCodes.ScoringManage })
        {
            Assert.True(PermissionCatalog.Exists(code));
            Assert.False(PermissionCatalog.IsPlatform(code));
            Assert.Contains(code, SystemRoles.All.Single(r => r.Name == SystemRoles.TenantAdministrator).Permissions);
            Assert.DoesNotContain(code, SystemRoles.All.Single(r => r.Name == SystemRoles.ReadOnly).Permissions);   // read-only users never import, export, assign, consent, suppress or configure
        }
    }
}
