namespace TrakImport;

public static class CsvParser
{
    public static List<CsvRecord> Parse(string filePath)
    {
        var records = new List<CsvRecord>();

        if (!File.Exists(filePath))
            throw new FileNotFoundException($"CSV file not found: {filePath}");

        var lines = File.ReadAllLines(filePath);
        if (lines.Length < 2)
            throw new Exception("CSV file is empty or has no data rows.");

        // Parse header to get column indexes dynamically
        var headers = ParseCsvLine(lines[0]);
        var idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < headers.Count; i++)
            idx[headers[i].Trim()] = i;

        for (int row = 1; row < lines.Length; row++)
        {
            if (string.IsNullOrWhiteSpace(lines[row])) continue;

            var cols = ParseCsvLine(lines[row]);

            try
            {
                var r = new CsvRecord
                {
                    LineId            = GetCol(cols, idx, "Line  ID"),
                    ExternalId        = GetCol(cols, idx, "External ID"),
                    DealId            = GetCol(cols, idx, "Deal ID"),
                    DealName          = GetCol(cols, idx, "Deal Name"),
                    DealDescription   = GetCol(cols, idx, "Deal Description"),
                    DealStage         = GetCol(cols, idx, "Deal Stage"),
                    DealType          = GetCol(cols, idx, "Deal Type"),
                    DealServicePerson = GetCol(cols, idx, "Deal Service Person"),
                    DealSalesPerson   = GetCol(cols, idx, "Deal Sales Person"),
                    Name              = GetCol(cols, idx, "Name"),
                    Season            = GetCol(cols, idx, "Season"),
                    Account           = GetCol(cols, idx, "Account"),
                    AccountIndustry   = GetCol(cols, idx, "Account Industry"),
                    Collection        = GetCol(cols, idx, "Collection"),
                    Description       = GetCol(cols, idx, "Description"),
                    Categories        = GetCol(cols, idx, "Categories"),
                    Inventory         = GetCol(cols, idx, "Inventory"),
                    RateCard          = GetCol(cols, idx, "Rate Card"),
                    Rate              = ParseDecimal(GetCol(cols, idx, "Rate")),
                    Expense           = ParseDecimal(GetCol(cols, idx, "Expense")),
                    Quantity          = ParseDecimal(GetCol(cols, idx, "Quantity")),
                    Total             = ParseDecimal(GetCol(cols, idx, "Total")),
                    ListRate          = ParseDecimal(GetCol(cols, idx, "List Rate")),
                    GainLoss          = ParseDecimal(GetCol(cols, idx, "Gain / Loss")),
                    Yield             = ParseYield(GetCol(cols, idx, "Yield")),
                    Published         = GetCol(cols, idx, "Published")
                };
                records.Add(r);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Row {row + 1} could not be parsed: {ex.Message}");
            }
        }

        return records;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetCol(List<string> cols, Dictionary<string, int> idx, string name)
    {
        if (idx.TryGetValue(name, out int i) && i < cols.Count)
            return cols[i].Trim();
        return "";
    }

    private static decimal ParseDecimal(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Replace(",", "").Trim();
        return decimal.TryParse(value, out var d) ? d : 0;
    }

    /// <summary>
    /// Parses "33.6%" → 0.336  |  "100%" → 1.0  |  "0.336" → 0.336
    /// </summary>
    private static decimal ParseYield(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        if (value.EndsWith('%'))
        {
            var raw = value.TrimEnd('%').Trim();
            if (decimal.TryParse(raw, out var pct))
                return Math.Round(pct / 100m, 6);
        }
        return decimal.TryParse(value, out var d) ? d : 0;
    }

    /// <summary>
    /// RFC-4180 compliant CSV line parser (handles quoted fields with commas/newlines)
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int i = 0;
        while (i <= line.Length)
        {
            if (i == line.Length) { fields.Add(""); break; }

            if (line[i] == '"')
            {
                // Quoted field
                i++; // skip opening quote
                var sb = new System.Text.StringBuilder();
                while (i < line.Length)
                {
                    if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    { sb.Append('"'); i += 2; }
                    else if (line[i] == '"')
                    { i++; break; }
                    else
                    { sb.Append(line[i++]); }
                }
                fields.Add(sb.ToString());
                if (i < line.Length && line[i] == ',') i++;
            }
            else
            {
                // Unquoted field
                int start = i;
                while (i < line.Length && line[i] != ',') i++;
                fields.Add(line.Substring(start, i - start));
                if (i < line.Length) i++; // skip comma
            }
        }
        return fields;
    }
}
