using System.Text;

namespace LeadPilot.Application.Crm;

public sealed class SpreadsheetFormatException(string message) : Exception(message);

/// <summary>RFC 4180 reader (quotes, escaped quotes, newlines inside quotes, BOM, comma/semicolon/tab delimiters) with hard limits. Callers cap the file size first.</summary>
public static class CsvReader
{
    public const int MaxColumns = 100;
    public const int MaxCellLength = 10_000;

    public static char DetectDelimiter(string text)
    {
        var inQuotes = false; int comma = 0, semi = 0, tab = 0;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (!inQuotes)
            {
                if (ch is '\r' or '\n') break;
                if (ch == ',') comma++; else if (ch == ';') semi++; else if (ch == '\t') tab++;
            }
        }
        return tab > comma && tab > semi ? '\t' : semi > comma ? ';' : ',';
    }

    public static string DecodeUtf8(Stream stream)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text;
        try { text = reader.ReadToEnd(); }
        catch (DecoderFallbackException) { throw new SpreadsheetFormatException("The file is not valid UTF-8 text. Save it as \"CSV UTF-8\" and try again."); }
        if (text.Contains('\0')) throw new SpreadsheetFormatException("The file contains binary data and is not a CSV text file.");
        return text;
    }

    public static IEnumerable<string[]> Read(Stream stream) => Parse(DecodeUtf8(stream));

    /// <summary>Every record that has at least one non-empty cell. Throws <see cref="SpreadsheetFormatException"/> for unbalanced quotes, over-long cells or too many columns.</summary>
    public static IEnumerable<string[]> Parse(string text)
    {
        var delimiter = DetectDelimiter(text);
        var fields = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        while (i < text.Length)
        {
            var ch = text[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; }
                    inQuotes = false; i++; continue;
                }
                cell.Append(ch); i++;
            }
            else if (ch == '"' && cell.Length == 0) { inQuotes = true; i++; }
            else if (ch == delimiter) { fields.Add(cell.ToString()); cell.Clear(); i++; }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                fields.Add(cell.ToString()); cell.Clear();
                if (fields.Any(f => f.Length > 0)) yield return fields.ToArray();
                fields.Clear();
            }
            else { cell.Append(ch); i++; }

            if (cell.Length > MaxCellLength) throw new SpreadsheetFormatException($"A cell is longer than {MaxCellLength} characters.");
            if (fields.Count >= MaxColumns && (ch == delimiter)) throw new SpreadsheetFormatException($"The file has more than {MaxColumns} columns.");
        }
        if (inQuotes) throw new SpreadsheetFormatException("The file ends inside a quoted value (unbalanced quote).");
        fields.Add(cell.ToString());
        if (fields.Any(f => f.Length > 0)) yield return fields.ToArray();
    }
}
