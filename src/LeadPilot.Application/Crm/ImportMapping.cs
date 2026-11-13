using System.Text.Json;
using LeadPilot.Domain.Crm;

namespace LeadPilot.Application.Crm;

public enum ImportField { Name = 1, Category, Country, Province, City, District, Area, Address, Website, Tags, Stage, OwnerEmail, ContactName, ContactRole, Phone, Email }

public sealed record ColumnMapping(int ColumnIndex, ImportField? Field);

public sealed record ImportRowData(
    string Name, string? Category, string? Country, string? Province, string? City, string? District, string? Area, string? Address, string? Website,
    IReadOnlyList<string> Tags, string? Stage, string? OwnerEmail, string? ContactName, string? ContactRole, string? PhoneRaw, string? PhoneE164, string? Email)
{
    public string ToJson() => JsonSerializer.Serialize(this);
    public static ImportRowData? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ImportRowData>(json); } catch (JsonException) { return null; }
    }
}

public sealed record RowIssue(string? Column, string Message);
public sealed record MappedRow(int RowNumber, ImportRowData? Data, IReadOnlyList<RowIssue> Issues);

/// <summary>Maps spreadsheet columns to lead fields and validates the values that do not need the database.</summary>
public static class ImportRowMapper
{
    public const int MaxTagsPerRow = 10;

    private static readonly Dictionary<ImportField, string[]> Aliases = new()
    {
        [ImportField.Name] = ["name", "leadname", "organization", "organisation", "company", "companyname", "school", "schoolname", "business", "businessname", "account", "accountname"],
        [ImportField.Category] = ["category", "type", "businesstype", "industry", "segment"],
        [ImportField.Country] = ["country"],
        [ImportField.Province] = ["province", "state", "region"],
        [ImportField.City] = ["city", "town"],
        [ImportField.District] = ["district", "county"],
        [ImportField.Area] = ["area", "neighborhood", "neighbourhood", "suburb"],
        [ImportField.Address] = ["address", "streetaddress", "location"],
        [ImportField.Website] = ["website", "web", "url", "site", "domain", "websiteurl"],
        [ImportField.Tags] = ["tags", "tag", "labels"],
        [ImportField.Stage] = ["stage", "pipelinestage"],
        [ImportField.OwnerEmail] = ["owner", "owneremail", "assignedto", "assignee"],
        [ImportField.ContactName] = ["contact", "contactname", "contactperson", "person", "fullname"],
        [ImportField.ContactRole] = ["role", "title", "jobtitle", "position", "contactrole"],
        [ImportField.Phone] = ["phone", "phonenumber", "mobile", "mobilenumber", "tel", "telephone", "whatsapp", "cell"],
        [ImportField.Email] = ["email", "emailaddress", "mail", "contactemail"],
    };

    public static string Label(ImportField f) => f switch
    {
        ImportField.OwnerEmail => "Owner (e-mail of a team member)", ImportField.ContactName => "Contact name", ImportField.ContactRole => "Contact role",
        ImportField.Phone => "Contact phone", ImportField.Email => "Contact e-mail", _ => f.ToString()
    };

