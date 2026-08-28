using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace DeleteOrphanDeals
{
    /// <summary>
    /// Deletes the orphan deals flagged in the "Orphan Deals_Storm Notes.xlsx" file
    /// (rows highlighted yellow), per Ray's task list after the 2026-08-21 Storm session.
    ///
    /// SAFETY MODEL (three independent nets):
    ///   1. ORPHAN GUARDRAIL - a deal is deleted ONLY if it currently has NO deal lines.
    ///      If a deal unexpectedly HAS lines (e.g. someone rebuilt them / moved it to
    ///      Closed Lost), it is SKIPPED and reported, never deleted. This protects real
    ///      pitched deals that gained data since the spreadsheet was made.
    ///   2. FULL BACKUP - every candidate's complete attribute set is dumped to CSV
    ///      BEFORE any delete, so there is a restoration reference.
    ///   3. TWO-PHASE + TYPED CONFIRM - a DryRun audit prints everything (with the
    ///      note-vs-highlight CONFLICT rows flagged in red); real deletion only runs
    ///      after typing DELETE.
    ///
    /// The Supergraphics duplicate deal is intentionally NOT here: it is handled by the
    /// MergeDuplicateAccounts tool (merge the two accounts first, then delete that deal).
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // Prod:    https://stormbasketball.crm.dynamics.com/
        // Sandbox: https://org00bff505.crm.dynamics.com/
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";

        // DryRun = true  -> AUDIT ONLY (retrieves + validates, writes the backup CSV, no deletes).
        // DryRun = false -> after the audit, deletes (asks for a typed DELETE first).
        private const bool DryRun = true;

        // Category switches for the ACTUAL delete. The dry run ALWAYS shows every row;
        // these only decide what a real (DryRun=false) run will remove. Conflicts
        // (State Street, Amazon Fashion 27/28) are HELD by default until confirmed.
        private const bool DeleteClean = true;
        private const bool DeleteVerify = true;
        private const bool DeleteConflicts = false;

        // Service account (same one every Storm console uses).
        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        private const string DealEntity = "new_deals";
        private const string DealLineEntity = "new_deallines";
        private const string DealLineDealLookup = "new_dealid";
        // ======================================================================

        // Flag = why the row is in this list. CONFLICT = highlighted yellow but the
        // spreadsheet note says keep/don't-delete; VERIFY = delete only after confirming
        // a real sibling deal exists. Both are still processed (Ray's call: delete yellow),
        // but they are printed in red so they get a last human check in the dry run.
        private enum Flag { Clean, Verify, Conflict }

        private sealed class Target
        {
            public Guid Id;
            public string Name;
            public Flag Flag;
            public string Note;
            public Target(string id, string name, Flag flag, string note)
            { Id = Guid.Parse(id); Name = name; Flag = flag; Note = note; }
        }

        // The 11 yellow orphan deals (Supergraphics handled by the merge tool, not here).
        private static readonly List<Target> Targets = new List<Target>
        {
            new Target("c6132651-a81c-f111-8341-6045bd0066ee", "STORM PEMCO 2026",                                   Flag.Clean,    "Seems like it can be deleted"),
            new Target("98c926de-b919-f111-8341-000d3a3ac02f", "Providence Swedish - 2026 - Storm",                  Flag.Clean,    "Seems like it can be deleted"),
            new Target("936abe9d-c80d-f111-8406-6045bd006af1", "TEST",                                               Flag.Clean,    "Test record"),
            new Target("272123eb-5a33-f111-88b4-6045bd081d2b", "Storm Sponsor / Amazon Groups - CPA Naming Rights",  Flag.Clean,    "Seems like it can be deleted"),
            new Target("cb0e3e38-e85a-f111-bec7-6045bd0066ee", "Alexa - 2026 - Storm",                               Flag.Conflict, "HELD - account 'Alexa' has NO other deal with lines, and the 2026-08-21 call named Amazon Alexa as a real prorated partner. Confirm the real Alexa deal lives elsewhere before deleting this one."),
            new Target("95530a8e-d026-f111-8341-000d3a3ac02f", "Storm Sponsor / Delta Dental - 2026 - Storm",        Flag.Verify,   "Possible test; confirm a real Delta Dental 2026 deal still exists"),
            new Target("92030d1b-1e0c-f111-8406-000d3a3ac02f", "Suite Level A - Full Season",                        Flag.Clean,    "Seems like it can be deleted"),
            new Target("d7faf93b-4729-f111-8341-000d3a3ac02f", "Coho Winery (sample) - 2026 - Storm",                Flag.Verify,   "If this was a test/sample, delete"),
            new Target("5adc00d8-b074-f111-ab0f-000d3a3ac02f", "State Street Investment Management",                 Flag.Conflict, "HELD - note says do not delete (real deal Bridget pitched, possibly moved to Closed Lost). Still pending Storm confirmation."),
            // Storm/Ray confirmed on the 2026-08-24 meeting: delete Amazon Fashion 27/28 (they recreate in future updates).
            new Target("280e0a31-7e66-f111-a826-6045bd0066ee", "Amazon Fashion - 2028 - Storm",                      Flag.Clean,    "Confirmed delete (2026-08-24 meeting) - auto-created future year, recreated on future updates."),
            new Target("6ea1f22a-7e66-f111-a826-6045bd0066ee", "Amazon Fashion - 2027 - Storm",                      Flag.Clean,    "Confirmed delete (2026-08-24 meeting) - auto-created future year, recreated on future updates."),
        };

        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";

        private static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;

            Console.ForegroundColor = (IsProd || !DryRun) ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  DeleteOrphanDeals - orphan deal cleanup (Storm)");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
            Console.WriteLine($"  Mode   : {(DryRun ? "DRY RUN (audit only, no deletes)" : "DELETE (will remove data)")}");
            Console.WriteLine($"  Deals in list : {Targets.Count}");
            Console.WriteLine("=========================================================");
            Console.ResetColor();
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
                // ---------- PHASE 1: AUDIT ----------
                Console.WriteLine("========== PHASE 1: AUDIT (no changes) ==========");
                var toDelete = new List<Entity>();   // enabled candidates (full records) - what a real run deletes
                var held = new List<Target>();       // candidates whose category toggle is off
                var skippedHasLines = new List<Target>();
                var notFound = new List<Target>();

                foreach (Target t in Targets)
                {
                    Entity deal = SafeRetrieve(service, t.Id);
                    if (deal == null)
                    {
                        notFound.Add(t);
                        Warn($"  NOT FOUND (already deleted?)  {t.Name}  [{t.Id}]");
                        continue;
                    }

                    int lineCount = CountDealLines(service, t.Id);
                    string storedName = deal.Contains("new_name") ? Convert.ToString(deal["new_name"]) : t.Name;

                    if (lineCount > 0)
                    {
                        skippedHasLines.Add(t);
                        Warn($"  SKIP - has {lineCount} deal line(s), NOT an orphan  '{storedName}'  [{t.Id}]");
                        continue;
                    }

                    // Candidate for deletion (no deal lines). Whether a real run removes it
                    // depends on its category toggle.
                    bool enabled = (t.Flag == Flag.Clean && DeleteClean)
                                || (t.Flag == Flag.Verify && DeleteVerify)
                                || (t.Flag == Flag.Conflict && DeleteConflicts);

                    ConsoleColor c = t.Flag == Flag.Conflict ? ConsoleColor.Red
                                   : t.Flag == Flag.Verify ? ConsoleColor.Yellow
                                   : ConsoleColor.Green;
                    Console.ForegroundColor = c;
                    Console.WriteLine($"  {(enabled ? "WILL DELETE" : "HELD (toggle off)")} [{t.Flag}]  '{storedName}'  [{t.Id}]");
                    if (t.Flag != Flag.Clean) Console.WriteLine($"      -> {t.Note}");
                    Console.ResetColor();

                    // Context: other deals on the SAME account and whether they have lines.
                    // A real sibling deal WITH lines is what makes an orphan safe to delete.
                    PrintSiblingDeals(service, deal);

                    if (enabled) toDelete.Add(deal); else held.Add(t);
                }

                // Backup every candidate (full attribute dump) before anything is deleted.
                string backupPath = WriteBackup(service, toDelete, stamp, outDir);

                Console.WriteLine("\n==================== AUDIT SUMMARY ====================");
                Console.WriteLine($"Environment            : {EnvUrl}  [{EnvTag()}]");
                Console.WriteLine($"In list                : {Targets.Count}");
                Console.WriteLine($"  will delete (enabled): {toDelete.Count}");
                Console.WriteLine($"  held (toggle off)    : {held.Count}" +
                    (held.Count > 0 ? "  -> " + string.Join("; ", held.Select(h => $"{h.Flag}:{h.Name}")) : ""));
                Console.WriteLine($"  skipped (has lines)  : {skippedHasLines.Count}");
                Console.WriteLine($"  not found            : {notFound.Count}");
                Console.WriteLine($"Toggles                : Clean={DeleteClean} Verify={DeleteVerify} Conflicts={DeleteConflicts}");
                Console.WriteLine($"Backup written         : {backupPath}");
                Console.WriteLine("======================================================\n");

                if (DryRun) { Bye("DryRun = true. No records deleted. Review the list above and the backup CSV."); return; }
                if (toDelete.Count == 0) { Bye("Nothing to delete."); return; }

                // ---------- PHASE 2: DELETE ----------
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"About to PERMANENTLY DELETE {toDelete.Count} deal(s) from {EnvUrl}.");
                int conflicts = toDelete.Count(e => Targets.First(t => t.Id == e.Id).Flag == Flag.Conflict);
                if (conflicts > 0)
                    Console.WriteLine($"  WARNING: {conflicts} of these are CONFLICT rows (note said keep). Make sure you reviewed them.");
                Console.ResetColor();
                Console.Write("Type DELETE (all caps) to proceed, anything else to cancel: ");
                if ((Console.ReadLine() ?? "") != "DELETE") { Bye("Delete cancelled by user."); return; }

                int ok = 0, err = 0;
                foreach (Entity deal in toDelete)
                {
                    try
                    {
                        service.Delete(DealEntity, deal.Id);
                        ok++;
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  DELETED {deal.Id}");
                        Console.ResetColor();
                    }
                    catch (Exception ex) { err++; Err($"FAILED {deal.Id}: {ex.Message}"); }
                }

                Console.WriteLine($"\nDelete complete. {ok} ok / {err} err. Backup: {backupPath}");
                Bye(err == 0 ? "All targeted orphan deals deleted." : "Some deletes failed - see errors above.");
            }
        }

        // ============================ HELPERS ============================

        private static Entity SafeRetrieve(CrmServiceClient svc, Guid id)
        {
            try { return svc.Retrieve(DealEntity, id, new ColumnSet(true)); }
            catch (Exception ex)
            {
                if (ex.Message.IndexOf("Does Not Exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    ex.Message.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;
                Warn($"  Retrieve error for {id}: {ex.Message}");
                return null;
            }
        }

        private static int CountDealLines(IOrganizationService svc, Guid dealId)
        {
            var q = new QueryExpression(DealLineEntity)
            {
                ColumnSet = new ColumnSet(false),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression(DealLineDealLookup, ConditionOperator.Equal, dealId) }
                },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            return svc.RetrieveMultiple(q).Entities.Count;
        }

        private static Guid GetLookupId(Entity e, string attr)
            => e.Contains(attr) && e[attr] is EntityReference r ? r.Id : Guid.Empty;

        /// <summary>Prints the other deals on this deal's account, with each one's deal-line count.
        /// A sibling deal WITH lines is the "real" deal that makes this orphan safe to delete;
        /// no sibling at all is a reason to double-check before deleting.</summary>
        private static void PrintSiblingDeals(IOrganizationService svc, Entity deal)
        {
            Guid accountId = GetLookupId(deal, "new_accountid");
            string accountName = deal.Contains("new_accountid") && deal["new_accountid"] is EntityReference ar ? (ar.Name ?? "") : "";
            if (accountId == Guid.Empty) { Console.WriteLine("        account: (none on this deal)"); return; }

            var q = new QueryExpression(DealEntity)
            {
                ColumnSet = new ColumnSet("new_name"),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression("new_accountid", ConditionOperator.Equal, accountId) }
                }
            };
            var siblings = svc.RetrieveMultiple(q).Entities.Where(s => s.Id != deal.Id).ToList();
            Console.WriteLine($"        account: {accountName}  [{accountId}]  -> {siblings.Count} other deal(s) on it");
            foreach (Entity s in siblings)
            {
                int lines = CountDealLines(svc, s.Id);
                string nm = s.Contains("new_name") ? Convert.ToString(s["new_name"]) : "";
                Console.WriteLine($"           - '{nm}'  lines={lines}{(lines > 0 ? "  <-- real deal with lines" : "")}");
            }
        }

        /// <summary>Dumps every attribute of every candidate to a flat CSV (one row per attribute).</summary>
        private static string WriteBackup(IOrganizationService svc, List<Entity> deals, string stamp, string outDir)
        {
            string path = Path.Combine(outDir, $"DeleteOrphanDeals_Backup_{EnvTag()}_{stamp}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("DealId,DealName,Attribute,Value,ValueType");
            foreach (Entity d in deals)
            {
                string name = d.Contains("new_name") ? Convert.ToString(d["new_name"]) : "";
                foreach (var kv in d.Attributes.OrderBy(k => k.Key))
                {
                    string val, type;
                    Describe(kv.Value, out val, out type);
                    sb.AppendLine(string.Join(",", Csv(d.Id.ToString()), Csv(name), Csv(kv.Key), Csv(val), Csv(type)));
                }
            }
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
            return path;
        }

        private static void Describe(object v, out string value, out string type)
        {
            if (v == null) { value = ""; type = "null"; return; }
            switch (v)
            {
                case Money m: value = m.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); type = "Money"; break;
                case OptionSetValue o: value = o.Value.ToString(); type = "OptionSet"; break;
                case EntityReference r: value = $"{r.LogicalName}:{r.Id}:{r.Name}"; type = "EntityReference"; break;
                case DateTime dt: value = dt.ToString("o"); type = "DateTime"; break;
                default: value = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture); type = v.GetType().Name; break;
            }
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(m); Console.ResetColor(); }
        private static void Err(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine("  ERROR " + m); Console.ResetColor(); }
        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
