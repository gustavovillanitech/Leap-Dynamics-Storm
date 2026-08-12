using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MultiYearReadiness
{
    /// <summary>
    /// READ-ONLY pre-flight for the CloneMultiYearDeals automation.
    ///
    /// CloneMultiYearDeals fires on Opportunity Win. For a MULTI-YEAR opp (Pitched Contract
    /// Length > 1 year) it clones future-year deals. Before deals are flipped to Closed Won it is
    /// important to know each multi-year deal is ready, because the plugin:
    ///   - THROWS (blocks the Win) if the associated deal has no Season.
    ///   - clones with 0% escalation if the opp's Escalator is missing/0 (silently wrong rates).
    ///   - does nothing if the opp type is not Prospect/Current or the term is not set.
    ///
    /// This tool lists every deal that has an opportunity, flags multi-year readiness, and writes
    /// a CSV. It NEVER writes to CRM.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Sandbox: https://org00bff505.crm.dynamics.com/
        // Prod:    https://stormbasketball.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        // Option-set values used by CloneMultiYearDeals.
        private const int OppType_Prospect = 100000003;
        private const int OppType_Current = 100000006;
        private const int ContractLength_Base = 100000000; // value 100000000 => 1 year, +1 per extra year

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

            Console.ForegroundColor = IsProd ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  MultiYearReadiness - READ ONLY pre-flight for cloning");
            Console.WriteLine($"  Target : {EnvUrl}  [{(IsProd ? "PROD" : "SANDBOX")}]");
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
                // 1. Deal line counts per deal
                var lineCount = new Dictionary<Guid, int>();
                var lineQuery = new QueryExpression("new_deallines") { ColumnSet = new ColumnSet("new_dealid") };
                foreach (Entity l in RetrieveAll(service, lineQuery))
                {
                    Guid dId = GetLookupId(l, "new_dealid");
                    if (dId != Guid.Empty) lineCount[dId] = (lineCount.TryGetValue(dId, out int c) ? c : 0) + 1;
                }

                // 2. Deals with their opportunity + season
                var dealQuery = new QueryExpression("new_deals")
                {
                    ColumnSet = new ColumnSet("new_name", "new_opportunity", "new_season", "new_dealstatus")
                };
                List<Entity> deals = RetrieveAll(service, dealQuery);
                Console.WriteLine($"Loaded {deals.Count} deals; {lineCount.Count} deals have lines.\n");

                // 3. Cache opportunities
                var oppCache = new Dictionary<Guid, Entity>();
                var rows = new List<Row>();

                foreach (Entity d in deals)
                {
                    var r = new Row
                    {
                        DealId = d.Id,
                        DealName = GetString(d, "new_name"),
                        Season = GetLookupName(d, "new_season"),
                        HasSeason = d.Contains("new_season") && d["new_season"] != null,
                        LineCount = lineCount.TryGetValue(d.Id, out int lc) ? lc : 0,
                        OppId = GetLookupId(d, "new_opportunity")
                    };

                    if (r.OppId != Guid.Empty)
                    {
                        if (!oppCache.TryGetValue(r.OppId, out Entity opp))
                        {
                            try
                            {
                                opp = service.Retrieve("opportunity", r.OppId,
                                    new ColumnSet("name", "new_opportunitytype", "new_pitchedcontractlength", "new_escalator", "statecode"));
                            }
                            catch { opp = null; }
                            oppCache[r.OppId] = opp;
                        }

                        if (opp != null)
                        {
                            r.OppName = GetString(opp, "name");
                            r.OppTypeVal = GetOptionSet(opp, "new_opportunitytype");
                            r.HasContractLength = opp.Contains("new_pitchedcontractlength") && opp["new_pitchedcontractlength"] != null;
                            if (r.HasContractLength)
                                r.ContractYears = (GetOptionSet(opp, "new_pitchedcontractlength") - ContractLength_Base) + 1;
                            r.Escalator = GetDecimal(opp, "new_escalator");
                            r.HasEscalator = opp.Contains("new_escalator") && opp["new_escalator"] != null;
                        }
                    }

                    r.Verdict = Evaluate(r);
                    rows.Add(r);
                }

                // 4. Report
                string path = Path.Combine(outDir, $"MultiYearReadiness_{(IsProd ? "PROD" : "SANDBOX")}_{stamp}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("DealName,DealId,Season,DealLineCount,OppName,OppId,OppType,ContractYears,Escalator%,HasSeason,Verdict");
                foreach (Row r in rows.OrderByDescending(x => x.ContractYears).ThenBy(x => x.Verdict))
                    sb.AppendLine(string.Join(",",
                        Csv(r.DealName), Csv(r.DealId.ToString()), Csv(r.Season), r.LineCount.ToString(),
                        Csv(r.OppName), Csv(r.OppId == Guid.Empty ? "" : r.OppId.ToString()),
                        Csv(r.OppTypeLabel), r.ContractYears.ToString(),
                        r.HasEscalator ? r.Escalator.ToString(System.Globalization.CultureInfo.InvariantCulture) : "",
                        r.HasSeason ? "Y" : "N", Csv(r.Verdict)));
                File.WriteAllText(path, sb.ToString(), Utf8NoBom);

                // 5. Console summary
                var multi = rows.Where(x => x.ContractYears > 1).ToList();
                var blocks = multi.Where(x => x.Verdict.StartsWith("BLOCK")).ToList();
                var warns = multi.Where(x => x.Verdict.StartsWith("WARN")).ToList();
                var ready = multi.Where(x => x.Verdict == "READY").ToList();
                var unknown = rows.Where(x => x.OppId != Guid.Empty && !x.HasContractLength).ToList();

                Console.WriteLine("=================== READINESS SUMMARY ===================");
                Console.WriteLine($"Deals scanned                 : {rows.Count}");
                Console.WriteLine($"Multi-year (contract > 1 yr)  : {multi.Count}");
                Console.WriteLine($"  READY to clone              : {ready.Count}");
                Console.WriteLine($"  BLOCK (Win will FAIL)       : {blocks.Count}");
                Console.WriteLine($"  WARN (clones but check)     : {warns.Count}");
                Console.WriteLine($"Opp without contract length   : {unknown.Count}  (won't clone - verify if should be multi-year)");
                Console.WriteLine($"Report                        : {path}");
                Console.WriteLine("========================================================\n");

                if (blocks.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("BLOCKERS (fix before flipping these to Closed Won):");
                    foreach (Row r in blocks) Console.WriteLine($"  - {r.DealName}  ({r.ContractYears}y)  -> {r.Verdict}");
                    Console.ResetColor();
                    Console.WriteLine();
                }
                if (warns.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("WARNINGS:");
                    foreach (Row r in warns) Console.WriteLine($"  - {r.DealName}  ({r.ContractYears}y)  -> {r.Verdict}");
                    Console.ResetColor();
                }

                Bye("Done. Review the CSV. No data was changed.");
            }
        }

        private static string Evaluate(Row r)
        {
            if (r.OppId == Guid.Empty) return "no opportunity linked (won't clone)";
            if (!r.HasContractLength) return "term not set (won't clone - verify)";
            if (r.ContractYears <= 1) return "single-year (no clone)";

            // Multi-year from here on.
            if (!r.HasSeason) return "BLOCK: no Season on deal (Win will FAIL)";
            if (r.OppTypeVal != OppType_Prospect && r.OppTypeVal != OppType_Current)
                return "WARN: opp type not Prospect/Current (won't clone)";
            if (!r.HasEscalator || r.Escalator == 0m)
                return "WARN: escalator 0/missing (clones with flat rates)";
            return "READY";
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
                q.PageInfo.PageNumber++;
                q.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        private static Guid GetLookupId(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? r.Id : Guid.Empty;
        private static string GetLookupName(Entity e, string a) => e.Contains(a) && e[a] is EntityReference r ? (r.Name ?? "") : "";
        private static int GetOptionSet(Entity e, string a) => e.Contains(a) && e[a] is OptionSetValue o ? o.Value : int.MinValue;
        private static decimal GetDecimal(Entity e, string a)
        {
            if (e.Contains(a) && e[a] != null)
            {
                if (e[a] is Money m) return m.Value;
                try { return Convert.ToDecimal(e[a]); } catch { return 0m; }
            }
            return 0m;
        }
        private static string GetString(Entity e, string a) => e.Contains(a) && e[a] != null ? e[a].ToString() : "";
        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
        private static void Bye(string msg)
        {
            Console.WriteLine("\n" + msg);
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }

        private class Row
        {
            public Guid DealId;
            public string DealName;
            public string Season;
            public bool HasSeason;
            public int LineCount;
            public Guid OppId;
            public string OppName = "";
            public int OppTypeVal = int.MinValue;
            public bool HasContractLength;
            public int ContractYears = 1;
            public decimal Escalator;
            public bool HasEscalator;
            public string Verdict = "";
            public string OppTypeLabel =>
                OppTypeVal == OppType_Prospect ? "Prospect" :
                OppTypeVal == OppType_Current ? "Current" :
                OppTypeVal == int.MinValue ? "" : OppTypeVal.ToString();
        }
    }
}
