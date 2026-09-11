using ClosedXML.Excel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ApplyInventoryUpdates
{
    /// <summary>
    /// Applies the 2026 inventory markup returned by the Storm team
    /// (Inventory2026_Storm_Update_Template.xlsx, sheet "2026 Inventory") back into Dynamics.
    ///
    /// The workbook is the round-trip of Export2026Inventory: column A carries the
    /// new_inventory GUID, the BLUE columns are the current CRM values and the ORANGE
    /// "New ..." columns carry the requested change.
    ///
    /// Design notes (see PROJECT_STATE.md):
    ///  - Collection / Division are NOT written on new_inventory. A real-time workflow
    ///    ("Set Collection-Division on Inventory") copies them from the PRODUCT on every
    ///    inventory Create/Update, so writing them on the inventory record is silently
    ///    reverted. This tool writes them on new_product and then stamps
    ///    new_updondemandcollectiondivision on the inventory row so the workflow pushes
    ///    the new values down.
    ///  - Everything else (name, rate, quantity, descriptions) is written on new_inventory,
    ///    keyed on the GUID from column A. Delta-only: unchanged fields are not sent.
    ///  - Rows the Storm team could not express in the template (removals, questions typed
    ///    into the "New Name" column, non-numeric rates, outlier values, rows with no GUID)
    ///    are NOT applied. They are classified as OPEN QUESTIONS and reported so they can be
    ///    confirmed in writing first.
    ///  - DryRun = true by default: no writes, report only.
    /// </summary>
    internal class Program
    {
        // ============================ CONFIGURATION ============================
        // App.config appSettings win; the constants are the fallback so the tool still
        // runs from a bare bin folder. Do not commit real credentials in the constants.
        private static readonly string EnvUrl = Cfg("EnvUrl", "https://stormbasketball.crm.dynamics.com/");
        private static readonly string UserName = Cfg("UserName", "");
        private static readonly string Password = Cfg("Password", "");
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        /// <summary>Path to the workbook returned by the Storm team.</summary>
        private static readonly string SourceWorkbook = Cfg("SourceWorkbook",
            @"C:\Customer Docs\Storm\Deal Options And PlayOff Automation\Inventory2026_Storm_Update_Template.xlsx");

        private const string SourceSheet = "2026 Inventory";

        /// <summary>true = preview + report only, no writes. Flip to false and type YES to apply.</summary>
        private static readonly bool DryRun = CfgBool("DryRun", true);

        /// <summary>Create a new_collection record when the requested collection does not exist.</summary>
        private static readonly bool AutoCreateCollections = CfgBool("AutoCreateCollections", true);

        /// <summary>Keep the product name in sync when an inventory item is renamed.</summary>
        private static readonly bool UpdateProductName = CfgBool("UpdateProductName", true);

        /// <summary>The workbook's record IDs come from PRODUCTION. When an ID is not found in
        /// the target environment, fall back to matching the item's current name inside the
        /// 2026 Storm season. This is what makes a sandbox rehearsal possible; in production
        /// every ID resolves, so the fallback never kicks in.</summary>
        private static readonly bool MatchByNameFallback = CfgBool("MatchByNameFallback", true);

        /// <summary>Set to the timestamp of a previous live run (e.g. "20260910_151204") to put that
        /// run's backup back into the environment and exit. The tool looks for
        /// InventoryUpdate_BACKUP_INVENTORY_&lt;env&gt;_&lt;stamp&gt;.csv and ..._PRODUCT_... next to the exe.
        /// Leave empty for normal operation.</summary>
        private static readonly string RestoreFromBackup = Cfg("RestoreFromBackup", "");

        /// <summary>PILOT MODE. Comma-separated Excel row numbers (e.g. "3,13,111") to apply ONLY those
        /// rows. Use it to write a handful of records first, confirm in the UI that the
        /// Set Collection-Division workflow propagated, and only then run the whole set.
        /// Leave empty to process every row.</summary>
        private static readonly string PilotExcelRows = Cfg("PilotExcelRows", "");

        /// <summary>What to do with the rows the Storm team marked for removal, once the tool has
        /// checked whether each one is used on a deal line.
        ///   Report     - report only (default).
        ///   Deactivate - set statecode Inactive on the rows that NO deal line uses.
        ///   Delete     - delete the rows that NO deal line uses.
        /// A row that IS used on a deal line is NEVER touched in any mode - it is reported so
        /// Storm can decide, because deleting it would break the deal line pointing at it.</summary>
        private static readonly string RemovalMode = Cfg("RemovalMode", "Report");

        /// <summary>Season filter for the name fallback (same rule as Export2026Inventory).</summary>
        private const string SeasonFilter = "2026";
        private const bool StormSeasonsOnly = true;

        // Unusually large moves are APPLIED and flagged with a note in the report - they are not
        // blocked. A rate or quantity is the client's own number, it touches nothing downstream
        // (deal lines keep their own rate), and it is corrected by writing another number.
        // Only what cannot be executed (non-numeric) or is irreversible (deleting a used item,
        // creating a master record under an uncertain name) is held back.
        private const decimal RateOutlierRatio = 5m;        // new rate >= 5x the current rate
        private const decimal NewRateOutlierFloor = 500000m; // brand-new rate at or above this
        private const decimal QtyOutlierRatio = 5m;

        // ---- entities / attributes (see DataDictionary.xlsx) ----
        private const string InvEntity = "new_inventory";
        private const string ProdEntity = "new_product";
        private const string CollEntity = "new_collection";
        private const string DivEntity = "new_division";
        private const string NameField = "new_name";

        private const string InvCollection = "new_collection";
        private const string InvDivision = "new_division";
        private const string InvProduct = "new_productid";
        private const string InvSeason = "new_seasonid";
        private const string InvRate = "new_rate";
        private const string InvQuantity = "new_quantity";
        private const string InvExtDesc = "new_description";
        private const string InvIntDesc = "new_internaldescription";
        private const string InvIsPackage = "new_ispackage";
        private const string InvUpdOnDemand = "new_updondemandcollectiondivision";

        private const string ProdCollection = "new_collection";
        private const string ProdDivision = "new_division";
        // Pl.Inventory.TotalCalculatedField mirrors these two onto the inventory on every
        // rate/quantity change, so the PRODUCT owns the descriptions - see PROJECT_STATE.
        private const string ProdExtDesc = "new_descriptionorspecifications";   // -> inventory new_description
        private const string ProdIntDesc = "new_internaldescription";           // -> inventory new_internaldescription

        /// <summary>Unambiguous misspellings in the proposed names. Corrected on load and
        /// listed in the report so the correction can be disclosed to the client.</summary>
        private static readonly Dictionary<string, string> Typos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Social Medai - Game/Player/Stat Highlight Video", "Social Media - Game/Player/Stat Highlight Video" },
            { "Social Media - Milestone Moments - Players/Team Accorlades", "Social Media - Milestone Moments - Players/Team Accolades" },
            { "Special Olympics Clinics - Suppoting Partner", "Special Olympics Clinics - Supporting Partner" },
            { "Free Throw Line Pacakge", "Free Throw Line Package" }
        };

        /// <summary>Proposed names that look like they may contain a typo. They are loaded exactly as
        /// written - the note only makes that visible in the report. Not a question for the client.</summary>
        private static readonly Dictionary<string, string> AmbiguousNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Social Media - Game Day Graphics = Graphics/Photo", "uses \"=\" instead of \"-\"" },
            { "Social Media - Facebook/Insta/Twitter/X/Combo", "trailing \"/Combo\" is inconsistent with the sibling row" }
        };
        // ======================================================================

        private static bool IsProd => EnvUrl.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string EnvTag() => IsProd ? "PROD" : "SANDBOX";
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly List<string> Log = new List<string>();

        // ---------------------------------------------------------------- row model
        private sealed class SheetRow
        {
            public int ExcelRow;
            public Guid InventoryId;
            public bool HasId;
            public string Name, NewName, Collection, NewCollection, Division, NewDivision;
            public string Rate, NewRate, Quantity, NewQuantity;
            public string ExtDesc, NewExtDesc, IntDesc, NewIntDesc;
            public string IsPackage, NewIsPackage, Playoff, Notes;
        }

        private sealed class Change
        {
            public int ExcelRow; public Guid Id; public string Item, Field, Current, New, Note, Target;
            public bool MatchedByName;
        }

        private sealed class OpenItem
        {
            public string ExcelRow, Item, Topic, WhatTheFileSays, WhatWeNeed;
        }

        /// <summary>A row the Storm team marked for removal, plus what the CRM check found.</summary>
        private sealed class Removal
        {
            public int ExcelRow; public Guid Id; public string Item, SaysWhat, Conflict;
            public bool FoundInCrm;
            public int DealLines; public List<string> Deals = new List<string>();
            public int DealLinesViaProduct;
            public bool Safe { get { return FoundInCrm && DealLines == 0 && DealLinesViaProduct == 0; } }
        }

        // ---------------------------------------------------------------- main
        private static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;

            Console.ForegroundColor = IsProd ? ConsoleColor.Red : ConsoleColor.Cyan;
            Console.WriteLine("=========================================================");
            Console.WriteLine("  ApplyInventoryUpdates - 2026 inventory markup loader");
            Console.WriteLine($"  Target : {EnvUrl}  [{EnvTag()}]");
            Console.WriteLine($"  Source : {SourceWorkbook}");
            Console.WriteLine($"  Mode   : {(DryRun ? "DRY RUN (no writes)" : "*** LIVE - WILL WRITE ***")}");
            Console.WriteLine("=========================================================");
            Console.ResetColor();

            // ---- 0) restore mode: put a backup back and exit, no workbook involved ----
            if (!string.IsNullOrEmpty(RestoreFromBackup)) { RunRestore(outDir, stamp); return; }

            if (!File.Exists(SourceWorkbook)) { Bye("Source workbook not found: " + SourceWorkbook); return; }

            // ---- 1) read + classify the workbook (no CRM needed) ----
            List<SheetRow> rows;
            try { rows = ReadSheet(SourceWorkbook); }
            catch (Exception ex) { Bye("Could not read the workbook: " + ex.Message); return; }
            Console.WriteLine($"\nRows read from \"{SourceSheet}\" : {rows.Count}");

            var changes = new List<Change>();
            var open = new List<OpenItem>();
            var removals = new List<Removal>();
            Classify(rows, changes, open, removals);

            Console.WriteLine($"Planned changes            : {changes.Count}");
            Console.WriteLine($"Open questions (not applied): {open.Count}");

            if (!string.IsNullOrWhiteSpace(PilotExcelRows))
            {
                var keep = new HashSet<int>(PilotExcelRows.Split(',')
                    .Select(x => x.Trim()).Where(x => x.Length > 0).Select(int.Parse));
                int before = changes.Count;
                changes.RemoveAll(c => !keep.Contains(c.ExcelRow));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\n*** PILOT MODE: rows {PilotExcelRows} only - {changes.Count} of {before} changes kept ***");
                Console.ResetColor();
                if (changes.Count == 0) { Bye("No changes for those rows. Check PilotExcelRows."); return; }
            }
            Console.WriteLine();

            // ---- 2) connect ----
            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
            Console.WriteLine("Connecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful.\n");

            using (service)
            {
                // ---- 3) snapshot CRM ----
                var collByName = LoadLookup(service, CollEntity);
                var divByName = LoadLookup(service, DivEntity);
                Console.WriteLine($"Collections in CRM : {collByName.Count}");
                Console.WriteLine($"Divisions in CRM   : {divByName.Count}");

                var invById = LoadInventory(service, changes.Select(c => c.Id).Distinct().ToList());
                int byId = changes.Select(c => c.Id).Distinct().Count(id => invById.ContainsKey(id));
                Console.WriteLine($"Inventory records matched by ID : {byId} of {changes.Select(c => c.Id).Distinct().Count()}");

                // The workbook was exported from PRODUCTION, so its GUIDs only resolve there.
                // To rehearse the same load in another environment (sandbox), fall back to
                // matching on the item's CURRENT name within the 2026 Storm season(s).
                Dictionary<string, List<Entity>> invByName = null;
                if (byId == 0 || MatchByNameFallback)
                {
                    invByName = LoadInventoryBySeason(service);
                    Console.WriteLine($"Inventory in the 2026 Storm season(s) : {invByName.Sum(k => k.Value.Count)} records / {invByName.Count} distinct names");
                }
                Console.WriteLine();

                var remapped = new Dictionary<Guid, Guid>();
                var unresolved = new HashSet<Guid>();
                foreach (Guid id in changes.Select(c => c.Id).Distinct().Where(x => !invById.ContainsKey(x)))
                {
                    Change any = changes.First(c => c.Id == id);
                    List<Entity> hits = null;
                    if (invByName != null) invByName.TryGetValue(Key(any.Item ?? ""), out hits);

                    if (hits != null && hits.Count == 1 && !remapped.ContainsValue(hits[0].Id))
                    {
                        Entity e = hits[0];
                        remapped[id] = e.Id;
                        invById[e.Id] = e;
                        Say($"row {any.ExcelRow} \"{any.Item}\" matched BY NAME -> {e.Id}");
                    }
                    else if (hits != null && hits.Count == 1)
                    {
                        // two workbook rows carry the same current name and would land on one record
                        unresolved.Add(id);
                        open.Add(new OpenItem
                        {
                            ExcelRow = any.ExcelRow.ToString(), Item = any.Item, Topic = "Duplicate name in the workbook",
                            WhatTheFileSays = $"Two rows are called \"{any.Item}\" and only one record with that name exists in {EnvTag()}.",
                            WhatWeNeed = "Confirm which row belongs to which record."
                        });
                    }
                    else
                    {
                        unresolved.Add(id);
                        open.Add(new OpenItem
                        {
                            ExcelRow = any.ExcelRow.ToString(),
                            Item = any.Item,
                            Topic = "Record not found",
                            WhatTheFileSays = hits != null && hits.Count > 1
                                ? $"Record ID {id} is not in {EnvTag()} and the name matches {hits.Count} records there."
                                : $"Record ID {id} is in the workbook but not in {EnvTag()}, and no item with that name was found.",
                            WhatWeNeed = "Confirm whether this item was deleted or renamed on your side."
                        });
                    }
                }
                foreach (Change c in changes) { Guid nid; if (remapped.TryGetValue(c.Id, out nid)) { c.Id = nid; c.MatchedByName = true; } }
                changes.RemoveAll(c => unresolved.Contains(c.Id) || !invById.ContainsKey(c.Id));
                if (remapped.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"{remapped.Count} records were matched BY NAME, not by ID. This is a rehearsal mode -");
                    Console.WriteLine("the workbook's IDs belong to production. Review the \"Matched by\" column in the report.");
                    Console.ResetColor();
                }
                Console.WriteLine($"Records available to update : {changes.Select(c => c.Id).Distinct().Count()}");

                // guard: one product renamed two different ways
                GuardConflictingProductRenames(changes, invById, open);
                GuardConflictingProductValues(changes, invById, open);

                // resolve collection / division targets
                var missingColls = changes.Where(c => c.Field == "Collection")
                                          .Select(c => c.New).Distinct(StringComparer.OrdinalIgnoreCase)
                                          .Where(n => !Resolve(collByName, n).HasValue).ToList();
                var missingDivs = changes.Where(c => c.Field == "Division")
                                         .Select(c => c.New).Distinct(StringComparer.OrdinalIgnoreCase)
                                         .Where(n => !Resolve(divByName, n).HasValue).ToList();

                foreach (string m in missingColls)
                    Console.WriteLine($"  Collection not in CRM : \"{m}\" -> " + (AutoCreateCollections ? "will be created" : "BLOCKED"));
                foreach (string m in missingDivs)
                    Console.WriteLine($"  Division not in CRM   : \"{m}\" -> BLOCKED (divisions are never auto-created)");

                if (missingDivs.Count > 0)
                {
                    foreach (Change c in changes.Where(c => c.Field == "Division" && missingDivs.Contains(c.New, StringComparer.OrdinalIgnoreCase)).ToList())
                    {
                        open.Add(new OpenItem
                        {
                            ExcelRow = c.ExcelRow.ToString(), Item = c.Item, Topic = "Division does not exist",
                            WhatTheFileSays = $"New Division = \"{c.New}\"",
                            WhatWeNeed = "Confirm the exact division name to create."
                        });
                        changes.Remove(c);
                    }
                }
                if (!AutoCreateCollections && missingColls.Count > 0)
                {
                    foreach (Change c in changes.Where(c => c.Field == "Collection" && missingColls.Contains(c.New, StringComparer.OrdinalIgnoreCase)).ToList())
                    {
                        open.Add(new OpenItem
                        {
                            ExcelRow = c.ExcelRow.ToString(), Item = c.Item, Topic = "Collection does not exist",
                            WhatTheFileSays = $"New Collection = \"{c.New}\"", WhatWeNeed = "Confirm the exact collection name to create."
                        });
                        changes.Remove(c);
                    }
                }

                // ---- 4) snapshot the products, then drop no-ops against the live data ----
                // Descriptions live on the PRODUCT (Pl.Inventory.TotalCalculatedField mirrors them
                // down on every rate/quantity change), so the delta must compare against the product.
                var productIds = changes.Select(c => invById[c.Id].GetAttributeValue<EntityReference>(InvProduct))
                                        .Where(p => p != null).Select(p => p.Id).Distinct().ToList();
                Dictionary<Guid, Entity> prodById = LoadProducts(service, productIds);
                Console.WriteLine($"Products referenced       : {prodById.Count}");

                int noop = DropNoOps(changes, invById, prodById, collByName, divByName);
                Console.WriteLine($"\nAlready up to date in CRM (skipped) : {noop}");
                Console.WriteLine($"Net changes to write                : {changes.Count}");

                // ---- 4b) removals: check deal-line usage before asking anyone anything ----
                CheckRemovals(service, removals);
                foreach (Removal rm in removals) open.Add(RemovalQuestion(rm));
                if (removals.Count > 0)
                {
                    int safe = removals.Count(x => x.Safe), used = removals.Count(x => x.FoundInCrm && !x.Safe);
                    Console.WriteLine($"\nMarked for removal        : {removals.Count}  ({safe} unused, {used} used on deals, {removals.Count - safe - used} not found)");
                }

                // ---- 5) report ----
                string reportPath = Path.Combine(outDir, $"InventoryUpdate_{(DryRun ? "PREVIEW" : "APPLIED")}_{EnvTag()}_{stamp}.xlsx");
                WriteReport(reportPath, changes, open, invById, collByName);
                Console.WriteLine($"\nReport : {reportPath}");

                if (DryRun)
                {
                    if (Log.Count > 0)
                    {
                        string dryLog = Path.Combine(outDir, $"InventoryUpdate_LOG_PREVIEW_{EnvTag()}_{stamp}.txt");
                        File.WriteAllLines(dryLog, Log, Utf8NoBom);
                        Console.WriteLine($"Log    : {dryLog}");
                    }
                    Bye("DRY RUN complete. No records were modified.");
                    return;
                }

                // ---- 6) apply ----
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"\nAbout to write {changes.Count} changes to {EnvTag()}. Type YES to continue:");
                Console.ResetColor();
                if ((Console.ReadLine() ?? "").Trim() != "YES") { Bye("Cancelled - nothing was written."); return; }

                // Backup BOTH sides before the first write: the inventory rows and the products
                // that carry collection / division / name / descriptions. Restore with RestoreFromBackup=<stamp>.
                string bkInv = Path.Combine(outDir, $"InventoryUpdate_BACKUP_INVENTORY_{EnvTag()}_{stamp}.csv");
                string bkProd = Path.Combine(outDir, $"InventoryUpdate_BACKUP_PRODUCT_{EnvTag()}_{stamp}.csv");
                WriteBackup(bkInv, changes, invById);
                WriteProductBackup(bkProd, prodById);
                Console.WriteLine($"Backup (inventory, {changes.Select(c => c.Id).Distinct().Count()} records) : {bkInv}");
                Console.WriteLine($"Backup (product,   {prodById.Count} records) : {bkProd}");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine($"To roll this run back: set RestoreFromBackup={stamp} in App.config and run again.");
                Console.ResetColor();
                Console.WriteLine();

                Apply(service, changes, invById, collByName, divByName, missingColls, removals);

                string logPath = Path.Combine(outDir, $"InventoryUpdate_LOG_{EnvTag()}_{stamp}.txt");
                File.WriteAllLines(logPath, Log, Utf8NoBom);
                Console.WriteLine($"\nLog : {logPath}");
                Bye("Done.");
            }
        }

        // ---------------------------------------------------------------- read
        private static List<SheetRow> ReadSheet(string path)
        {
            var list = new List<SheetRow>();
            using (var wb = new XLWorkbook(path))
            {
                IXLWorksheet ws = wb.Worksheet(SourceSheet);
                int last = ws.LastRowUsed().RowNumber();
                for (int i = 2; i <= last; i++)
                {
                    IXLRow r = ws.Row(i);
                    var sr = new SheetRow
                    {
                        ExcelRow = i,
                        Name = Cell(r, 2),
                        NewName = Cell(r, 3),
                        Collection = Cell(r, 4),
                        NewCollection = Cell(r, 5),
                        Division = Cell(r, 6),
                        NewDivision = Cell(r, 7),
                        Rate = Cell(r, 8),
                        NewRate = Cell(r, 9),
                        Quantity = Cell(r, 10),
                        NewQuantity = Cell(r, 11),
                        ExtDesc = Cell(r, 12),
                        NewExtDesc = Cell(r, 13),
                        IntDesc = Cell(r, 14),
                        NewIntDesc = Cell(r, 15),
                        IsPackage = Cell(r, 16),
                        NewIsPackage = Cell(r, 17),
                        Playoff = Cell(r, 18),
                        Notes = Cell(r, 19)
                    };
                    string id = Cell(r, 1);
                    Guid g;
                    sr.HasId = Guid.TryParse(id, out g);
                    sr.InventoryId = g;
                    if (string.IsNullOrEmpty(sr.Name) && !sr.HasId) continue;
                    list.Add(sr);
                }
            }
            return list;
        }

        private static string Cell(IXLRow r, int col) => (r.Cell(col).GetFormattedString() ?? "").Trim();

        // ---------------------------------------------------------------- classify
        private static void Classify(List<SheetRow> rows, List<Change> changes, List<OpenItem> open, List<Removal> removals)
        {
            foreach (SheetRow r in rows)
            {
                bool markedRemove = IsRemoveInstruction(r.NewName) ||
                                    string.Equals(r.NewCollection, "REMOVE", StringComparison.OrdinalIgnoreCase);

                if (!r.HasId)
                {
                    open.Add(new OpenItem
                    {
                        ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "Row without record ID",
                        WhatTheFileSays = "Row added by hand to the sheet (column A is empty). Rate given: " + (string.IsNullOrEmpty(r.NewRate) ? "n/a" : r.NewRate),
                        WhatWeNeed = "Is this a new item to create, or an edit to an existing one? If existing, which one?"
                    });
                    continue;
                }

                if (markedRemove)
                {
                    var says = new List<string>();
                    if (!string.IsNullOrEmpty(r.NewName)) says.Add($"New Name = \"{r.NewName}\"");
                    if (string.Equals(r.NewCollection, "REMOVE", StringComparison.OrdinalIgnoreCase)) says.Add("New Collection = \"REMOVE\"");
                    var also = new List<string>();
                    if (!string.IsNullOrEmpty(r.NewRate)) also.Add("a new rate of " + r.NewRate);
                    if (!string.IsNullOrEmpty(r.NewDivision)) also.Add("a new division of " + r.NewDivision);
                    string conflict = also.Count > 0 ? "The same row also asks for " + string.Join(" and ", also) + "." : "";
                    removals.Add(new Removal
                    {
                        ExcelRow = r.ExcelRow, Id = r.InventoryId, Item = r.Name,
                        SaysWhat = string.Join("; ", says), Conflict = conflict
                    });
                    continue;
                }

                string newName = r.NewName;
                if (!string.IsNullOrEmpty(newName) && IsQuestionInstruction(newName))
                {
                    open.Add(new OpenItem
                    {
                        ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "Question written in the New Name column",
                        WhatTheFileSays = $"New Name = \"{newName}\"",
                        WhatWeNeed = "This reads as a note rather than a name. What should this item be called?"
                    });
                    newName = "";
                }

                // ---- name ----
                if (!string.IsNullOrEmpty(newName))
                {
                    string corrected;
                    if (Typos.TryGetValue(newName, out corrected))
                        changes.Add(New(r, "Name", r.Name, corrected, $"typo corrected from \"{newName}\""));
                    else
                    {
                        string why;
                        AmbiguousNames.TryGetValue(newName, out why);
                        changes.Add(New(r, "Name", r.Name, newName,
                            why == null ? "" : "loaded exactly as written (" + why + ")"));
                    }
                }

                // ---- collection ----
                if (!string.IsNullOrEmpty(r.NewCollection) && !string.Equals(r.NewCollection, "REMOVE", StringComparison.OrdinalIgnoreCase))
                    changes.Add(New(r, "Collection", r.Collection, r.NewCollection, ""));

                // ---- division ----
                if (!string.IsNullOrEmpty(r.NewDivision))
                {
                    if (string.Equals(r.NewDivision, "Storm", StringComparison.OrdinalIgnoreCase))
                        changes.Add(New(r, "Division", r.Division, r.NewDivision, ""));
                    else
                        open.Add(new OpenItem
                        {
                            ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "New division",
                            WhatTheFileSays = $"New Division = \"{r.NewDivision}\"",
                            WhatWeNeed = "The workbook says \"Practice Facility\", the notes tab says \"new division BECU STORM CENTER\" and one item is renamed \"BECU Storm Center Tour\". Which single name should we create?"
                        });
                }

                // ---- rate ----
                if (!string.IsNullOrEmpty(r.NewRate))
                {
                    decimal? nv = Num(r.NewRate), cv = Num(r.Rate);
                    if (!nv.HasValue)
                        open.Add(new OpenItem
                        {
                            ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "Rate is not a number",
                            WhatTheFileSays = $"New Rate = \"{r.NewRate}\"", WhatWeNeed = "Please confirm the exact figure."
                        });
                    else if (!(cv.HasValue && Math.Abs(cv.Value - nv.Value) < 0.005m))
                    {
                        string note = "";
                        if (cv.HasValue && cv.Value > 0 && nv.Value / cv.Value >= RateOutlierRatio)
                            note = $"unusually large increase ({nv.Value / cv.Value:N0}x) - loaded as written";
                        else if (!cv.HasValue && nv.Value >= NewRateOutlierFloor)
                            note = "unusually large new rate - loaded as written";
                        changes.Add(New(r, "Rate", r.Rate, nv.Value.ToString("F2", CultureInfo.InvariantCulture), note));
                    }
                }

                // ---- quantity ----
                if (!string.IsNullOrEmpty(r.NewQuantity))
                {
                    decimal? nq = Num(r.NewQuantity), cq = Num(r.Quantity);
                    if (!nq.HasValue)
                        open.Add(new OpenItem
                        {
                            ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "Quantity is not a number",
                            WhatTheFileSays = $"New Quantity = \"{r.NewQuantity}\"", WhatWeNeed = "Please confirm the quantity."
                        });
                    else if (!(cq.HasValue && cq.Value == nq.Value))
                    {
                        string note = cq.HasValue && cq.Value > 0 && nq.Value / cq.Value >= QtyOutlierRatio
                            ? $"unusually large increase ({nq.Value / cq.Value:N0}x) - loaded as written" : "";
                        changes.Add(New(r, "Quantity", r.Quantity, nq.Value.ToString("G29", CultureInfo.InvariantCulture), note));
                    }
                }

                // ---- descriptions ----
                if (!string.IsNullOrEmpty(r.NewExtDesc)) changes.Add(New(r, "External Description", r.ExtDesc, r.NewExtDesc, ""));
                if (!string.IsNullOrEmpty(r.NewIntDesc)) changes.Add(New(r, "Internal Description", r.IntDesc, r.NewIntDesc, ""));

                // ---- is package ----
                if (!string.IsNullOrEmpty(r.NewIsPackage) && !string.Equals(r.NewIsPackage, r.IsPackage, StringComparison.OrdinalIgnoreCase))
                    open.Add(new OpenItem
                    {
                        ExcelRow = r.ExcelRow.ToString(), Item = r.Name, Topic = "Flagged as a package",
                        WhatTheFileSays = $"Is Package: {r.IsPackage} -> {r.NewIsPackage}",
                        WhatWeNeed = "A package needs its component items. Which items make it up, and in what quantity?"
                    });
            }

            // one question for the whole playoff column
            int yes = rows.Count(x => string.Equals(x.Playoff, "Yes", StringComparison.OrdinalIgnoreCase));
            int no = rows.Count(x => string.Equals(x.Playoff, "No", StringComparison.OrdinalIgnoreCase));
            if (yes + no > 0)
                open.Add(new OpenItem
                {
                    ExcelRow = "Column R", Item = "(all items)", Topic = "Playoff assets",
                    WhatTheFileSays = $"{yes} items marked \"Yes\" and {no} marked \"No\". The note on the New Inventory tab says a \"Yes\" means the asset could possibly be sold in the playoffs.",
                    WhatWeNeed = "Should we create a separate playoff inventory item for each of those assets now, or record the \"possible in playoffs\" flag on the existing items and build the playoff items later?"
                });
        }

        private static Change New(SheetRow r, string field, string cur, string val, string note) =>
            new Change { ExcelRow = r.ExcelRow, Id = r.InventoryId, Item = r.Name, Field = field, Current = cur, New = val, Note = note };

        private static bool IsRemoveInstruction(string t) =>
            !string.IsNullOrEmpty(t) && t.TrimStart().StartsWith("remove", StringComparison.OrdinalIgnoreCase);

        private static bool IsQuestionInstruction(string t)
        {
            if (string.IsNullOrEmpty(t)) return false;
            string s = t.ToLowerInvariant();
            return s.Contains("come back") || s.Contains("different from above") || s.Contains("?");
        }

        private static decimal? Num(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return null;
            string t = v.Replace("$", "").Replace(",", "").Trim();
            decimal d;
            return decimal.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? (decimal?)d : null;
        }

        // ---------------------------------------------------------------- CRM snapshot
        private static Dictionary<string, Guid> LoadLookup(IOrganizationService svc, string entity)
        {
            var d = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            foreach (Entity e in RetrieveAll(svc, new QueryExpression(entity) { ColumnSet = new ColumnSet(NameField) }))
            {
                string n = e.GetAttributeValue<string>(NameField);
                if (string.IsNullOrWhiteSpace(n)) continue;
                string k = Key(n);
                if (!d.ContainsKey(k)) d[k] = e.Id;   // first record wins; duplicates differ only by case/wording
            }
            return d;
        }

        /// <summary>Case-insensitive key that also treats "AND" and "&" as the same word.</summary>
        private static string Key(string n) =>
            string.Join(" ", n.Replace("&", " and ").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

        private static Guid? Resolve(Dictionary<string, Guid> map, string name)
        {
            Guid g;
            return map.TryGetValue(Key(name), out g) ? (Guid?)g : null;
        }

        private static Dictionary<Guid, Entity> LoadInventory(IOrganizationService svc, List<Guid> ids)
        {
            var d = new Dictionary<Guid, Entity>();
            var cols = new ColumnSet(NameField, InvCollection, InvDivision, InvProduct, InvSeason,
                                     InvRate, InvQuantity, InvExtDesc, InvIntDesc, InvIsPackage);
            for (int i = 0; i < ids.Count; i += 400)
            {
                object[] chunk = ids.Skip(i).Take(400).Cast<object>().ToArray();
                var q = new QueryExpression(InvEntity)
                {
                    ColumnSet = cols,
                    Criteria = { Conditions = { new ConditionExpression("new_inventoryid", ConditionOperator.In, chunk) } }
                };
                foreach (Entity e in RetrieveAll(svc, q)) d[e.Id] = e;
            }
            return d;
        }

        /// <summary>Every inventory record in the 2026 Storm season(s), indexed by normalised name.
        /// Used only as a fallback when the workbook's production IDs do not exist here.</summary>
        private static Dictionary<string, List<Entity>> LoadInventoryBySeason(IOrganizationService svc)
        {
            var seasonIds = new List<object>();
            foreach (Entity s in RetrieveAll(svc, new QueryExpression("new_season")
            {
                ColumnSet = new ColumnSet(NameField),
                Criteria = { Conditions = { new ConditionExpression(NameField, ConditionOperator.Like, "%" + SeasonFilter + "%") } }
            }))
            {
                string n = s.GetAttributeValue<string>(NameField) ?? "";
                if (StormSeasonsOnly && n.IndexOf("Storm", StringComparison.OrdinalIgnoreCase) < 0) continue;
                seasonIds.Add(s.Id);
            }

            var byName = new Dictionary<string, List<Entity>>(StringComparer.Ordinal);
            if (seasonIds.Count == 0) return byName;

            var q = new QueryExpression(InvEntity)
            {
                ColumnSet = new ColumnSet(NameField, InvCollection, InvDivision, InvProduct, InvSeason,
                                          InvRate, InvQuantity, InvExtDesc, InvIntDesc, InvIsPackage),
                Criteria = { Conditions = { new ConditionExpression(InvSeason, ConditionOperator.In, seasonIds.ToArray()) } }
            };
            foreach (Entity e in RetrieveAll(svc, q))
            {
                string k = Key(e.GetAttributeValue<string>(NameField) ?? "");
                if (k.Length == 0) continue;
                if (!byName.ContainsKey(k)) byName[k] = new List<Entity>();
                byName[k].Add(e);
            }
            return byName;
        }

        /// <summary>Two inventory rows can point at ONE product. Collection and Division are
        /// written on the product, so two rows asking for different values would silently
        /// overwrite each other. Flag both instead of letting the last write win.</summary>
        private static void GuardConflictingProductValues(List<Change> changes, Dictionary<Guid, Entity> inv, List<OpenItem> open)
        {
            foreach (string field in new[] { "Collection", "Division", "External Description", "Internal Description" })
            {
                string f = field;
                var byProduct = new Dictionary<Guid, HashSet<string>>();
                foreach (Change c in changes.Where(x => x.Field == f))
                {
                    Entity e;
                    if (!inv.TryGetValue(c.Id, out e)) continue;
                    var pr = e.GetAttributeValue<EntityReference>(InvProduct);
                    if (pr == null) continue;
                    if (!byProduct.ContainsKey(pr.Id)) byProduct[pr.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    byProduct[pr.Id].Add(c.New);
                }
                foreach (var kv in byProduct.Where(k => k.Value.Count > 1))
                {
                    Guid pid = kv.Key;
                    foreach (Change c in changes.Where(x => x.Field == f && inv.ContainsKey(x.Id)
                             && inv[x.Id].GetAttributeValue<EntityReference>(InvProduct) != null
                             && inv[x.Id].GetAttributeValue<EntityReference>(InvProduct).Id == pid).ToList())
                    {
                        open.Add(new OpenItem
                        {
                            ExcelRow = c.ExcelRow.ToString(), Item = c.Item, Topic = "Two items share one product",
                            WhatTheFileSays = $"The same product would receive two different {f.ToLowerInvariant()} values: " + string.Join(" / ", kv.Value.Select(v => v.Length > 60 ? v.Substring(0, 60) + "..." : v)),
                            WhatWeNeed = "Confirm which one applies, or confirm these should become two separate products."
                        });
                        changes.Remove(c);
                    }
                }
            }
        }

        private static void GuardConflictingProductRenames(List<Change> changes, Dictionary<Guid, Entity> inv, List<OpenItem> open)
        {
            var byProduct = new Dictionary<Guid, HashSet<string>>();
            foreach (Change c in changes.Where(x => x.Field == "Name"))
            {
                Entity e;
                if (!inv.TryGetValue(c.Id, out e)) continue;
                var pr = e.GetAttributeValue<EntityReference>(InvProduct);
                if (pr == null) continue;
                if (!byProduct.ContainsKey(pr.Id)) byProduct[pr.Id] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byProduct[pr.Id].Add(c.New);
            }
            foreach (var kv in byProduct.Where(k => k.Value.Count > 1))
            {
                foreach (Change c in changes.Where(x => x.Field == "Name" && inv.ContainsKey(x.Id)
                         && inv[x.Id].GetAttributeValue<EntityReference>(InvProduct) != null
                         && inv[x.Id].GetAttributeValue<EntityReference>(InvProduct).Id == kv.Key).ToList())
                {
                    open.Add(new OpenItem
                    {
                        ExcelRow = c.ExcelRow.ToString(), Item = c.Item, Topic = "Two items share one product",
                        WhatTheFileSays = "The same product is renamed two different ways: " + string.Join(" / ", kv.Value),
                        WhatWeNeed = "Confirm the single name to use, or confirm these should become two separate products."
                    });
                    changes.Remove(c);
                }
            }
        }

        // ---------------------------------------------------------------- delta
        private static int DropNoOps(List<Change> changes, Dictionary<Guid, Entity> inv,
                                     Dictionary<Guid, Entity> prod,
                                     Dictionary<string, Guid> colls, Dictionary<string, Guid> divs)
        {
            int dropped = 0;
            foreach (Change c in changes.ToList())
            {
                Entity e = inv[c.Id];
                Entity p = ProductOf(e, prod);
                bool same = false;
                switch (c.Field)
                {
                    case "Name": same = string.Equals(e.GetAttributeValue<string>(NameField), c.New, StringComparison.Ordinal); break;
                    case "Collection":
                        {
                            var r = e.GetAttributeValue<EntityReference>(InvCollection);
                            Guid? t = Resolve(colls, c.New);
                            same = r != null && t.HasValue && r.Id == t.Value; break;
                        }
                    case "Division":
                        {
                            var r = e.GetAttributeValue<EntityReference>(InvDivision);
                            Guid? t = Resolve(divs, c.New);
                            same = r != null && t.HasValue && r.Id == t.Value; break;
                        }
                    case "Rate":
                        {
                            var m = e.GetAttributeValue<Money>(InvRate);
                            decimal? n = Num(c.New);
                            same = m != null && n.HasValue && Math.Abs(m.Value - n.Value) < 0.005m; break;
                        }
                    case "Quantity":
                        {
                            decimal cur = e.GetAttributeValue<decimal>(InvQuantity);
                            decimal? n = Num(c.New);
                            same = n.HasValue && cur == n.Value; break;
                        }
                    // The product is the source of truth for both descriptions; the inventory copy is
                    // only a mirror the plugin refreshes. A change is done when BOTH sides agree.
                    case "External Description":
                        same = string.Equals(e.GetAttributeValue<string>(InvExtDesc) ?? "", c.New, StringComparison.Ordinal)
                            && (p == null || string.Equals(p.GetAttributeValue<string>(ProdExtDesc) ?? "", c.New, StringComparison.Ordinal));
                        break;
                    case "Internal Description":
                        same = string.Equals(e.GetAttributeValue<string>(InvIntDesc) ?? "", c.New, StringComparison.Ordinal)
                            && (p == null || string.Equals(p.GetAttributeValue<string>(ProdIntDesc) ?? "", c.New, StringComparison.Ordinal));
                        break;
                }
                if (same) { changes.Remove(c); dropped++; }
            }
            return dropped;
        }

        // ---------------------------------------------------------------- removals
        /// <summary>For every row the Storm team marked for removal, find out whether the item is
        /// actually used: deal lines pointing at the inventory record, and deal lines pointing at
        /// its product. Read-only - this only gathers the facts, so the question we ask back is a
        /// specific one ("these three are on live deals") instead of "please confirm".</summary>
        private static void CheckRemovals(IOrganizationService svc, List<Removal> removals)
        {
            if (removals.Count == 0) return;
            var ids = removals.Where(r => r.Id != Guid.Empty).Select(r => r.Id).Distinct().ToList();
            if (ids.Count == 0) return;

            // do the inventory records still exist, and what product do they hang off?
            Dictionary<Guid, Entity> inv = LoadInventory(svc, ids);
            var productToRemoval = new Dictionary<Guid, Removal>();
            foreach (Removal rm in removals)
            {
                Entity e;
                if (!inv.TryGetValue(rm.Id, out e)) continue;
                rm.FoundInCrm = true;
                var pr = e.GetAttributeValue<EntityReference>(InvProduct);
                if (pr != null && !productToRemoval.ContainsKey(pr.Id)) productToRemoval[pr.Id] = rm;
            }

            // deal lines pointing straight at the inventory record
            foreach (Entity dl in QueryDealLines(svc, "new_inventory", ids))
            {
                var invRef = dl.GetAttributeValue<EntityReference>("new_inventory");
                if (invRef == null) continue;
                Removal rm = removals.FirstOrDefault(x => x.Id == invRef.Id);
                if (rm == null) continue;
                rm.DealLines++;
                var deal = dl.GetAttributeValue<EntityReference>("new_dealid");
                string dn = deal == null ? "(deal not set)" : (string.IsNullOrEmpty(deal.Name) ? deal.Id.ToString() : deal.Name);
                if (!rm.Deals.Contains(dn)) rm.Deals.Add(dn);
            }

            // deal lines that reference the product instead - a weaker signal, still worth knowing
            if (productToRemoval.Count > 0)
                foreach (Entity dl in QueryDealLines(svc, "new_productid", productToRemoval.Keys.ToList()))
                {
                    var pRef = dl.GetAttributeValue<EntityReference>("new_productid");
                    Removal rm;
                    if (pRef != null && productToRemoval.TryGetValue(pRef.Id, out rm)) rm.DealLinesViaProduct++;
                }
        }

        private static List<Entity> QueryDealLines(IOrganizationService svc, string lookupField, List<Guid> ids)
        {
            var all = new List<Entity>();
            for (int i = 0; i < ids.Count; i += 400)
            {
                object[] chunk = ids.Skip(i).Take(400).Cast<object>().ToArray();
                var q = new QueryExpression("new_deallines")
                {
                    ColumnSet = new ColumnSet("new_inventory", "new_productid", "new_dealid", "new_name"),
                    Criteria = { Conditions = { new ConditionExpression(lookupField, ConditionOperator.In, chunk) } }
                };
                all.AddRange(RetrieveAll(svc, q));
            }
            return all;
        }

        /// <summary>Turns a checked removal into a question that carries the answer where we have one.</summary>
        private static OpenItem RemovalQuestion(Removal rm)
        {
            string says = rm.SaysWhat + (string.IsNullOrEmpty(rm.Conflict) ? "" : " " + rm.Conflict);
            if (!rm.FoundInCrm)
                return new OpenItem
                {
                    ExcelRow = rm.ExcelRow.ToString(), Item = rm.Item, Topic = "Marked for removal - already gone",
                    WhatTheFileSays = says,
                    WhatWeNeed = "This item no longer exists in the system, so there is nothing to remove. No action needed."
                };

            if (rm.DealLines > 0)
                return new OpenItem
                {
                    ExcelRow = rm.ExcelRow.ToString(), Item = rm.Item, Topic = "Marked for removal - IN USE on deals",
                    WhatTheFileSays = says + $" It is currently on {rm.DealLines} deal line(s), across: {string.Join("; ", rm.Deals)}.",
                    WhatWeNeed = "Deleting it would break those deal lines. We recommend making it inactive instead: the deals stay intact and the item stops showing up for new ones. Confirm, or tell us those deals should be adjusted first."
                };

            if (rm.DealLinesViaProduct > 0)
                return new OpenItem
                {
                    ExcelRow = rm.ExcelRow.ToString(), Item = rm.Item, Topic = "Marked for removal - product in use",
                    WhatTheFileSays = says + $" No deal line points at this item, but {rm.DealLinesViaProduct} deal line(s) reference the same underlying product.",
                    WhatWeNeed = "We will make the item inactive rather than delete it, so nothing on those deals is affected. Tell us if you want it deleted outright."
                };

            string done = RemovalMode.Equals("Delete", StringComparison.OrdinalIgnoreCase) ? "deleted"
                        : RemovalMode.Equals("Deactivate", StringComparison.OrdinalIgnoreCase) ? "made inactive"
                        : "reported only, not yet removed";
            return new OpenItem
            {
                ExcelRow = rm.ExcelRow.ToString(), Item = rm.Item, Topic = "Marked for removal - not in use",
                WhatTheFileSays = says + " It is not used on any deal line.",
                WhatWeNeed = $"Nothing depends on it, so removing it is safe. Status: {done}."
            };
        }

        /// <summary>Where a field physically lives. Collection, Division and both descriptions are
        /// owned by the product; the inventory copy is written too so the value is correct
        /// immediately, and it matches what the mirroring plugin would write anyway.</summary>
        private static string WrittenOn(string field)
        {
            switch (field)
            {
                case "Collection":
                case "Division": return "Product";
                case "External Description":
                case "Internal Description": return "Product + Inventory";
                default: return "Inventory";
            }
        }

        /// <summary>The product an inventory row points at, or null when it has none (row 169 case).</summary>
        private static Entity ProductOf(Entity inventory, Dictionary<Guid, Entity> prod)
        {
            var r = inventory.GetAttributeValue<EntityReference>(InvProduct);
            Entity p;
            return r != null && prod.TryGetValue(r.Id, out p) ? p : null;
        }

        // ---------------------------------------------------------------- apply
        private static void Apply(IOrganizationService svc, List<Change> changes, Dictionary<Guid, Entity> inv,
                                  Dictionary<string, Guid> colls, Dictionary<string, Guid> divs, List<string> missingColls,
                                  List<Removal> removals)
        {
            // 1) create the collections that do not exist yet
            foreach (string name in missingColls)
            {
                var c = new Entity(CollEntity); c[NameField] = name;
                Guid id = svc.Create(c);
                colls[Key(name)] = id;
                Say($"CREATED collection \"{name}\" [{id}]");
            }

            // 2) collection / division / descriptions go on the PRODUCT.
            //    Collection+Division: the real-time workflow "Set Collection-Division on Inventory"
            //    copies them from the product onto the inventory, so writing them on the inventory
            //    is reverted; the new_updondemandcollectiondivision stamp below re-triggers it.
            //    Descriptions: the Pl.Inventory.TotalCalculatedField plugin mirrors
            //    product.new_descriptionorspecifications -> inventory.new_description and
            //    product.new_internaldescription        -> inventory.new_internaldescription
            //    on every rate/quantity change. Writing them only on the inventory looks fine
            //    until the next rate edit silently reverts it. They are written on BOTH sides so
            //    the value is right now and stays right when the plugin next fires.
            var prodUpdates = new Dictionary<Guid, Entity>();
            var stampInventory = new HashSet<Guid>();
            var invToProduct = new Dictionary<Guid, Guid>();
            foreach (Change c in changes.Where(x => WrittenOn(x.Field) != "Inventory"))
            {
                Entity e = inv[c.Id];
                var pr = e.GetAttributeValue<EntityReference>(InvProduct);
                if (pr == null)
                {
                    // No product to own the value. Collection/Division cannot be set at all (the
                    // workflow needs a product); descriptions still land on the inventory below,
                    // and nothing will overwrite them because the mirror block needs a product too.
                    if (c.Field == "Collection" || c.Field == "Division")
                        Say($"SKIPPED row {c.ExcelRow} \"{c.Item}\" - inventory has no product, cannot set {c.Field}.");
                    continue;
                }

                Entity upd;
                if (!prodUpdates.TryGetValue(pr.Id, out upd)) { upd = new Entity(ProdEntity, pr.Id); prodUpdates[pr.Id] = upd; }

                switch (c.Field)
                {
                    case "Collection":
                        upd[ProdCollection] = new EntityReference(CollEntity, Resolve(colls, c.New).Value);
                        stampInventory.Add(c.Id); invToProduct[c.Id] = pr.Id; break;
                    case "Division":
                        upd[ProdDivision] = new EntityReference(DivEntity, Resolve(divs, c.New).Value);
                        stampInventory.Add(c.Id); invToProduct[c.Id] = pr.Id; break;
                    case "External Description": upd[ProdExtDesc] = Trunc(c.New, 1000); break;
                    case "Internal Description": upd[ProdIntDesc] = Trunc(c.New, 2000); break;
                }
            }
            int okP = 0, failP = 0;
            var failedProducts = new HashSet<Guid>();
            foreach (Entity p in prodUpdates.Values)
            {
                try { svc.Update(p); okP++; }
                catch (Exception ex) { failP++; failedProducts.Add(p.Id); Say($"ERROR product {p.Id}: {ex.Message}"); }
            }
            Console.WriteLine($"Products updated (coll/div/descriptions) : {okP} ok, {failP} failed");

            // If the product write failed, do NOT stamp its inventory: the workflow would copy the
            // OLD collection/division down and the run would look successful when it is not.
            if (failedProducts.Count > 0)
            {
                int skipped = stampInventory.RemoveWhere(id => invToProduct.ContainsKey(id) && failedProducts.Contains(invToProduct[id]));
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  {skipped} inventory rows NOT stamped because their product failed - see the log.");
                Console.ResetColor();
            }

            // 3) everything else on the inventory row, one update per record
            var invUpdates = new Dictionary<Guid, Entity>();
            foreach (Change c in changes.Where(x => x.Field != "Collection" && x.Field != "Division"))
            {
                Entity upd;
                if (!invUpdates.TryGetValue(c.Id, out upd)) { upd = new Entity(InvEntity, c.Id); invUpdates[c.Id] = upd; }
                switch (c.Field)
                {
                    case "Name": upd[NameField] = c.New; break;
                    case "Rate": upd[InvRate] = new Money(Num(c.New).Value); break;
                    case "Quantity": upd[InvQuantity] = Num(c.New).Value; break;
                    case "External Description": upd[InvExtDesc] = c.New; break;
                    case "Internal Description": upd[InvIntDesc] = c.New; break;
                }
            }
            // make the workflow re-copy collection/division onto the inventory rows we touched
            foreach (Guid id in stampInventory)
            {
                Entity upd;
                if (!invUpdates.TryGetValue(id, out upd)) { upd = new Entity(InvEntity, id); invUpdates[id] = upd; }
                upd[InvUpdOnDemand] = DateTime.UtcNow;
            }

            int okI = 0, failI = 0;
            foreach (Entity e in invUpdates.Values)
            {
                try { svc.Update(e); okI++; }
                catch (Exception ex) { failI++; Say($"ERROR inventory {e.Id}: {ex.Message}"); }
            }
            Console.WriteLine($"Inventory records updated             : {okI} ok, {failI} failed");

            // 0) removals the Storm team asked for - ONLY the ones no deal line uses.
            //    Anything in use is left alone and reported; deleting it would break the deal line.
            bool deleteMode = RemovalMode.Equals("Delete", StringComparison.OrdinalIgnoreCase);
            bool deactivateMode = RemovalMode.Equals("Deactivate", StringComparison.OrdinalIgnoreCase);
            if ((deleteMode || deactivateMode) && removals.Count > 0)
            {
                int okR = 0, failR = 0, held = 0;
                foreach (Removal rm in removals)
                {
                    if (!rm.Safe) { if (rm.FoundInCrm) held++; continue; }
                    try
                    {
                        if (deleteMode)
                        {
                            svc.Delete(InvEntity, rm.Id);
                            Say($"DELETED row {rm.ExcelRow} \"{rm.Item}\" [{rm.Id}] - no deal line used it.");
                        }
                        else
                        {
                            svc.Update(new Entity(InvEntity, rm.Id)
                            {
                                ["statecode"] = new OptionSetValue(1),   // Inactive
                                ["statuscode"] = new OptionSetValue(2)   // Inactive
                            });
                            Say($"DEACTIVATED row {rm.ExcelRow} \"{rm.Item}\" [{rm.Id}] - no deal line used it.");
                        }
                        okR++;
                    }
                    catch (Exception ex) { failR++; Say($"ERROR removing {rm.Id}: {ex.Message}"); }
                }
                Console.WriteLine($"Items removed ({RemovalMode.ToLowerInvariant()})       : {okR} ok, {failR} failed, {held} left in place because deals use them");
            }

            // 4) keep the product name aligned with the renamed inventory item
            if (UpdateProductName)
            {
                int okN = 0, failN = 0;
                foreach (Change c in changes.Where(x => x.Field == "Name"))
                {
                    Entity e = inv[c.Id];
                    var pr = e.GetAttributeValue<EntityReference>(InvProduct);
                    if (pr == null) continue;
                    try { svc.Update(new Entity(ProdEntity, pr.Id) { [NameField] = Trunc(c.New, 400) }); okN++; }
                    catch (Exception ex) { failN++; Say($"ERROR product name {pr.Id}: {ex.Message}"); }
                }
                Console.WriteLine($"Product names aligned                 : {okN} ok, {failN} failed");
            }
        }

        private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n);

        // ---------------------------------------------------------------- output
        private static void WriteReport(string path, List<Change> changes, List<OpenItem> open,
                                        Dictionary<Guid, Entity> inv, Dictionary<string, Guid> colls)
        {
            using (var wb = new XLWorkbook())
            {
                IXLWorksheet s = wb.Worksheets.Add("Summary");
                s.Cell(1, 1).Value = "2026 Inventory Update - " + (DryRun ? "preview" : "applied");
                s.Cell(1, 1).Style.Font.Bold = true;
                s.Cell(1, 1).Style.Font.FontSize = 14;
                s.Cell(2, 1).Value = "Environment: " + EnvTag() + "   |   Source: " + Path.GetFileName(SourceWorkbook);
                s.Cell(3, 1).Value = "Run: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                int r = 5;
                s.Cell(r, 1).Value = "Changes by field"; s.Cell(r, 1).Style.Font.Bold = true; r++;
                foreach (var g in changes.GroupBy(c => c.Field).OrderByDescending(g => g.Count()))
                { s.Cell(r, 1).Value = g.Key; s.Cell(r, 2).Value = g.Count(); r++; }
                s.Cell(r, 1).Value = "TOTAL"; s.Cell(r, 2).Value = changes.Count; s.Row(r).Style.Font.Bold = true; r += 2;
                s.Cell(r, 1).Value = "Open questions (not applied)"; s.Cell(r, 1).Style.Font.Bold = true; r++;
                foreach (var g in open.GroupBy(o => o.Topic).OrderByDescending(g => g.Count()))
                { s.Cell(r, 1).Value = g.Key; s.Cell(r, 2).Value = g.Count(); r++; }
                s.Cell(r, 1).Value = "TOTAL"; s.Cell(r, 2).Value = open.Count; s.Row(r).Style.Font.Bold = true;
                s.Column(1).Width = 55; s.Column(2).Width = 10;

                IXLWorksheet a = wb.Worksheets.Add(DryRun ? "Planned" : "Applied");
                string[] ah = { "Row", "Item", "Field", "Current value", "New value", "Written on", "Matched by", "Note" };
                for (int i = 0; i < ah.Length; i++) a.Cell(1, i + 1).Value = ah[i];
                a.Row(1).Style.Font.Bold = true;
                int ar = 2;
                foreach (Change c in changes.OrderBy(x => x.ExcelRow).ThenBy(x => x.Field))
                {
                    a.Cell(ar, 1).Value = c.ExcelRow;
                    a.Cell(ar, 2).Value = c.Item;
                    a.Cell(ar, 3).Value = c.Field;
                    a.Cell(ar, 4).Value = c.Current;
                    a.Cell(ar, 5).Value = c.New;
                    a.Cell(ar, 6).Value = WrittenOn(c.Field);
                    a.Cell(ar, 7).Value = c.MatchedByName ? "Name (rehearsal)" : "Record ID";
                    a.Cell(ar, 8).Value = c.Note;
                    ar++;
                }
                a.SheetView.FreezeRows(1);
                a.Column(1).Width = 8;  a.Column(2).Width = 38; a.Column(3).Width = 20;
                a.Column(4).Width = 34; a.Column(5).Width = 46; a.Column(6).Width = 12;
                a.Column(7).Width = 18; a.Column(8).Width = 34;
                a.Columns(4, 5).Style.Alignment.SetWrapText();

                IXLWorksheet q = wb.Worksheets.Add("Open Questions");
                string[] qh = { "Row", "Item", "Topic", "What the workbook says", "What we need" };
                for (int i = 0; i < qh.Length; i++) q.Cell(1, i + 1).Value = qh[i];
                q.Row(1).Style.Font.Bold = true;
                int qr = 2;
                foreach (OpenItem o in open)
                {
                    q.Cell(qr, 1).Value = o.ExcelRow; q.Cell(qr, 2).Value = o.Item; q.Cell(qr, 3).Value = o.Topic;
                    q.Cell(qr, 4).Value = o.WhatTheFileSays; q.Cell(qr, 5).Value = o.WhatWeNeed; qr++;
                }
                q.SheetView.FreezeRows(1);
                q.Column(1).Width = 12; q.Column(2).Width = 38; q.Column(3).Width = 26;
                q.Column(4).Width = 60; q.Column(5).Width = 60;
                q.Columns(4, 5).Style.Alignment.SetWrapText();

                wb.SaveAs(path);
            }
        }

        /// <summary>Snapshot of every product this run will touch (collection / division / name).</summary>
        private static Dictionary<Guid, Entity> LoadProducts(IOrganizationService svc, List<Guid> ids)
        {
            var d = new Dictionary<Guid, Entity>();
            var cols = new ColumnSet(NameField, ProdCollection, ProdDivision, ProdExtDesc, ProdIntDesc);
            for (int i = 0; i < ids.Count; i += 400)
            {
                object[] chunk = ids.Skip(i).Take(400).Cast<object>().ToArray();
                var q = new QueryExpression(ProdEntity)
                {
                    ColumnSet = cols,
                    Criteria = { Conditions = { new ConditionExpression("new_productid", ConditionOperator.In, chunk) } }
                };
                foreach (Entity e in RetrieveAll(svc, q)) d[e.Id] = e;
            }
            return d;
        }

        private static void WriteProductBackup(string path, Dictionary<Guid, Entity> prod)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ProductId,Name,CollectionId,DivisionId,ExternalDescription,InternalDescription");
            foreach (Entity e in prod.Values)
            {
                var cr = e.GetAttributeValue<EntityReference>(ProdCollection);
                var dr = e.GetAttributeValue<EntityReference>(ProdDivision);
                sb.AppendLine(string.Join(",", new[]
                {
                    e.Id.ToString(),
                    Csv(e.GetAttributeValue<string>(NameField)),
                    cr == null ? "" : cr.Id.ToString(),
                    dr == null ? "" : dr.Id.ToString(),
                    Csv(e.GetAttributeValue<string>(ProdExtDesc)),
                    Csv(e.GetAttributeValue<string>(ProdIntDesc))
                }));
            }
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        }

        // ---------------------------------------------------------------- restore
        /// <summary>Writes a previous run's backup CSVs back, field by field, exactly as they were.
        /// Blank values are restored as blank (the field is cleared), which is what the snapshot said.</summary>
        private static void RunRestore(string outDir, string stamp)
        {
            string bkInv = Path.Combine(outDir, $"InventoryUpdate_BACKUP_INVENTORY_{EnvTag()}_{RestoreFromBackup}.csv");
            string bkProd = Path.Combine(outDir, $"InventoryUpdate_BACKUP_PRODUCT_{EnvTag()}_{RestoreFromBackup}.csv");
            if (!File.Exists(bkInv)) { Bye("Backup not found: " + bkInv); return; }
            if (!File.Exists(bkProd)) { Bye("Backup not found: " + bkProd); return; }

            List<string[]> invRows = ReadCsv(bkInv), prodRows = ReadCsv(bkProd);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\nRESTORE MODE - backup {RestoreFromBackup}");
            Console.WriteLine($"  {invRows.Count} inventory records and {prodRows.Count} products will be written back to {EnvTag()},");
            Console.WriteLine("  overwriting whatever they hold now. Type RESTORE to continue:");
            Console.ResetColor();
            if ((Console.ReadLine() ?? "").Trim() != "RESTORE") { Bye("Cancelled - nothing was written."); return; }

            string cs = $"AuthType=OAuth;Url={EnvUrl};Username={UserName};Password={Password};" +
                        $"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
            Console.WriteLine("\nConnecting to Dynamics 365...");
            var service = new CrmServiceClient(cs);
            if (!service.IsReady) { Bye("Connection error: " + service.LastCrmError); return; }
            Console.WriteLine("Connection successful.\n");

            using (service)
            {
                // products first, so the workflow copies the restored collection/division down
                int okP = 0, failP = 0;
                foreach (string[] r in prodRows)                       // ProductId,Name,CollectionId,DivisionId
                {
                    try
                    {
                        var e = new Entity(ProdEntity, Guid.Parse(r[0]));
                        e[NameField] = NullIfEmpty(r[1]);
                        e[ProdCollection] = Ref(CollEntity, r[2]);
                        e[ProdDivision] = Ref(DivEntity, r[3]);
                        if (r.Length >= 6)   // backups written before descriptions were tracked have 4 columns
                        {
                            e[ProdExtDesc] = NullIfEmpty(r[4]);
                            e[ProdIntDesc] = NullIfEmpty(r[5]);
                        }
                        service.Update(e); okP++;
                    }
                    catch (Exception ex) { failP++; Say($"ERROR product {r[0]}: {ex.Message}"); }
                }
                Console.WriteLine($"Products restored  : {okP} ok, {failP} failed");

                int okI = 0, failI = 0;
                foreach (string[] r in invRows)                        // InventoryId,Name,Collection,Division,Rate,Quantity,Ext,Int
                {
                    try
                    {
                        var e = new Entity(InvEntity, Guid.Parse(r[0]));
                        e[NameField] = NullIfEmpty(r[1]);
                        e[InvRate] = string.IsNullOrWhiteSpace(r[4]) ? null : new Money(decimal.Parse(r[4], CultureInfo.InvariantCulture));
                        e[InvQuantity] = string.IsNullOrWhiteSpace(r[5]) ? (object)null : (object)decimal.Parse(r[5], CultureInfo.InvariantCulture);
                        e[InvExtDesc] = NullIfEmpty(r[6]);
                        e[InvIntDesc] = NullIfEmpty(r[7]);
                        e[InvUpdOnDemand] = DateTime.UtcNow;   // make the workflow re-copy collection/division
                        service.Update(e); okI++;
                    }
                    catch (Exception ex) { failI++; Say($"ERROR inventory {r[0]}: {ex.Message}"); }
                }
                Console.WriteLine($"Inventory restored : {okI} ok, {failI} failed");

                string logPath = Path.Combine(outDir, $"InventoryUpdate_RESTORE_LOG_{EnvTag()}_{stamp}.txt");
                if (Log.Count > 0) File.WriteAllLines(logPath, Log, Utf8NoBom);
                Console.WriteLine("\nNote: collections created by the original run are NOT deleted (harmless if empty).");
                Bye("Restore finished.");
            }
        }

        private static object NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
        private static EntityReference Ref(string entity, string id) =>
            string.IsNullOrWhiteSpace(id) ? null : new EntityReference(entity, Guid.Parse(id));

        /// <summary>Minimal RFC-4180 reader for the backup files this tool writes. Skips the header.</summary>
        private static List<string[]> ReadCsv(string path)
        {
            var rows = new List<string[]>();
            foreach (string line in File.ReadAllLines(path).Skip(1))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var fields = new List<string>();
                var sb = new StringBuilder();
                bool q = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char ch = line[i];
                    if (q)
                    {
                        if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else if (ch == '"') q = false;
                        else sb.Append(ch);
                    }
                    else if (ch == '"') q = true;
                    else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                    else sb.Append(ch);
                }
                fields.Add(sb.ToString());
                rows.Add(fields.ToArray());
            }
            return rows;
        }

        private static void WriteBackup(string path, List<Change> changes, Dictionary<Guid, Entity> inv)
        {
            var sb = new StringBuilder();
            sb.AppendLine("InventoryId,Name,Collection,Division,Rate,Quantity,ExternalDescription,InternalDescription");
            foreach (Guid id in changes.Select(c => c.Id).Distinct())
            {
                Entity e = inv[id];
                var cr = e.GetAttributeValue<EntityReference>(InvCollection);
                var dr = e.GetAttributeValue<EntityReference>(InvDivision);
                var m = e.GetAttributeValue<Money>(InvRate);
                sb.AppendLine(string.Join(",", new[]
                {
                    id.ToString(),
                    Csv(e.GetAttributeValue<string>(NameField)),
                    Csv(cr == null ? "" : cr.Id.ToString()),
                    Csv(dr == null ? "" : dr.Id.ToString()),
                    m == null ? "" : m.Value.ToString(CultureInfo.InvariantCulture),
                    e.GetAttributeValue<decimal>(InvQuantity).ToString(CultureInfo.InvariantCulture),
                    Csv(e.GetAttributeValue<string>(InvExtDesc)),
                    Csv(e.GetAttributeValue<string>(InvIntDesc))
                }));
            }
            File.WriteAllText(path, sb.ToString(), Utf8NoBom);
        }

        // ---------------------------------------------------------------- helpers
        private static List<Entity> RetrieveAll(IOrganizationService svc, QueryExpression q)
        {
            var all = new List<Entity>();
            q.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, PagingCookie = null };
            while (true)
            {
                EntityCollection page = svc.RetrieveMultiple(q);
                all.AddRange(page.Entities);
                if (!page.MoreRecords) break;
                q.PageInfo.PageNumber++; q.PageInfo.PagingCookie = page.PagingCookie;
            }
            return all;
        }

        private static string Csv(string s)
        {
            s = s ?? "";
            if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0) s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        private static string Cfg(string key, string fallback)
        {
            string v = ConfigurationManager.AppSettings[key];
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }

        private static bool CfgBool(string key, bool fallback)
        {
            bool b;
            return bool.TryParse(ConfigurationManager.AppSettings[key] ?? "", out b) ? b : fallback;
        }

        private static void Say(string m) { Console.WriteLine("  " + m); Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + m); }

        private static void Bye(string m) { Console.WriteLine("\n" + m); Console.WriteLine("Press Enter to exit..."); Console.ReadLine(); }
    }
}
