using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LoadEscalatorOptions
{
    /// <summary>
    /// Loads the escalator + option/playoff data from Zack's reviewed file back into Dynamics.
    /// Input: EscalatorOptions_Input.tsv (one row per contract-backed deal, extracted from the
    /// "Escalator_Renewal_Report_..._ZM.xlsx" Deals tab; Amazon Fashion and Intrepid excluded,
    /// Symetra escalator corrected to 3.506% per Christine).
    ///
    /// Matching: by Deal Name (normalized), Account as a tiebreaker. Rows that don't match a live
    /// deal (e.g. records deleted in last week's cleanup) are reported and SKIPPED. Duplicates are
    /// reported and skipped, never guessed.
    ///
    /// Writes:
    ///   * Escalator % -> the linked OPPORTUNITY (new_escalator).
    ///   * Option / playoff fields -> the DEAL (new_optouttype, new_optoutdeadline, ...).
    /// Status text "unknown" and blanks are NOT written (they are placeholders, not contract data),
    /// so existing values are never overwritten with a placeholder.
    ///
    /// DryRun = true (default) writes nothing: it prints every planned change AND reports the current
    /// value + CLR type of each escalator field, so the real write can be type-safe. Set DryRun=false
    /// and type YES to apply.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
        private const bool DryRun = true;

        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        private const string InputFileName = "EscalatorOptions_Input.tsv";
        // ======================================================================

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";

        // -------- option-set maps (text in file -> Dynamics integer value) --------
        // "unknown"/"" are intentionally absent so they are skipped, not written.
        private static readonly Dictionary<string, int> DealOptStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { { "opt-in", 100000001 }, { "opt-out", 100000002 }, { "no option", 100000000 } };

        private static readonly Dictionary<string, int> PlayoffOptStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { { "opt-in", 100000000 }, { "opt-out", 100000001 }, { "in", 100000002 }, { "out", 100000003 } };

        private static readonly Dictionary<string, int> DealOptDecision = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { { "client opted-in", 100000000 }, { "client opted-out", 100000001 }, { "mutual opted-out", 100000002 },
          { "mutual opted-in", 100000003 }, { "storm opted-out", 100000004 } };

        private static readonly Dictionary<string, int> PlayoffOptDecision = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        { { "client opted-in", 100000000 }, { "client opted-out", 100000001 }, { "team didn't qualify", 100000002 } };

        private sealed class Row
        {
            public string DealName, Account, DealOptStatus, OptionDeadline, DealOptDecision,
                          OptionNegWindow, DealOptNotes, PlayoffStatus, PlayoffDeadline, PlayoffDecision, PlayoffNote;
            public string EscalatorPct;
        }

        private static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;

            Console.ForegroundColor = (IsProd || !DryRun) ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  LoadEscalatorOptions - escalator + option/playoff load");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
            Console.WriteLine($"  Mode   : {(DryRun ? "DRY RUN (no writes)" : "WRITE (will modify data)")}");
            Console.WriteLine("=========================================================");
            Console.ResetColor();

            string inputPath = ResolveInput(args);
            if (inputPath == null) { Bye($"Could not find {InputFileName} next to the exe or as arg[0]."); return; }
            List<Row> rows = LoadTsv(inputPath);
            Console.WriteLine($"Input rows: {rows.Count}  ({Path.GetFileName(inputPath)})\n");

            Console.WriteLine("Type 'Y' to connect and continue:");
            if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "Y") { Bye("Cancelled."); return; }

            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
            Console.WriteLine("\nConnecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful!\n");

            using (service)
            {
                // ---- load all deals for matching ----
                var deals = RetrieveAll(service, new QueryExpression("new_deals")
                { ColumnSet = new ColumnSet("new_name", "new_accountid", "new_opportunity") });
                var byName = new Dictionary<string, List<Entity>>(StringComparer.OrdinalIgnoreCase);
                foreach (Entity d in deals)
                {
                    string key = Norm(GetString(d, "new_name"));
                    if (!byName.ContainsKey(key)) byName[key] = new List<Entity>();
                    byName[key].Add(d);
                }
                Console.WriteLine($"Loaded {deals.Count} deals from Dynamics.\n");

                var log = new StringBuilder();
                log.AppendLine("DealName,Match,OpportunityId,PlannedWrites");
                int matched = 0, notFound = 0, ambiguous = 0, wrote = 0, errors = 0, escNoOpp = 0;

                foreach (Row r in rows)
                {
                    string key = Norm(r.DealName);
                    List<Entity> cands = byName.ContainsKey(key) ? byName[key] : new List<Entity>();
                    if (cands.Count > 1)
                        cands = cands.Where(d => Norm(GetLookupName(d, "new_accountid")) == Norm(r.Account)).ToList();

                    if (cands.Count == 0) { notFound++; Warn($"NOT FOUND  '{r.DealName}' (acct '{r.Account}') -> skipped"); log.AppendLine($"{Csv(r.DealName)},NOT_FOUND,,"); continue; }
                    if (cands.Count > 1) { ambiguous++; Warn($"AMBIGUOUS  '{r.DealName}' -> {cands.Count} matches, skipped"); log.AppendLine($"{Csv(r.DealName)},AMBIGUOUS,,"); continue; }

                    Entity deal = cands[0];
                    matched++;

                    // ----- build DEAL update -----
                    var upd = new Entity("new_deals", deal.Id);
                    var planned = new List<string>();

                    AddOptionSet(upd, "new_optouttype", r.DealOptStatus, DealOptStatus, planned, "DealOptionStatus");
                    AddDate(upd, "new_optoutdeadline", r.OptionDeadline, planned, "OptionDeadline");
                    AddOptionSet(upd, "new_dealoptiondecision", r.DealOptDecision, DealOptDecision, planned, "DealOptionDecision");
                    AddBool(upd, "new_optionnegotiationwindow", r.OptionNegWindow, planned, "OptionNegWindow");
                    AddText(upd, "new_dealoptionnotes", r.DealOptNotes, planned, "DealOptionNotes");
                    AddOptionSet(upd, "new_playoffoptionstatus", r.PlayoffStatus, PlayoffOptStatus, planned, "PlayoffStatus");
                    AddDate(upd, "new_playoffoptiondeadline", r.PlayoffDeadline, planned, "PlayoffDeadline");
                    AddOptionSet(upd, "new_playoffoptiondecision", r.PlayoffDecision, PlayoffOptDecision, planned, "PlayoffDecision");
                    AddText(upd, "new_playoffoptionnote", r.PlayoffNote, planned, "PlayoffNote");

                    // ----- escalator -> opportunity -----
                    Guid oppId = GetLookupId(deal, "new_opportunity");
                    string escPlan = "";
                    if (!string.IsNullOrWhiteSpace(r.EscalatorPct))
                    {
                        if (oppId == Guid.Empty) { escNoOpp++; escPlan = $"escalator {r.EscalatorPct}% -> NO LINKED OPPORTUNITY (skipped)"; }
                        else escPlan = $"escalator {r.EscalatorPct}% -> opp {oppId}";
                    }

                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"MATCH  '{GetString(deal, "new_name")}'  [{deal.Id}]");
                    Console.ResetColor();
                    foreach (string p in planned) Console.WriteLine($"     {p}");
                    if (escPlan != "") Console.WriteLine($"     {escPlan}");
                    if (planned.Count == 0 && escPlan == "") Console.WriteLine("     (nothing to write - only placeholders)");

                    log.AppendLine($"{Csv(r.DealName)},MATCH,{oppId},{Csv(string.Join(" | ", planned) + (escPlan == "" ? "" : " | " + escPlan))}");

                    // ----- DryRun: probe escalator field type; else write -----
                    if (DryRun)
                    {
                        if (!string.IsNullOrWhiteSpace(r.EscalatorPct) && oppId != Guid.Empty)
                        {
                            try
                            {
                                Entity opp = service.Retrieve("opportunity", oppId, new ColumnSet("new_escalator"));
                                object cur = opp.Contains("new_escalator") ? opp["new_escalator"] : null;
                                Console.WriteLine($"     [probe] new_escalator current = {Describe(cur)}");
                            }
                            catch (Exception ex) { Console.WriteLine($"     [probe] could not read new_escalator: {ex.Message}"); }
                        }
                        continue;
                    }

                    // ----- REAL WRITE -----
                    try
                    {
                        if (upd.Attributes.Count > 0) service.Update(upd);

                        if (!string.IsNullOrWhiteSpace(r.EscalatorPct) && oppId != Guid.Empty)
                        {
                            Entity opp = service.Retrieve("opportunity", oppId, new ColumnSet("new_escalator"));
                            object cur = opp.Contains("new_escalator") ? opp["new_escalator"] : null;
                            var oppUpd = new Entity("opportunity", oppId);
                            oppUpd["new_escalator"] = CoerceEscalator(r.EscalatorPct, cur);
                            service.Update(oppUpd);
                        }
                        wrote++;
                        Console.ForegroundColor = ConsoleColor.DarkGreen; Console.WriteLine("     WRITTEN"); Console.ResetColor();
                    }
                    catch (Exception ex) { errors++; Err($"write '{r.DealName}': {ex.Message}"); }
                }

                string logPath = Path.Combine(outDir, $"LoadEscalatorOptions_{EnvTag()}_{stamp}.csv");
                File.WriteAllText(logPath, log.ToString(), Utf8NoBom);

                Console.WriteLine("\n==================== SUMMARY ====================");
                Console.WriteLine($"Environment  : {EnvUrl} [{EnvTag()}]  Mode: {(DryRun ? "DRY RUN" : "WRITE")}");
                Console.WriteLine($"Input rows   : {rows.Count}");
                Console.WriteLine($"  matched    : {matched}");
                Console.WriteLine($"  not found  : {notFound}");
                Console.WriteLine($"  ambiguous  : {ambiguous}");
                Console.WriteLine($"  escalator w/o opportunity : {escNoOpp}");
                if (!DryRun) Console.WriteLine($"  written    : {wrote}   errors: {errors}");
                Console.WriteLine($"Log          : {logPath}");
                Console.WriteLine("================================================");

                if (DryRun) { Bye("DryRun = true. Review the planned writes + escalator probes above, then set DryRun=false."); return; }
                Bye(errors == 0 ? "Load complete." : "Load finished with errors - see log.");
            }
        }

        // ============================ WRITE HELPERS ============================
        private static void AddOptionSet(Entity e, string attr, string text, Dictionary<string, int> map, List<string> planned, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!map.TryGetValue(text.Trim(), out int v)) return; // unknown/unmapped -> skip
            e[attr] = new OptionSetValue(v);
            planned.Add($"{label} = {text} ({v})");
        }
        private static void AddDate(Entity e, string attr, string text, List<string> planned, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            { e[attr] = dt; planned.Add($"{label} = {dt:yyyy-MM-dd}"); }
        }
        private static void AddBool(Entity e, string attr, string text, List<string> planned, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            string t = text.Trim().ToLowerInvariant();
            bool? b = (t == "yes" || t == "true") ? true : (t == "no" || t == "false") ? (bool?)false : null;
            if (b == null) return;
            e[attr] = b.Value; planned.Add($"{label} = {(b.Value ? "Yes" : "No")}");
        }
        private static void AddText(Entity e, string attr, string text, List<string> planned, string label)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            e[attr] = text; planned.Add($"{label} = \"{(text.Length > 40 ? text.Substring(0, 40) + "..." : text)}\"");
        }

        /// <summary>Writes the escalator using the field's actual stored type (probed from current value).</summary>
        private static object CoerceEscalator(string pct, object current)
        {
            decimal d = decimal.Parse(pct, NumberStyles.Any, CultureInfo.InvariantCulture);
            switch (current)
            {
                case int _: return (int)Math.Round(d);
                case double _: return (double)d;
                case decimal _: return d;
                case Money _: return new Money(d);
                case string _: return d.ToString(CultureInfo.InvariantCulture);
                default: return d; // never set before -> decimal
            }
        }

        // ============================ IO / HELPERS ============================
        private static string ResolveInput(string[] args)
        {
            if (args != null && args.Length > 0 && File.Exists(args[0])) return args[0];
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            for (int i = 0; i < 6 && dir != null; i++)
            {
                string c = Path.Combine(dir, InputFileName);
                if (File.Exists(c)) return c;
                dir = Directory.GetParent(dir)?.FullName;
            }
            return null;
        }

        private static List<Row> LoadTsv(string path)
        {
            var rows = new List<Row>();
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int i = 1; i < lines.Length; i++) // skip header
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                string[] f = lines[i].Split('\t');
                string F(int n) => n < f.Length ? f[n] : "";
                rows.Add(new Row
                {
                    DealName = F(0), Account = F(1), EscalatorPct = F(2), DealOptStatus = F(3),
                    OptionDeadline = F(4), DealOptDecision = F(5), OptionNegWindow = F(6), DealOptNotes = F(7),
                    PlayoffStatus = F(8), PlayoffDeadline = F(9), PlayoffDecision = F(10), PlayoffNote = F(11)
                });
            }
            return rows;
        }

        private static List<Entity> RetrieveAll(IOrganizationService service, QueryExpression q)
        {
            var all = new List<Entity>();
            q.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, PagingCookie = null };
            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(q);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                q.PageInfo.PageNumber++; q.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            bool prevSpace = false;
            foreach (char c in s)
            {
                if (char.IsWhiteSpace(c)) { if (!prevSpace) sb.Append(' '); prevSpace = true; }
                else { sb.Append(c); prevSpace = false; }
            }
            return sb.ToString().Trim();
        }

        private static Guid GetLookupId(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? r.Id : Guid.Empty;
        private static string GetLookupName(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? (r.Name ?? "") : "";
        private static string GetString(Entity e, string a) => e.Contains(a) && e[a] != null ? e[a].ToString() : "";

        private static string Describe(object v)
        {
            if (v == null) return "(null)";
            if (v is Money m) return $"Money({m.Value})";
            if (v is OptionSetValue o) return $"OptionSet({o.Value})";
            return $"{v.GetType().Name}({v})";
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine("  " + m); Console.ResetColor(); }
        private static void Err(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  ERROR " + m); Console.ResetColor(); }
        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
