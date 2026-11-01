using System.Text;

namespace MediaReconciler;

/// <summary>Minimal RFC 4180 CSV reading and writing (Excel-friendly).</summary>
public static class Csv
{
    public static List<string> ParseLine(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";

        // File names come from the scanned sources; stop Excel running one like "=HYPERLINK(...).jpg" as a formula.
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    public static void Write(string path, IEnumerable<string> header, IEnumerable<IEnumerable<string?>> rows)
    {
        // UTF-8 with BOM so Excel shows non-ASCII file names correctly.
        using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine(string.Join(",", header.Select(Escape)));
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", row.Select(Escape)));
        }
    }
}
