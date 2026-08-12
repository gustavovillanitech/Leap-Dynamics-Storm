using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FixDealTotal
{
    /// <summary>
    /// Audits and (optionally) fixes Deal Line totals and Deal Net Totals across the whole org.
    ///
    /// Background: new_deallines.new_total = new_quantity * new_rate is computed by the
    /// InventoryManagement (UnifiedDealInventoryPlugin) PRE-op step; new_deals.new_total is a
    /// rollup written by its POST-op step. Historically the POST-op step did not fire on a
    /// rate-only edit, so some stored totals drifted. Ray's manual "touch one line per deal"
    /// pass could not fix sibling lines it did not touch. This tool finds and reconciles them.
    ///
    /// The fix does NOT write line/deal totals directly: it re-triggers the plugin by updating
    /// each affected line's new_quantity to its SAME current value. That fires the PRE-op
    /// recompute and the POST-op rollup, while keeping deltaQty = 0 so inventory buckets are
    /// untouched.
    ///
    /// Two categories of "wrong deal total" are separated on purpose:
    ///   * ROLLUP DRIFT  - deal HAS lines but stored total != sum(lines). These are the real
    ///                     target and are fixed by re-triggering one of the deal's lines.
    ///   * ORPHAN TOTAL  - deal has NO lines but a non-zero stored total (typically a leftover
    ///                     from the full-replace migration that wiped deal lines). Reported
    ///                     separately and NOT modified unless FixOrphanDealTotals is turned on.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Sandbox: https://org00bff505.crm.dynamics.com/
        // Prod:    https://stormbasketball.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        // DryRun = true  -> AUDIT ONLY (no writes). Produces the CSV mismatch reports.
        // DryRun = false -> after the audit, applies the fix (asks for a typed "YES", backs up first).
        private const bool DryRun = true;

        // When true, also zeroes the total of deals that have NO lines (orphan totals).
        // Leave FALSE unless it has been explicitly decided to clear those leftovers.
        private const bool FixOrphanDealTotals = false;

        // Money comparison tolerance (absorbs currency rounding; real drift is far larger).
        private const decimal Tolerance = 0.005m;

        // Service account (same one the other Storm consoles use).
        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";
        // ======================================================================

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static string _stamp;
        private static string _outDir;
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;

        private static void Main(string[] args)
        {
            _stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            _outDir = AppDomain.CurrentDomain.BaseDirectory;

            PrintBanner();
            Console.WriteLine("Type 'Y' to continue, anything else to cancel:");
            if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "Y") { Bye("Cancelled."); return; }

            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";

            Console.WriteLine("\nConnecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful!\n");

            using (service)
            {
                // ---------- PHASE A: LOAD ----------
                List<LineRow> lines = LoadDealLines(service);
                List<DealRow> deals = LoadDeals(service);
                Console.WriteLine($"Loaded {lines.Count} deal lines and {deals.Count} deals.\n");

                // ---------- PHASE B: AUDIT (read-only) ----------
                var lineIssues = new List<LineRow>();
                var expectedByDeal = new Dictionary<Guid, decimal>();
                var lineIdsByDeal = new Dictionary<Guid, List<Guid>>();

                foreach (LineRow l in lines)
                {
                    decimal expected = Math.Round(l.Quantity * l.Rate, 2, MidpointRounding.AwayFromZero);

                    if (l.DealId != Guid.Empty)
                    {
                        if (!expectedByDeal.ContainsKey(l.DealId))
                        {
                            expectedByDeal[l.DealId] = 0m;
                            lineIdsByDeal[l.DealId] = new List<Guid>();
                        }
                        expectedByDeal[l.DealId] += expected;
                        lineIdsByDeal[l.DealId].Add(l.Id);
                    }

                    if (Math.Abs(expected - l.StoredTotal) > Tolerance)
                    {
                        l.ExpectedTotal = expected;
                        lineIssues.Add(l);
                    }
                }

                var rollupIssues = new List<DealRow>();  // deal HAS lines, stored != sum(lines)
                var orphanIssues = new List<DealRow>();  // deal has NO lines, stored != 0
                foreach (DealRow d in deals)
                {
                    bool hasLines = lineIdsByDeal.ContainsKey(d.Id);
                    decimal expected = hasLines ? expectedByDeal[d.Id] : 0m;
                    if (Math.Abs(expected - d.StoredTotal) <= Tolerance) continue;

                    d.ExpectedTotal = expected;
                    d.HasLines = hasLines;
                    d.LineMismatchCount = lineIssues.Count(x => x.DealId == d.Id);
                    if (hasLines) rollupIssues.Add(d); else orphanIssues.Add(d);
                }

                lineIssues = lineIssues.OrderByDescending(x => Math.Abs(x.ExpectedTotal - x.StoredTotal)).ToList();
                rollupIssues = rollupIssues.OrderByDescending(x => Math.Abs(x.ExpectedTotal - x.StoredTotal)).ToList();
                orphanIssues = orphanIssues.OrderByDescending(x => Math.Abs(x.StoredTotal)).ToList();

                string lineReport = WriteLineReport(lineIssues);
                string dealReport = WriteDealReport(rollupIssues);
                string orphanReport = WriteOrphanReport(orphanIssues);

                Console.WriteLine("===================== AUDIT SUMMARY =====================");
                Console.WriteLine($"Environment              : {EnvUrl}");
                Console.WriteLine($"Deal lines scanned       : {lines.Count}");
                Console.WriteLine($"  line totals wrong      : {lineIssues.Count}");
                Console.WriteLine($"Deals scanned            : {deals.Count}");
                Console.WriteLine($"  ROLLUP DRIFT (has lines, will FIX)      : {rollupIssues.Count}");
                Console.WriteLine($"  ORPHAN TOTAL (no lines, info only)      : {orphanIssues.Count}");
                Console.WriteLine($"Line report              : {lineReport}");
                Console.WriteLine($"Deal (rollup) report     : {dealReport}");
                Console.WriteLine($"Orphan report            : {orphanReport}");
                Console.WriteLine("========================================================\n");

                if (DryRun) { Bye("DryRun = true. No changes made. Review the CSV reports above."); return; }

                int plannedDealFixes = rollupIssues.Count + (FixOrphanDealTotals ? orphanIssues.Count : 0);
                if (lineIssues.Count == 0 && plannedDealFixes == 0)
                { Bye("Nothing to fix under the current settings. (Orphan totals are left untouched.)"); return; }

                // ---------- PHASE C: FIX ----------
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"About to FIX: {lineIssues.Count} line(s), {rollupIssues.Count} rollup deal(s)" +
                                  (FixOrphanDealTotals ? $", and ZERO {orphanIssues.Count} orphan deal total(s)" : "") + " in:");
                Console.WriteLine($"  {EnvUrl}");
                if (!FixOrphanDealTotals && orphanIssues.Count > 0)
                    Console.WriteLine($"  ({orphanIssues.Count} orphan-total deal(s) will be LEFT UNCHANGED.)");
                Console.ResetColor();
                Console.WriteLine("Type 'YES' (uppercase) to proceed, anything else to cancel:");
                if ((Console.ReadLine() ?? "") != "YES") { Bye("Fix cancelled by user."); return; }

                string backup = WriteBackup(lineIssues, rollupIssues, orphanIssues);
                Console.WriteLine($"Backup written: {backup}\n");

                var touchedDeals = new HashSet<Guid>();
                int okLines = 0, errLines = 0;
                foreach (LineRow l in lineIssues)
                {
                    try
                    {
                        // Same value -> deltaQty = 0 (inventory untouched); fires PRE-op recompute + POST-op rollup.
                        var upd = new Entity("new_deallines", l.Id);
                        upd["new_quantity"] = l.Quantity;
                        service.Update(upd);
                        if (l.DealId != Guid.Empty) touchedDeals.Add(l.DealId);
                        okLines++;
                        Console.WriteLine($"  line {l.Id}: {Money(l.StoredTotal)} -> {Money(l.ExpectedTotal)}");
                    }
                    catch (Exception ex) { errLines++; Err($"line {l.Id}: {ex.Message}"); }
                }

                // Deals with lines whose rollup was stale but had no wrong lines (e.g. a deleted line
                // that never rolled up): re-trigger one of their lines to force the rollup.
                int okDeals = 0, errDeals = 0;
                foreach (DealRow d in rollupIssues)
                {
                    if (touchedDeals.Contains(d.Id)) continue; // already rolled up via its own lines
                    try
                    {
                        Guid anyLineId = lineIdsByDeal[d.Id][0];
                        LineRow anyLine = lines.First(x => x.Id == anyLineId);
                        var upd = new Entity("new_deallines", anyLineId);
                        upd["new_quantity"] = anyLine.Quantity;
                        service.Update(upd);
                        okDeals++;
                    }
                    catch (Exception ex) { errDeals++; Err($"deal {d.Id}: {ex.Message}"); }
                }

                // Orphan totals: only if explicitly enabled.
                int okOrphan = 0, errOrphan = 0;
                if (FixOrphanDealTotals)
                {
                    foreach (DealRow d in orphanIssues)
                    {
                        try
                        {
                            var upd = new Entity("new_deals", d.Id);
                            upd["new_total"] = new Money(0m);
                            service.Update(upd);
                            okOrphan++;
                        }
                        catch (Exception ex) { errOrphan++; Err($"orphan deal {d.Id}: {ex.Message}"); }
                    }
                }

                Console.WriteLine($"\nFix complete. Lines: {okLines} ok / {errLines} err. " +
                                  $"Rollup deals: {okDeals} ok / {errDeals} err. " +
                                  (FixOrphanDealTotals ? $"Orphans zeroed: {okOrphan} ok / {errOrphan} err." : "Orphans left untouched."));

                // ---------- VERIFICATION RE-AUDIT ----------
                Console.WriteLine("\nRe-auditing to verify...");
                List<LineRow> lines2 = LoadDealLines(service);
                List<DealRow> deals2 = LoadDeals(service);

                var expectedByDeal2 = new Dictionary<Guid, decimal>();
                var hasLines2 = new HashSet<Guid>();
                int remLines = 0;
                foreach (LineRow l in lines2)
                {
                    decimal exp = Math.Round(l.Quantity * l.Rate, 2, MidpointRounding.AwayFromZero);
                    if (l.DealId != Guid.Empty)
                    {
                        if (!expectedByDeal2.ContainsKey(l.DealId)) expectedByDeal2[l.DealId] = 0m;
                        expectedByDeal2[l.DealId] += exp;
                        hasLines2.Add(l.DealId);
                    }
                    if (Math.Abs(exp - l.StoredTotal) > Tolerance) remLines++;
                }
                int remRollup = 0, remOrphan = 0;
                foreach (DealRow d in deals2)
                {
                    bool hl = hasLines2.Contains(d.Id);
                    decimal exp = hl ? expectedByDeal2[d.Id] : 0m;
                    if (Math.Abs(exp - d.StoredTotal) <= Tolerance) continue;
                    if (hl) remRollup++; else remOrphan++;
                }

                Console.WriteLine($"Post-fix remaining: {remLines} line, {remRollup} rollup-deal, {remOrphan} orphan-deal.");
                bool clean = remLines == 0 && remRollup == 0 && (!FixOrphanDealTotals || remOrphan == 0);
                Bye(clean ? "All targeted mismatches reconciled." : "Some mismatches remain - re-run the audit and check the log.");
            }
        }

        // ============================ LOAD ============================

        private static List<LineRow> LoadDealLines(IOrganizationService service)
        {
            var q = new QueryExpression("new_deallines")
            {
                ColumnSet = new ColumnSet("new_dealid", "new_quantity", "new_rate", "new_ratecard", "new_total", "new_name")
            };
            var rows = new List<LineRow>();
            foreach (Entity e in RetrieveAll(service, q))
            {
                rows.Add(new LineRow
                {
                    Id = e.Id,
                    DealId = GetLookupId(e, "new_dealid"),
                    DealName = GetLookupName(e, "new_dealid"),
                    Name = GetString(e, "new_name"),
                    Quantity = GetDecimal(e, "new_quantity"),
                    Rate = GetMoney(e, "new_rate"),
                    RateCard = GetMoney(e, "new_ratecard"),
                    StoredTotal = GetMoney(e, "new_total")
                });
            }
            return rows;
        }

        private static List<DealRow> LoadDeals(IOrganizationService service)
        {
            var q = new QueryExpression("new_deals")
            {
                ColumnSet = new ColumnSet("new_name", "new_total")
            };
            var rows = new List<DealRow>();
            foreach (Entity e in RetrieveAll(service, q))
            {
                rows.Add(new DealRow
                {
                    Id = e.Id,
                    Name = GetString(e, "new_name"),
                    StoredTotal = GetMoney(e, "new_total")
                });
            }
            return rows;
        }

        /// <summary>Retrieves every page of a query using the paging cookie (handles >5000 rows).</summary>
        private static List<Entity> RetrieveAll(IOrganizationService service, QueryExpression query)
        {
            var all = new List<Entity>();
            query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, PagingCookie = null };
            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        // ============================ REPORTS ============================

        private static string WriteLineReport(List<LineRow> issues)
        {
            string path = Path.Combine(_outDir, $"FixDealTotal_LineAudit_{EnvTag()}_{_stamp}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("DealName,DealId,LineName,LineId,Quantity,Rate,StoredLineTotal,ExpectedLineTotal,Diff");
            foreach (LineRow l in issues)
                sb.AppendLine(string.Join(",",
                    Csv(l.DealName), Csv(l.DealId.ToString()), Csv(l.Name), Csv(l.Id.ToString()),
                    Num(l.Quantity), Num(l.Rate), Num(l.StoredTotal), Num(l.ExpectedTotal), Num(l.ExpectedTotal - l.StoredTotal)));
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return path;
        }

        private static string WriteDealReport(List<DealRow> issues)
        {
            string path = Path.Combine(_outDir, $"FixDealTotal_DealAudit_{EnvTag()}_{_stamp}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("DealName,DealId,StoredDealTotal,ExpectedDealTotal,Diff,LineMismatchCount");
            foreach (DealRow d in issues)
                sb.AppendLine(string.Join(",",
                    Csv(d.Name), Csv(d.Id.ToString()),
                    Num(d.StoredTotal), Num(d.ExpectedTotal), Num(d.ExpectedTotal - d.StoredTotal), d.LineMismatchCount.ToString()));
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return path;
        }

        private static string WriteOrphanReport(List<DealRow> issues)
        {
            string path = Path.Combine(_outDir, $"FixDealTotal_OrphanDeals_{EnvTag()}_{_stamp}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("DealName,DealId,StoredDealTotal,Note");
            foreach (DealRow d in issues)
                sb.AppendLine(string.Join(",", Csv(d.Name), Csv(d.Id.ToString()), Num(d.StoredTotal),
                    Csv("No deal lines; stored total is a leftover. Not modified unless FixOrphanDealTotals=true")));
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return path;
        }

        private static string WriteBackup(List<LineRow> lineIssues, List<DealRow> rollupIssues, List<DealRow> orphanIssues)
        {
            string path = Path.Combine(_outDir, $"FixDealTotal_Backup_{EnvTag()}_{_stamp}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("Type,RecordId,Name,Quantity,Rate,StoredTotalBefore");
            foreach (LineRow l in lineIssues)
                sb.AppendLine(string.Join(",", "DealLine", Csv(l.Id.ToString()), Csv(l.Name),
                    Num(l.Quantity), Num(l.Rate), Num(l.StoredTotal)));
            foreach (DealRow d in rollupIssues)
                sb.AppendLine(string.Join(",", "Deal(rollup)", Csv(d.Id.ToString()), Csv(d.Name), "", "", Num(d.StoredTotal)));
            foreach (DealRow d in orphanIssues)
                sb.AppendLine(string.Join(",", "Deal(orphan)", Csv(d.Id.ToString()), Csv(d.Name), "", "", Num(d.StoredTotal)));
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return path;
        }

        // ============================ HELPERS ============================

        private static Guid GetLookupId(Entity e, string attr)
            => e.Contains(attr) && e[attr] is EntityReference r ? r.Id : Guid.Empty;

        private static string GetLookupName(Entity e, string attr)
            => e.Contains(attr) && e[attr] is EntityReference r ? (r.Name ?? "") : "";

        private static decimal GetMoney(Entity e, string attr)
            => e.Contains(attr) && e[attr] is Money m ? m.Value : 0m;

        private static decimal GetDecimal(Entity e, string attr)
        {
            if (e.Contains(attr) && e[attr] != null)
            {
                if (e[attr] is Money mm) return mm.Value;
                try { return Convert.ToDecimal(e[attr]); } catch { return 0m; }
            }
            return 0m;
        }

        private static string GetString(Entity e, string attr)
            => e.Contains(attr) && e[attr] != null ? e[attr].ToString() : "";

        private static string Num(decimal v) => v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        private static string Money(decimal v) => v.ToString("N2", System.Globalization.CultureInfo.InvariantCulture);

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";

        private static void PrintBanner()
        {
            Console.ForegroundColor = (IsProd || !DryRun) ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  FixDealTotal - Deal / Deal-Line total reconciliation");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
            Console.WriteLine($"  Mode   : {(DryRun ? "DRY RUN (audit only, no writes)" : "FIX (will modify data)")}");
            Console.WriteLine($"  Orphan totals: {(FixOrphanDealTotals ? "WILL be zeroed" : "left untouched")}");
            Console.WriteLine("=========================================================");
            Console.ResetColor();
        }

        private static void Err(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ERROR " + msg);
            Console.ResetColor();
        }

        private static void Bye(string msg)
        {
            Console.WriteLine("\n" + msg);
            Console.WriteLine("Press Enter to exit...");
            Console.ReadLine();
        }

        // ============================ ROW TYPES ============================

        private class LineRow
        {
            public Guid Id;
            public Guid DealId;
            public string DealName;
            public string Name;
            public decimal Quantity;
            public decimal Rate;
            public decimal RateCard;
            public decimal StoredTotal;
            public decimal ExpectedTotal;
        }

        private class DealRow
        {
            public Guid Id;
            public string Name;
            public decimal StoredTotal;
            public decimal ExpectedTotal;
            public bool HasLines;
            public int LineMismatchCount;
        }
    }
}
