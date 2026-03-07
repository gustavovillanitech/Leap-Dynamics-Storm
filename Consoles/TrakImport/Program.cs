using TrakImport;

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║       Trak → Dynamics 365 Import Tool        ║");
Console.WriteLine("╚══════════════════════════════════════════════╝\n");

// ── Load configuration ────────────────────────────────────────────────────────
AppConfig config;
try
{
    config = AppConfig.Load();
    Console.WriteLine($"[Config] CRM: {config.CrmBaseUrl}");
    Console.WriteLine($"[Config] CSV: {config.CsvFilePath}");
    Console.WriteLine($"[Config] Log: {config.LogFilePath}\n");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Could not load configuration: {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Parse CSV ─────────────────────────────────────────────────────────────────
List<CsvRecord> records;
try
{
    records = CsvParser.Parse(config.CsvFilePath);
    var uniqueDeals = records.Select(r => r.DealId).Distinct().Count();
    Console.WriteLine($"[CSV] Loaded {records.Count} lines across {uniqueDeals} unique deals.\n");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Could not read CSV: {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Connect to Dynamics (interactive login + MFA) ─────────────────────────────
Microsoft.PowerPlatform.Dataverse.Client.ServiceClient client;
try
{
    var auth = new AuthService(config);
    client = auth.Connect();
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Could not connect to Dynamics: {ex.Message}");
    Console.ResetColor();
    return;
}

// ── Confirm before writing anything ──────────────────────────────────────────
Console.WriteLine($"Ready to import {records.Count} lines into {config.CrmBaseUrl}");
Console.Write("Type 'YES' to proceed, anything else to abort: ");
var answer = Console.ReadLine()?.Trim();
if (!string.Equals(answer, "YES", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Import aborted by user.");
    return;
}
Console.WriteLine();

// ── Run import ────────────────────────────────────────────────────────────────
var logger       = new ImportLogger(config.LogFilePath);
var crmService   = new DynamicsService(client, config);
var orchestrator = new ImportOrchestrator(crmService, logger);

try
{
    orchestrator.Run(records);
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[FATAL] Unexpected error during import: {ex.Message}");
    Console.ResetColor();
}

// ── Save log ──────────────────────────────────────────────────────────────────
logger.Save();

Console.WriteLine("\nPress any key to exit...");
Console.ReadKey();
