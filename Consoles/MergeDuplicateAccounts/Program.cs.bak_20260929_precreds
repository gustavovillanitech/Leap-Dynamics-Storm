using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MergeDuplicateAccounts
{
    /// <summary>
    /// Merges duplicate accounts (Supergraphics, Alaska Airlines) per Ray's task list after
    /// the 2026-08-21 Storm session, and optionally deletes a duplicate deal once the accounts
    /// are merged (Supergraphics: "merge accounts and delete the duplicate deal").
    ///
    /// SAFE BY DEFAULT: with MasterId/SubordinateId left empty, the tool runs DISCOVERY only -
    /// it searches accounts by name and prints each candidate with its child-record counts
    /// (deals / contacts / opportunities) so you can decide which account is the MASTER (kept)
    /// and which is the SUBORDINATE (merged in and deactivated). Fill the two GUIDs, re-run,
    /// review the dry-run, then type MERGE to execute.
    ///
    /// Dataverse MergeRequest reparents the subordinate's child records to the master and
    /// deactivates the subordinate (it is not hard-deleted).
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
        private const string UserName = "FanInteractive@stormbasketball.com";
        private const string Password = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        private const string DealEntity = "new_deals";
        private const string DealAccountLookup = "new_accountid";

        private sealed class MergePair
        {
            public string Label;
            public string SearchTerm;               // used for discovery when GUIDs are empty
            public Guid MasterId = Guid.Empty;      // account kept
            public Guid SubordinateId = Guid.Empty; // account merged in + deactivated
            public Guid DuplicateDealIdToDelete = Guid.Empty; // optional: delete after merge
        }

        // Fill MasterId/SubordinateId after reviewing the discovery output, then re-run.
        // NOTE: Supergraphics and Alaska Airlines were already merged on 2026-08-24 and were
        // REMOVED from this list so a re-run does not attempt to merge them again.
        // SearchTerm accepts several terms separated by '|' (e.g. spelling variants).
        private static readonly List<MergePair> Pairs = new List<MergePair>
        {
            new MergePair
            {
                Label = "Premera Blue Cross",
                // Covers both spellings seen in the meeting notes ("Primera" / "Premera").
                SearchTerm = "Primera|Premera",
                // Discovery first: pick master = the account with the real deal(s)/history,
                // subordinate = the duplicate to merge in. Target name should end as "Premera Blue Cross".
                MasterId = Guid.Empty,
                SubordinateId = Guid.Empty
            },
        };
        // ======================================================================

        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";

        private static void Main(string[] args)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  MergeDuplicateAccounts (Storm)");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
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
                foreach (MergePair p in Pairs)
                {
                    Console.WriteLine("\n=========================================================");
                    Console.WriteLine($"  PAIR: {p.Label}");
                    Console.WriteLine("=========================================================");

                    if (p.MasterId == Guid.Empty || p.SubordinateId == Guid.Empty)
                    {
                        Discover(service, p);
                        continue;
                    }

                    MergeOne(service, p);
                }
            }

            Bye("Done. If you only saw discovery output, fill MasterId/SubordinateId and re-run.");
        }

        // ---------- DISCOVERY ----------
        private static void Discover(IOrganizationService svc, MergePair p)
        {
            Console.WriteLine($"MasterId/SubordinateId not set -> DISCOVERY for accounts matching '{p.SearchTerm}':\n");

            // SearchTerm may hold several terms separated by '|' (e.g. spelling variants) -> OR them.
            var filter = new FilterExpression(LogicalOperator.Or);
            foreach (string t in p.SearchTerm.Split('|'))
                filter.AddCondition("name", ConditionOperator.Like, "%" + t.Trim() + "%");

            var q = new QueryExpression("account")
            {
                ColumnSet = new ColumnSet("name", "createdon", "statecode"),
                Criteria = filter
            };
            var accounts = svc.RetrieveMultiple(q).Entities;

            if (accounts.Count == 0) { Console.WriteLine("  (no accounts found)"); return; }

            foreach (Entity a in accounts.OrderBy(x => x.Contains("name") ? Convert.ToString(x["name"]) : ""))
            {
                string name = a.Contains("name") ? Convert.ToString(a["name"]) : "(no name)";
                string state = a.FormattedValues.Contains("statecode") ? a.FormattedValues["statecode"] : "?";
                string created = a.Contains("createdon") ? ((DateTime)a["createdon"]).ToString("yyyy-MM-dd") : "?";
                int deals = Count(svc, DealEntity, DealAccountLookup, a.Id);
                int contacts = Count(svc, "contact", "parentcustomerid", a.Id);
                int opps = Count(svc, "opportunity", "parentaccountid", a.Id);

                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"  {name}");
                Console.ResetColor();
                Console.WriteLine($"     Id={a.Id}  state={state}  created={created}");
                Console.WriteLine($"     deals={deals}  contacts={contacts}  opportunities={opps}");
                ListAccountDeals(svc, a.Id, p.DuplicateDealIdToDelete);
            }

            Console.WriteLine("\n  -> Decide MASTER (keep) vs SUBORDINATE (merge in). Usually the account holding the");
            Console.WriteLine("     correct 2026 deal WITH deal lines is the master. Put the two GUIDs in Pairs and re-run.");
        }

        // ---------- MERGE ----------
        private static void MergeOne(IOrganizationService svc, MergePair p)
        {
            Entity master = Retrieve(svc, "account", p.MasterId);
            Entity sub = Retrieve(svc, "account", p.SubordinateId);
            if (master == null || sub == null) { Console.WriteLine("  ERROR: master or subordinate account not found. Skipping."); return; }

            Console.WriteLine("  DRY-RUN PREVIEW:");
            Console.WriteLine($"    MASTER (keep)        : {Name(master)}  [{master.Id}]  deals={Count(svc, DealEntity, DealAccountLookup, master.Id)} contacts={Count(svc, "contact", "parentcustomerid", master.Id)} opps={Count(svc, "opportunity", "parentaccountid", master.Id)}");
            Console.WriteLine($"    SUBORDINATE (merge)  : {Name(sub)}  [{sub.Id}]  deals={Count(svc, DealEntity, DealAccountLookup, sub.Id)} contacts={Count(svc, "contact", "parentcustomerid", sub.Id)} opps={Count(svc, "opportunity", "parentaccountid", sub.Id)}");
            if (p.DuplicateDealIdToDelete != Guid.Empty)
                Console.WriteLine($"    THEN DELETE deal     : {p.DuplicateDealIdToDelete}");

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  About to MERGE '{Name(sub)}' INTO '{Name(master)}' (subordinate will be deactivated).");
            Console.ResetColor();
            Console.Write("  Type MERGE (all caps) to proceed, anything else to skip this pair: ");
            if ((Console.ReadLine() ?? "") != "MERGE") { Console.WriteLine("  Skipped."); return; }

            var merge = new MergeRequest
            {
                Target = new EntityReference("account", p.MasterId),
                SubordinateId = p.SubordinateId,
                PerformParentingChecks = false,
                UpdateContent = new Entity("account")
            };

            try
            {
                svc.Execute(merge);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  MERGE OK. Subordinate deactivated; its records reparented to master.");
                Console.ResetColor();
            }
            catch (Exception ex) { Console.WriteLine($"  MERGE FAILED: {ex.Message}"); return; }

            if (p.DuplicateDealIdToDelete != Guid.Empty)
            {
                Console.Write($"  Delete duplicate deal {p.DuplicateDealIdToDelete}? Type DELETE to confirm: ");
                if ((Console.ReadLine() ?? "") == "DELETE")
                {
                    try { svc.Delete(DealEntity, p.DuplicateDealIdToDelete); Console.WriteLine("  Deal deleted."); }
                    catch (Exception ex) { Console.WriteLine($"  Deal delete FAILED: {ex.Message}"); }
                }
                else Console.WriteLine("  Deal delete skipped.");
            }
        }

        /// <summary>Lists each deal on the account with its deal-line count, so the master
        /// (holds the real deal WITH lines) and subordinate (holds the orphan) are obvious.</summary>
        private static void ListAccountDeals(IOrganizationService svc, Guid accountId, Guid knownOrphanDealId)
        {
            var q = new QueryExpression(DealEntity)
            {
                ColumnSet = new ColumnSet("new_name"),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression(DealAccountLookup, ConditionOperator.Equal, accountId) }
                }
            };
            var deals = svc.RetrieveMultiple(q).Entities;
            foreach (Entity d in deals)
            {
                int lines = CountLines(svc, d.Id);
                string nm = d.Contains("new_name") ? Convert.ToString(d["new_name"]) : "";
                string tag = d.Id == knownOrphanDealId ? "  <-- ORPHAN duplicate (delete after merge)"
                           : lines > 0 ? "  <-- has lines (likely the real deal -> MASTER)" : "";
                Console.WriteLine($"        deal: '{nm}'  [{d.Id}]  lines={lines}{tag}");
            }
        }

        private static int CountLines(IOrganizationService svc, Guid dealId)
        {
            var q = new QueryExpression("new_deallines")
            {
                ColumnSet = new ColumnSet(false),
                Criteria = new FilterExpression { Conditions = { new ConditionExpression("new_dealid", ConditionOperator.Equal, dealId) } },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            try { return svc.RetrieveMultiple(q).Entities.Count; } catch { return -1; }
        }

        // ---------- HELPERS ----------
        private static int Count(IOrganizationService svc, string entity, string lookup, Guid accountId)
        {
            var q = new QueryExpression(entity)
            {
                ColumnSet = new ColumnSet(false),
                Criteria = new FilterExpression
                {
                    Conditions = { new ConditionExpression(lookup, ConditionOperator.Equal, accountId) }
                },
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            try { return svc.RetrieveMultiple(q).Entities.Count; } catch { return -1; }
        }

        private static Entity Retrieve(IOrganizationService svc, string entity, Guid id)
        {
            try { return svc.Retrieve(entity, id, new ColumnSet("name", "statecode")); } catch { return null; }
        }

        private static string Name(Entity a) => a.Contains("name") ? Convert.ToString(a["name"]) : "(no name)";
        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
