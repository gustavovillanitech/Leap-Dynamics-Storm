namespace TrakImport;

public class ImportLogger
{
    private readonly List<LogEntry> _entries = new();
    private readonly string _filePath;

    public ImportLogger(string filePath) => _filePath = filePath;

    public void Add(LogEntry entry)
    {
        _entries.Add(entry);

        // Also print to console in real time
        var color = entry.Status switch
        {
            "Success" => ConsoleColor.Green,
            "Warning" => ConsoleColor.Yellow,
            "Error"   => ConsoleColor.Red,
            _         => ConsoleColor.Gray
        };
        Console.ForegroundColor = color;
        Console.WriteLine($"[{entry.Status,-8}] {entry.Entity,-12} LineID={entry.LineId,-6} DealID={entry.DealId,-6} {entry.DealName}");
        if (!string.IsNullOrEmpty(entry.ErrorMessage))
            Console.WriteLine($"           => {entry.ErrorMessage}");
        Console.ResetColor();
    }

    public void Save()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Timestamp,LineId,DealId,DealName,Account,Entity,Status,RecordId,ErrorMessage");

        foreach (var e in _entries)
        {
            sb.AppendLine(string.Join(",",
                Escape(e.Timestamp),
                Escape(e.LineId),
                Escape(e.DealId),
                Escape(e.DealName),
                Escape(e.Account),
                Escape(e.Entity),
                Escape(e.Status),
                Escape(e.RecordId),
                Escape(e.ErrorMessage)
            ));
        }

        File.WriteAllText(_filePath, sb.ToString(), System.Text.Encoding.UTF8);

        // Summary
        Console.WriteLine("\n=== IMPORT SUMMARY ===");
        Console.WriteLine($"Total entries logged : {_entries.Count}");
        Console.WriteLine($"Success              : {_entries.Count(e => e.Status == "Success")}");
        Console.WriteLine($"Warnings             : {_entries.Count(e => e.Status == "Warning")}");
        Console.WriteLine($"Errors               : {_entries.Count(e => e.Status == "Error")}");
        Console.WriteLine($"Skipped              : {_entries.Count(e => e.Status == "Skipped")}");
        Console.WriteLine($"Log saved to         : {Path.GetFullPath(_filePath)}");
    }

    private static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        value = value.Replace("\"", "\"\"");
        return $"\"{value}\"";
    }
}
