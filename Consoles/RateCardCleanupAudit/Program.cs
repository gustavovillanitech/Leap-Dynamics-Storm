using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RateCardCleanupAudit
{
    /// <summary>
    /// READ-ONLY audit for the "Rate Card Cleanup" backlog item (remove old/unused Collections and
    /// Seasons). Counts how many products / inventory reference each Collection, and how many
    /// inventory rows / deals reference each Season, then flags removal candidates. NEVER writes.
    ///
    /// Output: two CSVs (Collections, Seasons) + a console summary. The actual delete stays a
    /// separate, gated decision with Storm.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Sandbox: https://org00bff505.crm.dynamics.com/
        // Prod:    https://stormbasketball.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        // Seasons with a year strictly before this are flagged as "historical".
        private const int CurrentSeasonYear = 2026;

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
            Console.WriteLine("  RateCardCleanupAudit - READ ONLY (Collections & Seasons)");
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
                // ---- COLLECTIONS ----
                var collections = RetrieveAll(service, new QueryExpression("new_collection")
                { ColumnSet = new ColumnSet("new_name") });

                var prodByColl = new Dictionary<Guid, int>();
                foreach (Entity p in RetrieveAll(service, new QueryExpression("new_product") { ColumnSet = new ColumnSet("new_collection") }))
                    Bump(prodByColl, GetLookupId(p, "new_collection"));

                var invByColl = new Dictionary<Guid, int>();
                var invBySeason = new Dictionary<Guid, int>();
                foreach (Entity inv in RetrieveAll(service, new QueryExpression("new_inventory") { ColumnSet = new ColumnSet("new_collection", "new_seasonid") }))
                {
                    Bump(invByColl, GetLookupId(inv, "new_collection"));
                    Bump(invBySeason, GetLookupId(inv, "new_seasonid"));
                }

                // ---- SEASONS ----
                var seasons = RetrieveAll(service, new QueryExpression("new_season")
                { ColumnSet = new ColumnSet("new_name", "new_seasonyear") });

                var dealBySeason = new Dictionary<Guid, int>();
                foreach (Entity d in RetrieveAll(service, new QueryExpression("new_deals") { ColumnSet = new ColumnSet("new_season") }))
                    Bump(dealBySeason, GetLookupId(d, "new_season"));

                // ---- COLLECTIONS report ----
                string collPath = Path.Combine(outDir, $"RateCard_Collections_{env}_{stamp}.csv");
                var cb = new StringBuilder();
                cb.AppendLine("CollectionName,CollectionId,Products,Inventory,Verdict");
                int collEmpty = 0;
                foreach (Entity c in collections.OrderBy(x => (prodByColl.TryGetValue(x.Id, out int p) ? p : 0) + (invByColl.TryGetValue(x.Id, out int i) ? i : 0)))
                {
                    int prods = prodByColl.TryGetValue(c.Id, out int pp) ? pp : 0;
                    int invs = invByColl.TryGetValue(c.Id, out int ii) ? ii : 0;
                    string verdict = (prods == 0 && invs == 0) ? "REMOVAL CANDIDATE (unused)" : "in use";
                    if (prods == 0 && invs == 0) collEmpty++;
                    cb.AppendLine(string.Join(",", Csv(GetString(c, "new_name")), Csv(c.Id.ToString()), prods.ToString(), invs.ToString(), Csv(verdict)));
                }
                File.WriteAllText(collPath, cb.ToString(), Utf8NoBom);

                // ---- SEASONS report ----
                string seasPath = Path.Combine(outDir, $"RateCard_Seasons_{env}_{stamp}.csv");
                var sb = new StringBuilder();
                sb.AppendLine("SeasonName,SeasonId,Year,Inventory,Deals,Verdict");
                int seasEmpty = 0, seasOld = 0, seasFutureEmpty = 0;
                foreach (Entity s in seasons.OrderBy(x => x.GetAttributeValue<int>("new_seasonyear")))
                {
                    int year = s.GetAttributeValue<int>("new_seasonyear");
                    int invs = invBySeason.TryGetValue(s.Id, out int ii) ? ii : 0;
                    int deals = dealBySeason.TryGetValue(s.Id, out int dd) ? dd : 0;
                    string verdict;
                    if (invs == 0 && deals == 0)
                    {
                        // Empty FUTURE seasons are likely required as targets by the CloneMultiYearDeals
                        // automation (it looks up the season by year+suffix); do NOT flag them to delete.
                        if (year < CurrentSeasonYear) { verdict = "REMOVAL CANDIDATE (past & empty)"; seasEmpty++; }
                        else { verdict = "empty - KEEP (future multi-year clone target)"; seasFutureEmpty++; }
                    }
                    else if (year < CurrentSeasonYear) { verdict = "historical (has data)"; seasOld++; }
                    else verdict = "in use";
                    sb.AppendLine(string.Join(",", Csv(GetString(s, "new_name")), Csv(s.Id.ToString()), year.ToString(), invs.ToString(), deals.ToString(), Csv(verdict)));
                }
                File.WriteAllText(seasPath, sb.ToString(), Utf8NoBom);

                Console.WriteLine("==================== CLEANUP AUDIT ====================");
                Console.WriteLine($"Collections scanned       : {collections.Count}");
                Console.WriteLine($"  removal candidates (0/0): {collEmpty}");
                Console.WriteLine($"Seasons scanned           : {seasons.Count}");
                Console.WriteLine($"  removal candidates (past&empty): {seasEmpty}");
                Console.WriteLine($"  empty future (KEEP-clone targets): {seasFutureEmpty}");
                Console.WriteLine($"  historical with data (year<{CurrentSeasonYear}): {seasOld}");
                Console.WriteLine($"Collections report        : {collPath}");
                Console.WriteLine($"Seasons report            : {seasPath}");
                Console.WriteLine("======================================================");
                Console.WriteLine("\nNote: read-only. Deletion is a separate gated decision with Storm.");
                Bye("Done.");
            }
        }

        // ============================ HELPERS ============================
        private static void Bump(Dictionary<Guid, int> d, Guid k) { if (k != Guid.Empty) d[k] = (d.TryGetValue(k, out int v) ? v : 0) + 1; }

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
        private static string GetString(Entity e, string a) => e.Contains(a) && e[a] != null ? e[a].ToString() : "";
        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n")) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
