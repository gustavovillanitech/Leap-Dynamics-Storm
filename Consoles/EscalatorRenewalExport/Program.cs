using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace EscalatorRenewalExport
{
    /// <summary>
    /// READ-ONLY export for the Storm team (Zack) to complete escalator + option + playoff-option
    /// data. One row per Deal. The escalator lives on the parent OPPORTUNITY (new_escalator); the
    /// option / playoff / status fields live on the DEAL. We join Deal -> Opportunity via
    /// new_opportunity to bring the escalator over. This tool NEVER writes.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Prod:    https://stormbasketball.crm.dynamics.com/
        // Sandbox: https://org00bff505.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";
        // ======================================================================

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;
            string env = IsProd ? "PROD" : "SANDBOX";

            Console.ForegroundColor = IsProd ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  EscalatorRenewalExport - READ ONLY (Deals + Escalator + Option + Playoff)");
            Console.WriteLine($"  Target : {EnvUrl}  [{env}]");
            Console.WriteLine("=========================================================");
            Console.ResetColor();
            Console.WriteLine("This tool only READS data. Type 'Y' to continue:");
            if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "Y") { Bye("Cancelled."); return; }

            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
            Console.WriteLine("\nConnecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful!\n");

            using (service)
            {
                // ---- 1) OPPORTUNITIES: map id -> (escalator, hasEscalator, term) ----
                var oppEscalator = new Dictionary<Guid, string>();
                var oppHasEscalator = new Dictionary<Guid, bool>();
                var oppTerm = new Dictionary<Guid, string>();

                foreach (Entity o in RetrieveAll(service, new QueryExpression("opportunity")
                {
                    ColumnSet = new ColumnSet("new_escalator", "new_pitchedcontractlength")
                }))
                {
                    oppEscalator[o.Id] = GetDisplay(o, "new_escalator");
                    oppHasEscalator[o.Id] = IsNonZeroNumeric(o, "new_escalator");
                    oppTerm[o.Id] = GetDisplay(o, "new_pitchedcontractlength");
                }
                Console.WriteLine($"Opportunities scanned : {oppEscalator.Count}");

                // ---- 2) DEALS ----
                var deals = RetrieveAll(service, new QueryExpression("new_deals")
                {
                    ColumnSet = new ColumnSet(
                        "new_name",                     // Deal Name
                        "new_dealstatus",               // Deal Status (prospect / current / lost, etc.)
                        "new_dealtype",                 // Deal Type (New Business / Renewal / Upsell / Other)
                        "new_accountid",                // Account
                        "new_season",                   // Season
                        "new_opportunity",              // Management Opportunity (link to opportunity)
                        "new_total",                    // Total / Net Total
                        // ---- Regular-season option ----
                        "new_optouttype",               // Deal Option Status
                        "new_optoutdeadline",           // Option Deadline Date
                        "new_dealoptiondecision",       // Deal Option Decision
                        "new_optionnegotiationwindow",  // Option Negotiation Window
                        "new_dealoptionnotes",          // Deal Option Notes
                        // ---- Playoff option ----
                        "new_playoffoptionstatus",      // Playoff Option Status
                        "new_playoffoptiondeadline",    // Playoff Option Deadline
                        "new_playoffoptiondecision",    // Playoff Option Decision
                        "new_playoffoptionnote")        // Playoff Option Note
                });
                Console.WriteLine($"Deals scanned         : {deals.Count}\n");

                // ---- 3) BUILD CSV ----
                string csvPath = Path.Combine(outDir, $"EscalatorRenewal_Report_{env}_{stamp}.csv");
                var sb = new StringBuilder();
                sb.AppendLine(string.Join(",",
                    "DealName", "Account", "Season", "DealStatus", "DealType", "PitchedContractLength",
                    "LinkedOpportunity", "DealTotal",
                    "Escalator", "HasEscalator",
                    "DealOptionStatus", "OptionDeadline", "DealOptionDecision", "OptionNegotiationWindow", "DealOptionNotes",
                    "PlayoffOptionStatus", "PlayoffOptionDeadline", "PlayoffOptionDecision", "PlayoffOptionNote"));

                int withEscalator = 0, missingEscalator = 0, noLinkedOpp = 0, renewalType = 0;
                var byStatus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (Entity d in deals.OrderBy(x => GetString(x, "new_name")))
                {
                    Guid oppId = GetLookupId(d, "new_opportunity");
                    bool hasOpp = oppId != Guid.Empty && oppEscalator.ContainsKey(oppId);

                    string escalator, term;
                    bool hasEsc;
                    if (hasOpp) { escalator = oppEscalator[oppId]; hasEsc = oppHasEscalator[oppId]; term = oppTerm[oppId]; }
                    else { escalator = ""; hasEsc = false; term = ""; noLinkedOpp++; }

                    string hasEscText = !hasOpp ? "NO LINKED OPPORTUNITY" : (hasEsc ? "Y" : "N");
                    if (hasOpp) { if (hasEsc) withEscalator++; else missingEscalator++; }

                    string dealType = GetOptionLabel(d, "new_dealtype");
                    if (dealType.IndexOf("Renewal", StringComparison.OrdinalIgnoreCase) >= 0) renewalType++;

                    string status = GetDisplay(d, "new_dealstatus");
                    string statusKey = string.IsNullOrWhiteSpace(status) ? "(blank)" : status;
                    byStatus[statusKey] = (byStatus.TryGetValue(statusKey, out int sv) ? sv : 0) + 1;

                    sb.AppendLine(string.Join(",",
                        Csv(GetString(d, "new_name")),
                        Csv(GetLookupName(d, "new_accountid")),
                        Csv(GetLookupName(d, "new_season")),
                        Csv(status),
                        Csv(dealType),
                        Csv(term),
                        Csv(GetLookupName(d, "new_opportunity")),
                        Csv(GetMoneyStr(d, "new_total")),
                        Csv(escalator),
                        Csv(hasEscText),
                        Csv(GetOptionLabel(d, "new_optouttype")),
                        Csv(GetDateStr(d, "new_optoutdeadline")),
                        Csv(GetOptionLabel(d, "new_dealoptiondecision")),
                        Csv(GetOptionLabel(d, "new_optionnegotiationwindow")),
                        Csv(GetString(d, "new_dealoptionnotes")),
                        Csv(GetOptionLabel(d, "new_playoffoptionstatus")),
                        Csv(GetDateStr(d, "new_playoffoptiondeadline")),
                        Csv(GetOptionLabel(d, "new_playoffoptiondecision")),
                        Csv(GetString(d, "new_playoffoptionnote"))));
                }

                File.WriteAllText(csvPath, sb.ToString(), Utf8NoBom);

                Console.WriteLine("==================== EXPORT SUMMARY ====================");
                Console.WriteLine($"Deals exported                 : {deals.Count}");
                Console.WriteLine($"  with escalator (Y)           : {withEscalator}");
                Console.WriteLine($"  missing escalator (N)        : {missingEscalator}");
                Console.WriteLine($"  no linked opportunity        : {noLinkedOpp}");
                Console.WriteLine($"  deal type = Renewal          : {renewalType}");
                Console.WriteLine("  ---- deals by Deal Status ----");
                foreach (var kv in byStatus.OrderByDescending(k => k.Value))
                    Console.WriteLine($"    {kv.Key,-32} : {kv.Value}");
                Console.WriteLine($"Report                         : {csvPath}");
                Console.WriteLine("=======================================================");
                Console.WriteLine("\nNote: read-only. Escalator comes from the parent Opportunity (new_escalator).");
                Bye("Done.");
            }
        }

        // ============================ HELPERS ============================
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

        private static Guid GetLookupId(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? r.Id : Guid.Empty;

        private static string GetLookupName(Entity e, string a)
        {
            if (e.Contains(a) && e[a] is EntityReference r)
                return string.IsNullOrEmpty(r.Name) ? r.Id.ToString() : r.Name;
            return "";
        }

        private static string GetString(Entity e, string a) => e.Contains(a) && e[a] != null ? e[a].ToString() : "";

        private static string GetOptionLabel(Entity e, string a)
        {
            if (e.FormattedValues != null && e.FormattedValues.Contains(a)) return e.FormattedValues[a];
            if (e.Contains(a) && e[a] is OptionSetValue osv) return osv.Value.ToString();
            return "";
        }

        private static string GetDisplay(Entity e, string a)
        {
            if (e.FormattedValues != null && e.FormattedValues.Contains(a)) return e.FormattedValues[a];
            if (!e.Contains(a) || e[a] == null) return "";
            object v = e[a];
            if (v is Money m) return m.Value.ToString(CultureInfo.InvariantCulture);
            if (v is OptionSetValue o) return o.Value.ToString();
            if (v is EntityReference r) return r.Name ?? r.Id.ToString();
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        private static bool IsNonZeroNumeric(Entity e, string a)
        {
            if (!e.Contains(a) || e[a] == null) return false;
            object v = e[a];
            decimal d;
            if (v is Money m) return m.Value != 0m;
            if (v is int i) return i != 0;
            if (v is decimal dec) return dec != 0m;
            if (v is double db) return db != 0d;
            if (v is OptionSetValue) return true;
            if (decimal.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                return d != 0m;
            return !string.IsNullOrWhiteSpace(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        private static string GetDateStr(Entity e, string a) =>
            e.Contains(a) && e[a] is DateTime dt ? dt.ToString("yyyy-MM-dd") : "";

        private static string GetMoneyStr(Entity e, string a) =>
            e.Contains(a) && e[a] is Money m ? m.Value.ToString(CultureInfo.InvariantCulture) : "";

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
