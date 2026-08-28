using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Export2026Inventory
{
    /// <summary>
    /// READ-ONLY export of the 2026 inventory list for Bridget/Tiffany to mark up
    /// (collection / name audit), per Ray's task list after the 2026-08-21 session.
    ///
    /// One row per new_inventory record whose season name contains "2026". The record
    /// GUID (new_inventoryid) is the FIRST column so any edit on the sheet can be mapped
    /// straight back to Dynamics for a fast bulk update. All business columns are exported
    /// dynamically (no hard-coded schema), with Collection / Division / Season pulled to
    /// the front. Two blank markup columns are appended for the editors.
    ///
    /// This tool NEVER writes. To deliver the "GUID hidden" version Ray asked for, keep
    /// the GUID column and just hide it in Excel/SharePoint before sharing.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        private const string InvEntity = "new_inventory";
        private const string InvSeasonLookup = "new_seasonid";
        private const string SeasonEntity = "new_season";
        private const string SeasonNameField = "new_name";
        private const string SeasonFilter = "2026";            // season name must contain this

        // If true, only seasons whose name also contains "Storm" are included
        // (excludes "Practice Facility 2026" etc.). Set false to include every 2026 season.
        private const bool StormSeasonsOnly = true;

        // Diagnostic: when true the tool does NOT export - it lists the inventory COUNT per season
        // across the whole org (including records with no season) to reconcile totals (e.g. 281 vs 282).
        private const bool CountBySeasonOnly = false;

        // System / audit fields we do not want cluttering the editable sheet.
        private static readonly HashSet<string> SkipAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "createdby","createdon","createdonbehalfby","modifiedby","modifiedon","modifiedonbehalfby",
            "ownerid","owningbusinessunit","owninguser","owningteam","statecode","statuscode",
            "versionnumber","overriddencreatedon","importsequencenumber","timezoneruleversionnumber",
            "utcconversiontimezonecode","new_inventoryid" // id is emitted explicitly as the first column
        };
        // ======================================================================

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";

        private static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;

            Console.ForegroundColor = IsProd ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  Export2026Inventory - READ ONLY");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
            Console.WriteLine($"  Season filter : name contains '{SeasonFilter}'" + (StormSeasonsOnly ? " AND 'Storm'" : ""));
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
                if (CountBySeasonOnly) { RunSeasonCount(service); Bye("Season count done (no export)."); return; }

                // ---- 1) 2026 season ids ----
                var seasonById = new Dictionary<Guid, string>();
                foreach (Entity s in RetrieveAll(service, new QueryExpression(SeasonEntity)
                {
                    ColumnSet = new ColumnSet(SeasonNameField),
                    Criteria = new FilterExpression
                    {
                        Conditions = { new ConditionExpression(SeasonNameField, ConditionOperator.Like, "%" + SeasonFilter + "%") }
                    }
                }))
                {
                    string name = s.Contains(SeasonNameField) ? Convert.ToString(s[SeasonNameField]) : "";
                    if (StormSeasonsOnly && name.IndexOf("Storm", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    seasonById[s.Id] = name;
                }
                Console.WriteLine($"2026 seasons matched : {seasonById.Count}");
                foreach (var kv in seasonById) Console.WriteLine($"   - {kv.Value}  [{kv.Key}]");
                if (seasonById.Count == 0) { Bye("No matching 2026 seasons found. Check SeasonFilter/StormSeasonsOnly."); return; }

                // ---- 2) inventory for those seasons ----
                var invQuery = new QueryExpression(InvEntity)
                {
                    ColumnSet = new ColumnSet(true),
                    Criteria = new FilterExpression
                    {
                        Conditions = { new ConditionExpression(InvSeasonLookup, ConditionOperator.In, seasonById.Keys.Cast<object>().ToArray()) }
                    }
                };
                List<Entity> inv = RetrieveAll(service, invQuery);
                Console.WriteLine($"\nInventory records    : {inv.Count}\n");
                if (inv.Count == 0) { Bye("No inventory found for those seasons."); return; }

                // ---- 3) build ordered column list across all records ----
                var allAttrs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Entity e in inv)
                    foreach (var a in e.Attributes.Keys)
                        if (!SkipAttrs.Contains(a)) allAttrs.Add(a);

                // Front-load the columns the Storm team searches by.
                string[] preferredOrder = { "new_name", "new_collection", "new_collectionid",
                    "new_division", "new_divisionid", InvSeasonLookup, "new_ratecard", "new_rate",
                    "new_quantity", "new_sold", "new_pitched", "new_unsold", "new_allocated" };

                var ordered = new List<string>();
                foreach (string p in preferredOrder)
                    if (allAttrs.Contains(p)) { ordered.Add(p); allAttrs.Remove(p); }
                // Then anything else containing collection/division/season, then the rest alphabetically.
                foreach (string a in allAttrs.Where(x => Contains(x, "collection") || Contains(x, "division") || Contains(x, "season")).OrderBy(x => x))
                    ordered.Add(a);
                foreach (string a in allAttrs.Where(x => !ordered.Contains(x)).OrderBy(x => x))
                    ordered.Add(a);

                // ---- 4) write CSV ----
                string csvPath = Path.Combine(outDir, $"Inventory2026_Export_{EnvTag()}_{stamp}.csv");
                var sb = new StringBuilder();

                var header = new List<string> { "InventoryId (GUID - do not edit)" };
                header.AddRange(ordered);
                header.Add("NEW Name (edit here)");
                header.Add("NEW Collection (edit here)");
                header.Add("Change Notes");
                sb.AppendLine(string.Join(",", header.Select(Csv)));

                foreach (Entity e in inv.OrderBy(x => Display(x, "new_name")))
                {
                    var row = new List<string> { e.Id.ToString() };
                    foreach (string a in ordered) row.Add(Display(e, a));
                    row.Add(""); row.Add(""); row.Add("");   // markup columns
                    sb.AppendLine(string.Join(",", row.Select(Csv)));
                }

                File.WriteAllText(csvPath, sb.ToString(), Utf8NoBom);

                Console.WriteLine("==================== EXPORT SUMMARY ====================");
                Console.WriteLine($"Seasons   : {seasonById.Count}");
                Console.WriteLine($"Inventory : {inv.Count}");
                Console.WriteLine($"Columns   : {ordered.Count} data + GUID + 3 markup");
                Console.WriteLine($"Report    : {csvPath}");
                Console.WriteLine("=======================================================");
                Console.WriteLine("\nNote: GUID is column A so edits map back to Dynamics. Hide column A before sharing if desired.");
                Bye("Done.");
            }
        }

        // ============================ DIAGNOSTIC ============================
        /// <summary>Counts every inventory record in the org grouped by its season name
        /// (including records with no season), to reconcile a disputed total like 281 vs 282.</summary>
        private static void RunSeasonCount(IOrganizationService service)
        {
            var seasonName = new Dictionary<Guid, string>();
            foreach (Entity s in RetrieveAll(service, new QueryExpression(SeasonEntity) { ColumnSet = new ColumnSet(SeasonNameField) }))
                seasonName[s.Id] = s.Contains(SeasonNameField) ? Convert.ToString(s[SeasonNameField]) : "(unnamed)";

            var counts = new Dictionary<string, int>();
            int total = 0;
            foreach (Entity e in RetrieveAll(service, new QueryExpression(InvEntity) { ColumnSet = new ColumnSet(InvSeasonLookup) }))
            {
                total++;
                Guid sid = e.Contains(InvSeasonLookup) && e[InvSeasonLookup] is EntityReference r ? r.Id : Guid.Empty;
                string key = sid == Guid.Empty ? "(no season)"
                           : (seasonName.TryGetValue(sid, out string nm) ? nm : "(unknown season " + sid + ")");
                counts[key] = (counts.TryGetValue(key, out int v) ? v : 0) + 1;
            }

            Console.WriteLine("\n============== INVENTORY COUNT BY SEASON ==============");
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                Console.WriteLine($"  {kv.Value,5}  {kv.Key}");
            Console.WriteLine("------------------------------------------------------");
            Console.WriteLine($"  {total,5}  TOTAL inventory records in the org");
            Console.WriteLine("======================================================");
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

        private static bool Contains(string s, string term) => s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Human-friendly value: prefers the formatted label (option sets, lookups, money).</summary>
        private static string Display(Entity e, string attr)
        {
            if (e.FormattedValues != null && e.FormattedValues.Contains(attr)) return e.FormattedValues[attr];
            if (!e.Contains(attr) || e[attr] == null) return "";
            object v = e[attr];
            if (v is Money m) return m.Value.ToString(CultureInfo.InvariantCulture);
            if (v is OptionSetValue o) return o.Value.ToString();
            if (v is EntityReference r) return string.IsNullOrEmpty(r.Name) ? r.Id.ToString() : r.Name;
            if (v is DateTime dt) return dt.ToString("yyyy-MM-dd");
            if (v is bool b) return b ? "Yes" : "No";
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

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