    private static string Key(string? header) => new string((header ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Best-effort mapping by header name; each field is used at most once.</summary>
    public static IReadOnlyList<ColumnMapping> Guess(IReadOnlyList<string> headers)
    {
        var used = new HashSet<ImportField>();
        var result = new List<ColumnMapping>();
        for (var i = 0; i < headers.Count; i++)
        {
            var key = Key(headers[i]);
            ImportField? match = null;
            foreach (var (field, names) in Aliases)
                if (!used.Contains(field) && names.Contains(key)) { match = field; break; }
            if (match is { } m) used.Add(m);
            result.Add(new ColumnMapping(i, match));
        }
        return result;
    }

    /// <summary>Null when the mapping is usable.</summary>
    public static string? ValidateMapping(IEnumerable<ColumnMapping> mapping, int columnCount)
    {
        var fields = new HashSet<ImportField>();
        foreach (var m in mapping)
        {
            if (m.ColumnIndex < 0 || m.ColumnIndex >= columnCount) return "The mapping refers to a column that does not exist.";
            if (m.Field is { } f && !fields.Add(f)) return $"The field '{Label(f)}' is mapped to more than one column.";
        }
        return fields.Contains(ImportField.Name) ? null : "Map one column to 'Name': every lead needs a name.";
    }

    public static MappedRow Map(int rowNumber, IReadOnlyList<string> cells, IReadOnlyList<string> headers, IEnumerable<ColumnMapping> mapping, string? defaultCountryCode)
    {
        var values = new Dictionary<ImportField, (string Value, string Column)>();
        foreach (var m in mapping)
        {
            if (m.Field is not { } f || m.ColumnIndex >= cells.Count) continue;
            var v = cells[m.ColumnIndex]?.Trim();
            if (!string.IsNullOrEmpty(v)) values[f] = (v, m.ColumnIndex < headers.Count ? headers[m.ColumnIndex] : $"Column {m.ColumnIndex + 1}");
        }

        var issues = new List<RowIssue>();
        string? Get(ImportField f, int max)
        {
            if (!values.TryGetValue(f, out var x)) return null;
            if (x.Value.Length > max) { issues.Add(new RowIssue(x.Column, $"{Label(f)} is longer than {max} characters.")); return null; }
            return x.Value;
        }

        var name = Get(ImportField.Name, 300);
        if (!values.ContainsKey(ImportField.Name))
        {
            var nameCol = mapping.FirstOrDefault(m => m.Field == ImportField.Name);
            issues.Add(new RowIssue(nameCol is not null && nameCol.ColumnIndex < headers.Count ? headers[nameCol.ColumnIndex] : null, "Name is required."));
        }
        else if (name is not null && Normalizer.Name(name).Length == 0) { issues.Add(new RowIssue(values[ImportField.Name].Column, "Name must contain letters or digits.")); name = null; }

        var website = Get(ImportField.Website, 500);
        if (website is not null && Normalizer.Domain(website) is null) { issues.Add(new RowIssue(values[ImportField.Website].Column, "Website is not a valid address.")); website = null; }

        var phoneRaw = Get(ImportField.Phone, 40);
        string? phoneE164 = null;
        if (phoneRaw is not null && (phoneE164 = Normalizer.PhoneE164(phoneRaw, defaultCountryCode)) is null)
        {
            issues.Add(new RowIssue(values[ImportField.Phone].Column, "Phone number is not valid (use international format, e.g. +966501234567)."));
            phoneRaw = null;
        }

        var email = Get(ImportField.Email, 256);
        if (email is not null && (email = Normalizer.Email(email)) is null) issues.Add(new RowIssue(values[ImportField.Email].Column, "E-mail address is not valid."));

        var tags = new List<string>();
        if (values.TryGetValue(ImportField.Tags, out var tagCell))
        {
            foreach (var t in tagCell.Value.Split([',', ';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (t.Length > 50) issues.Add(new RowIssue(tagCell.Column, $"Tag '{t[..20]}…' is longer than 50 characters."));
                else tags.Add(t);
            }
            if (tags.Count > MaxTagsPerRow) { issues.Add(new RowIssue(tagCell.Column, $"At most {MaxTagsPerRow} tags per row.")); tags.Clear(); }
        }

        var ownerEmail = Get(ImportField.OwnerEmail, 256);
        if (ownerEmail is not null)
        {
            var normalizedOwner = Normalizer.Email(ownerEmail);
            if (normalizedOwner is null) issues.Add(new RowIssue(values[ImportField.OwnerEmail].Column, "Owner must be the e-mail address of a team member."));
            ownerEmail = normalizedOwner;
        }

        var data = new ImportRowData(
            name ?? string.Empty, Get(ImportField.Category, 100), Get(ImportField.Country, 100), Get(ImportField.Province, 100), Get(ImportField.City, 100),
            Get(ImportField.District, 100), Get(ImportField.Area, 100), Get(ImportField.Address, 500), website, tags, Get(ImportField.Stage, 100), ownerEmail,
            Get(ImportField.ContactName, 200), Get(ImportField.ContactRole, 100), phoneRaw, phoneE164, email);

        if (issues.Count == 0 && data.ContactName is null && data.PhoneE164 is null && data.Email is null && (data.ContactRole is not null))
            issues.Add(new RowIssue(values[ImportField.ContactRole].Column, "A contact role needs a contact name, phone or e-mail."));

        return new MappedRow(rowNumber, issues.Count == 0 ? data : null, issues);
    }
}
