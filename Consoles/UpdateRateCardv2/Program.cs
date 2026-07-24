// ============================================================
//  Storm Basketball - Dynamics 365  Rate Card v2 LOADER
//  ------------------------------------------------------------
//  PURPOSE
//    Load Zach's real 2026 Rate Card ("...FINAL V2.xlsx") into Dynamics for
//    the single season "2026 - Storm", and produce an Excel report of what
//    was loaded and what was omitted (and why) to send to Zach.
//
//    TWO PHASES in one run:
//      A) INVENTORY  - every rate-card row (regular items AND packages) becomes
//         a new_inventory row for the season, with collection + rate + quantity.
//         Package rows also flag their product/inventory as new_ispackage = true.
//      B) PACKAGE DEFINITIONS - for each package row, ensure the product is a
//         package and build its recipe in new_packagecomponent (one row per
//         component product, with quantity; Is Anchor = No for now).
//
//    RE-RUNNABLE / DELTA: every run snapshots current CRM data and only writes
//    what changed (fields, and missing component links). A new file version just
//    updates the delta - no duplicates, no rewrite of unchanged records.
//
//  ACTUAL PACKAGE MODEL (confirmed in code, NOT the INCREMENT proposal):
//    - new_product.new_ispackage (bool) marks a product as a package.
//    - new_packagecomponent holds the recipe: two lookups to new_product -
//      new_packageproduct (the package) + new_componentproduct (the component,
//      restricted to non-package products) - plus new_quantity + new_isanchor.
//    - new_ispackage is mirrored onto new_inventory (plugin on create).
//    - Packages ARE inventory rows (one per division/collection/season); the
//      Deal Line Builder finds them by season + product.new_ispackage=true and
//      uses the inventory row's new_rate as the package price.
//
//  DECISIONS BAKED IN (Zach + Gustavo, 2026-07-22):
//    - Season   = "2026 - Storm" only.
//    - Price     = "2026 Unit Rate" (col L) -> new_rate.
//    - Non-numeric rate/qty cells are SKIPPED, never guessed.
//    - Hard cost = not in file -> new_expense NOT touched.
//    - Division  = "Storm" (set by the Set-Collection-Division workflow).
//    - Collection= loaded per row (incl. packages) so it can be filtered later.
//    - Playoffs  = DEFERRED to early 2027 (flag + "Presenting Partner - Playoffs" ignored).
//    - Match key = Name + Collection GUID.
//    - Is Anchor = No for all components (Zach has not specified anchors).
//    - Components resolved by Package Detail "Closest Match in Inventory" name;
//      unmatched components go to the report as exceptions.
//    - Entitlement Game variants share the two generic Package Detail recipes
//      ("Entitlement Game - Presenting Partner" / "... Supporting Partner").
//
//  SAFETY: DryRun = true previews + reports WITHOUT touching CRM. Flip to false
//    to load (still asks YES). A CSV backup of the season snapshot is taken first.
//
//  SDK: Microsoft.Xrm.Tooling.Connector | Excel: ClosedXML | .NET: 4.6.2
//  NOTE: do not commit real credentials; prefer env vars / args.
// ============================================================

using ClosedXML.Excel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace UpdateRateCardv2
{
    internal class Program
    {
        // ==============================================================
        //  CONFIGURATION - VERIFY BEFORE EVERY RUN
        // ==============================================================
        //private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/"; // PRODUCTION
        private const string EnvUrl = "https://org00bff505.crm.dynamics.com/";        // <-- SANDBOX first!
        private const string CrmUsername = "FanInteractive@stormbasketball.com";
        private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
        private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
        private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

        // true = preview + report only, NO CRM writes. false = actually load (still asks YES).
        private const bool DryRun = false;

        private const string ExcelPath =
            @"C:\Code\Storm\Deal Options And PlayOff Automation\UpdateRateCard\2026 Rate Card with Packages FINAL V2.xlsx";
        private const string InventorySheet = "Inventory Rate Card"; // trailing space tolerated (matched trimmed)
        private const string PackageSheet = "Package Detail";

        private const string TargetSeason = "2026 - Storm";
        private const decimal UNLIMITED_QUANTITY = 2147483647m;

        // Entities / fields
        private const string CollectionEntity = "new_collection";
        private const string CollectionNameField = "new_name";
        private const string ProductEntity = "new_product";
        private const string ProductNameField = "new_name";
        private const string IsPackageField = "new_ispackage";              // bool on product AND inventory (mirror)
        private const string DivisionEntity = "new_division";
        private const string DivisionNameField = "new_name";
        private const string DivisionIdField = "new_divisionid";
        private const string DivisionName = "Storm";                        // all inventory belongs to this division (Zach)
        private const string PkgComponentEntity = "new_packagecomponent";
        private const string PkgComponentPackage = "new_packageproduct";    // lookup -> new_product (the package)
        private const string PkgComponentComponent = "new_componentproduct";// lookup -> new_product (the component)
        private const string PkgComponentQty = "new_quantity";
        private const string PkgComponentAnchor = "new_isanchor";

        // Inventory Rate Card columns (1-based)
        private const int C_NAME = 1;
        private const int C_PACKAGE = 2;   // "1" => the item IS a package
        private const int C_TOTALUNITS = 3;
        private const int C_COLLECTION = 4;
        private const int C_UNITRATE = 12; // 2026 Unit Rate (the price)
        private const int C_PLAYOFFS = 16; // Possible in Playoffs (ignored, deferred)

        // Package Detail columns (1-based)
        private const int P_PARENT = 1;
        private const int P_SUB = 2;
        private const int P_MATCH = 3;     // Closest Match in Inventory -> resolves the component product
        private const int P_QTY = 4;

        private static readonly Dictionary<string, string> CollectionAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "sinage", "Signage" },
                { "digital -website", "Digital - Website" },
            };

        // Package-name typo -> Package Detail parent (tentative, confirmed by Zach).
        private static readonly Dictionary<string, string> PackageAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "present the dj", "Presenting the DJ" },
                { "celebrity cam - in-arena/social/broadcast", "Celebrity Cam" },
                { "jr. storm hoops academy presenting", "Storm Academy Presenting" },
                { "jr. storm hoops academy - supporting partner", "Storm Academy - Supporting" },
            };

        private static readonly HashSet<string> DeferredPackages =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "presenting partner - playoffs" };

        // ==============================================================
        internal class InvRow
        {
            public int ExcelRow;
            public string Name;
            public bool IsPackage;
            public string RawCollection;
            public string Collection;      // canonical (null if blank)
            public decimal? Rate;
            public decimal? Quantity;
            public bool Unlimited;
        }
        internal class Comp { public string Sub, Match; public decimal Qty; }
        internal class Report { public string Name, Collection, Action, Detail; public decimal? Rate, Quantity; }

        // Package products created/known this run: package name (norm) -> product ref
        private static readonly Dictionary<string, EntityReference> ProductCache =
            new Dictionary<string, EntityReference>(StringComparer.OrdinalIgnoreCase);

        // The "Storm" division (lookup target), resolved once after connect. Set directly on each
        // inventory row: products are created with no division, so the Set-Collection-Division
        // workflow has nothing to inherit and will not overwrite what we set here.
        private static EntityReference _division;

        static void Main(string[] args)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outDir = AppDomain.CurrentDomain.BaseDirectory;
            var log = new StringBuilder();
            void Log(string m) { Console.WriteLine(m); log.AppendLine(m); }

            var report = new List<Report>();
            void Rep(string name, string coll, string action, string detail, decimal? rate = null, decimal? qty = null)
                => report.Add(new Report { Name = name, Collection = coll, Action = action, Detail = detail, Rate = rate, Quantity = qty });

            Log("============================================================");
            Log(" STORM RATE CARD v2 - LOADER (inventory + package definitions)");
            Log($" Environment : {EnvUrl}   {(DryRun ? "[DRY RUN - no writes]" : "[LIVE WRITES]")}");
            Log($" Season      : {TargetSeason}");
            Log($" Source      : {Path.GetFileName(ExcelPath)}");
            Log($" Time        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Log("============================================================");

            if (!File.Exists(ExcelPath)) { Log($"[ERROR] Excel not found: {ExcelPath}"); Pause(); return; }

            // ── 1. Read inventory + package detail (offline) ──────────
            var inv = new List<InvRow>();
            var comps = new Dictionary<string, List<Comp>>(StringComparer.OrdinalIgnoreCase); // norm(parent) -> components
            var parentDisplay = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int playoffsFlagCount = 0;

            using (var wb = new XLWorkbook(ExcelPath))
            {
                var wsInv = FindSheet(wb, InventorySheet);
                var wsPkg = FindSheet(wb, PackageSheet);
                if (wsInv == null) { Log($"[ERROR] Sheet '{InventorySheet}' not found."); Pause(); return; }
                if (wsPkg == null) { Log($"[ERROR] Sheet '{PackageSheet}' not found."); Pause(); return; }

                foreach (var row in wsPkg.RowsUsed().Skip(1))
                {
                    string parent = Collapse(GetStr(row.Cell(P_PARENT)));
                    if (string.IsNullOrWhiteSpace(parent)) continue;
                    string key = Norm(parent);
                    if (!comps.ContainsKey(key)) { comps[key] = new List<Comp>(); parentDisplay[key] = parent; }
                    comps[key].Add(new Comp { Sub = Collapse(GetStr(row.Cell(P_SUB))), Match = Collapse(GetStr(row.Cell(P_MATCH))), Qty = GetDec(row.Cell(P_QTY)) ?? 1m });
                }

                foreach (var row in wsInv.RowsUsed().Skip(1))
                {
                    string name = Collapse(GetStr(row.Cell(C_NAME)));
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var r = new InvRow
                    {
                        ExcelRow = row.RowNumber(),
                        Name = name,
                        IsPackage = string.Equals(GetStr(row.Cell(C_PACKAGE)), "1", StringComparison.Ordinal),
                        RawCollection = Collapse(GetStr(row.Cell(C_COLLECTION)))
                    };
                    r.Collection = CanonCollection(r.RawCollection);
                    r.Rate = ParseRate(row.Cell(C_UNITRATE), out _);
                    r.Quantity = ParseQty(row.Cell(C_TOTALUNITS), out string qtyState);
                    r.Unlimited = qtyState == "unlimited";
                    if (!string.IsNullOrWhiteSpace(GetStr(row.Cell(C_PLAYOFFS)))) playoffsFlagCount++;
                    inv.Add(r);
                }
            }
            Log($"Parsed {inv.Count} rows | packages: {inv.Count(x => x.IsPackage)} | Package Detail parents: {comps.Count}");

            // Loadable inventory rows = anything with a collection (packages INCLUDED).
            var loadable = new List<InvRow>();
            foreach (var r in inv)
            {
                if (string.IsNullOrWhiteSpace(r.Collection))
                { Rep(r.Name, "", "Omitted", (r.IsPackage ? "Package - " : "") + "no collection, cannot place the item", r.Rate, r.Quantity); continue; }
                loadable.Add(r);
            }
            if (playoffsFlagCount > 0) Rep("(global)", "", "Note", $"{playoffsFlagCount} rows flagged 'Possible in Playoffs' - deferred to early 2027");

            var distinctColls = loadable.Select(r => r.Collection).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            try
            {
                // ── 2. Connect ────────────────────────────────────────
                CrmServiceClient svc = null;
                if (!DryRun)
                {
                    Log("Connecting to Dynamics 365...");
                    string conn = $"AuthType=OAuth;Url={EnvUrl};Username={CrmUsername};Password={CrmPassword};AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
                    svc = new CrmServiceClient(conn);
                    if (!svc.IsReady) { Log($"[ERROR] Connection failed: {svc.LastCrmError}"); Pause(); return; }
                    Log($"Connected. Org: {svc.ConnectedOrgUniqueName}");

                    // Resolve the Storm division once (set on every inventory row).
                    _division = ResolveByName(svc, DivisionEntity, DivisionNameField, DivisionIdField, DivisionName);
                    if (_division == null)
                    {
                        Log($"[WARNING] Division '{DivisionName}' not found - inventory will be created without a division.");
                        Rep("(division)", "", "Note", $"Division '{DivisionName}' not found; inventory left with no division");
                    }
                    else Log($"Division resolved: {DivisionName}");
                }
                else Log("DRY RUN: skipping connection. Report shows intended actions only.");

                EntityReference seasonRef = null;
                var collMap = new Dictionary<string, EntityReference>(StringComparer.OrdinalIgnoreCase);
                var byKey = new Dictionary<string, Entity>();

                if (!DryRun)
                {
                    // ── 3. Resolve season + collections ───────────────
                    seasonRef = ResolveByName(svc, "new_season", "new_name", "new_seasonid", TargetSeason);
                    if (seasonRef == null) { Log($"[ERROR] Season '{TargetSeason}' not found. Aborting."); Pause(); return; }

                    collMap = ResolveCollections(svc, distinctColls);
                    // Auto-create any collection the rate card needs that doesn't exist yet.
                    // new_collection only requires new_name (no division field). Full-replace makes this
                    // safe: all inventory was wiped, so there is nothing to duplicate/orphan.
                    foreach (var collName in distinctColls)
                    {
                        if (collMap.ContainsKey(Norm(collName))) continue;
                        var c = new Entity(CollectionEntity);
                        c[CollectionNameField] = collName;
                        Guid cid = svc.Create(c);
                        collMap[Norm(collName)] = new EntityReference(CollectionEntity, cid) { Name = collName };
                        Rep("(collection)", collName, "Note", "auto-created new collection");
                        Log($"   + Collection created '{collName}'");
                    }

                    // ── 4. Snapshot + backup ──────────────────────────
                    var snapshot = RetrieveSeasonInventory(svc, seasonRef.Id);
                    byKey = snapshot.Where(e => e.Contains("new_name"))
                        .GroupBy(e => InvKey(e.GetAttributeValue<string>("new_name"), e.GetAttributeValue<EntityReference>("new_collection")?.Id))
                        .ToDictionary(g => g.Key, g => g.First());
                    string backup = Path.Combine(outDir, $"backup_2026Storm_{stamp}.csv");
                    WriteBackupCsv(backup, snapshot);
                    Log($"Snapshot {snapshot.Count} inventory records | backup -> {Path.GetFileName(backup)}");

                    Console.Write($"Load {loadable.Count} inventory rows + package definitions into '{TargetSeason}' on '{svc.ConnectedOrgUniqueName}'? Type YES: ");
                    if (!(Console.ReadLine() ?? "").Trim().Equals("YES", StringComparison.OrdinalIgnoreCase))
                    { Log("Cancelled by user. No changes made."); Pause(); return; }
                }

                // ── 5. PHASE A: inventory ─────────────────────────────
                int created = 0, updated = 0, unchanged = 0, failed = 0;
                foreach (var r in loadable)
                {
                    try
                    {
                        if (DryRun)
                        {
                            Rep(r.Name, r.Collection, "WouldLoad",
                                $"{(r.IsPackage ? "[package] " : "")}rate={(r.Rate?.ToString(CultureInfo.InvariantCulture) ?? "-")}, qty={(r.Unlimited ? "Unlimited" : r.Quantity?.ToString(CultureInfo.InvariantCulture) ?? "-")}",
                                r.Rate, r.Quantity);
                            continue;
                        }

                        EntityReference coll = collMap.TryGetValue(Norm(r.Collection), out var c) ? c : null;
                        Guid? collId = coll?.Id;
                        Entity match = byKey.TryGetValue(InvKey(r.Name, collId), out var en) ? en : null;

                        if (match != null)
                        {
                            var changes = UpdateExisting(svc, match, r, coll);
                            if (changes.Count > 0) { updated++; Rep(r.Name, r.Collection, "Updated", (r.IsPackage ? "[package] " : "") + string.Join(", ", changes), r.Rate, r.Quantity); }
                            else { unchanged++; Rep(r.Name, r.Collection, "Unchanged", r.IsPackage ? "[package]" : "no field differed", r.Rate, r.Quantity); }
                        }
                        else
                        {
                            Guid id = CreateNetNew(svc, r, seasonRef, coll);
                            byKey[InvKey(r.Name, collId)] = new Entity("new_inventory", id) { ["new_name"] = r.Name };
                            created++; Rep(r.Name, r.Collection, "Created", (r.IsPackage ? "[package] " : "") + "net-new item + product", r.Rate, r.Quantity);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++; Rep(r.Name, r.Collection, "Failed", ex.Message, r.Rate, r.Quantity);
                        Log($"[ERROR] Row {r.ExcelRow} '{r.Name}': {ex.Message}");
                    }
                }
                Log(DryRun ? $"DRY RUN: would load {loadable.Count} inventory rows." : $"PHASE A done. created:{created} updated:{updated} unchanged:{unchanged} failed:{failed}");

                // ── 6. PHASE B: package definitions (product flag + recipe) ──
                int linkCreated = 0, linkSkipped = 0, linkExc = 0;
                foreach (var r in loadable.Where(x => x.IsPackage))
                {
                    string pkgKey = ResolveParentKey(r.Name, comps, out _);
                    if (DeferredPackages.Contains(Norm(r.Name)) || (pkgKey != null && DeferredPackages.Contains(pkgKey)))
                    { Rep(r.Name, r.Collection, "PkgDefExc", "package deferred (Playoffs)"); continue; }
                    if (pkgKey == null)
                    { Rep(r.Name, r.Collection, "PkgDefExc", "no recipe found in Package Detail - confirm with Zach"); linkExc++; continue; }

                    var recipe = comps[pkgKey];
                    if (DryRun)
                    {
                        foreach (var cp in recipe)
                            Rep(r.Name, r.Collection, "WouldLinkComponent", $"{cp.Match} (qty {cp.Qty})");
                        continue;
                    }

                    try
                    {
                        // Ensure the package product exists and is flagged new_ispackage = true.
                        EntityReference pkgProd = FindOrCreateProduct(svc, r.Name, isPackage: true);
                        var existing = GetExistingComponentProductIds(svc, pkgProd.Id);

                        foreach (var cp in recipe)
                        {
                            if (string.IsNullOrWhiteSpace(cp.Match))
                            { Rep(r.Name, r.Collection, "PkgDefExc", $"component '{cp.Sub}' has no Closest Match - cannot link"); linkExc++; continue; }

                            EntityReference compProd = ResolveByName(svc, ProductEntity, ProductNameField, "new_productid", cp.Match);
                            if (compProd == null)
                            { Rep(r.Name, r.Collection, "PkgDefExc", $"component product not found: '{cp.Match}'"); linkExc++; continue; }
                            if (existing.Contains(compProd.Id))
                            { linkSkipped++; continue; } // idempotent

                            var pc = new Entity(PkgComponentEntity);
                            pc[PkgComponentPackage] = pkgProd;
                            pc[PkgComponentComponent] = compProd;
                            pc[PkgComponentQty] = cp.Qty;
                            pc[PkgComponentAnchor] = false; // Is Anchor = No for all, per Zach
                            svc.Create(pc);
                            existing.Add(compProd.Id);
                            linkCreated++;
                            Rep(r.Name, r.Collection, "PkgComponent", $"{cp.Match} (qty {cp.Qty})");
                        }
                    }
                    catch (Exception ex)
                    {
                        Rep(r.Name, r.Collection, "PkgDefExc", $"definition failed: {ex.Message}");
                        Log($"[ERROR] Package '{r.Name}' definition: {ex.Message}");
                    }
                }
                Log(DryRun ? "DRY RUN: package component links previewed." : $"PHASE B done. links created:{linkCreated} skipped(existing):{linkSkipped} exceptions:{linkExc}");

                // ── 7. Excel report ───────────────────────────────────
                string reportPath = WriteReport(outDir, stamp, report, distinctColls);
                Log($"Report -> {Path.GetFileName(reportPath)}");
            }
            catch (Exception ex)
            {
                Log($"[FATAL] {ex.Message}");
                if (ex.InnerException != null) Log($"  Inner: {ex.InnerException.Message}");
            }

            File.WriteAllText(Path.Combine(outDir, $"log_load_{stamp}.txt"), log.ToString(), Encoding.UTF8);
            Pause();
        }

        // ==============================================================
        //  Excel report
        // ==============================================================
        private static string WriteReport(string outDir, string stamp, List<Report> report, List<string> collections)
        {
            string path = Path.Combine(outDir, $"RateCardV2_LoadReport_{stamp}.xlsx");
            using (var wb = new XLWorkbook())
            {
                var s = wb.AddWorksheet("Summary");
                int C(string a) => report.Count(r => r.Action == a);
                var lines = new (string, string)[]
                {
                    ("2026 Rate Card - Load Report", ""),
                    ("Season", TargetSeason),
                    ("Environment", EnvUrl + (DryRun ? "  (DRY RUN - preview only)" : "")),
                    ("Source file", Path.GetFileName(ExcelPath)),
                    ("Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm")),
                    ("", ""),
                    ("Inventory created", C("Created").ToString()),
                    ("Inventory updated", C("Updated").ToString()),
                    ("Inventory unchanged", C("Unchanged").ToString()),
                    ("Inventory would-load (dry run)", C("WouldLoad").ToString()),
                    ("Inventory omitted", C("Omitted").ToString()),
                    ("Inventory failed", C("Failed").ToString()),
                    ("Package component links created", C("PkgComponent").ToString()),
                    ("Package component links (dry run)", C("WouldLinkComponent").ToString()),
                    ("Package definition exceptions", C("PkgDefExc").ToString()),
                    ("", ""),
                    ("Rules applied", ""),
                    ("  Price", "2026 Unit Rate -> Rate"),
                    ("  Non-numeric rate/qty", "skipped, not guessed"),
                    ("  Hard cost", "not in file -> not updated"),
                    ("  Division", "Storm (set by workflow)"),
                    ("  Collection", "loaded per row, incl. packages"),
                    ("  Playoffs", "deferred to early 2027"),
                    ("  Packages", "loaded as inventory + defined in package components"),
                    ("  Is Anchor", "No for all (pending Zach)"),
                    ("  Match key", "Name + Collection"),
                };
                for (int i = 0; i < lines.Length; i++)
                {
                    s.Cell(i + 1, 1).Value = lines[i].Item1;
                    s.Cell(i + 1, 2).Value = lines[i].Item2;
                    if (i == 0) { s.Cell(1, 1).Style.Font.Bold = true; s.Cell(1, 1).Style.Font.FontSize = 14; }
                    else if (lines[i].Item2 == "" && lines[i].Item1 != "") s.Cell(i + 1, 1).Style.Font.Bold = true;
                }
                s.Column(1).Width = 42; s.Column(2).Width = 55;

                var invActions = new HashSet<string> { "Created", "Updated", "Unchanged", "WouldLoad", "Failed" };
                WriteGrid(wb, "Inventory", new[] { "Name", "Collection", "Action", "Detail", "Rate", "Quantity" },
                    report.Where(r => invActions.Contains(r.Action)).Select(r => new object[] { r.Name, r.Collection, r.Action, r.Detail,
                        r.Rate.HasValue ? (object)r.Rate.Value : "", r.Quantity.HasValue ? (object)r.Quantity.Value : "" }));

                var pkgActions = new HashSet<string> { "PkgComponent", "WouldLinkComponent", "PkgDefExc" };
                WriteGrid(wb, "PackageComponents", new[] { "Package", "Collection", "Action", "Component / detail" },
                    report.Where(r => pkgActions.Contains(r.Action)).Select(r => new object[] { r.Name, r.Collection, r.Action, r.Detail }));

                var omitActions = new HashSet<string> { "Omitted", "Note", "BLOCKER" };
                WriteGrid(wb, "Omitted", new[] { "Name", "Collection", "Reason" },
                    report.Where(r => omitActions.Contains(r.Action)).Select(r => new object[] { r.Name, r.Collection, (r.Action == "Omitted" ? "" : r.Action + ": ") + r.Detail }));

                WriteGrid(wb, "Collections", new[] { "Collection" }, collections.OrderBy(c => c).Select(c => new object[] { c }));

                wb.SaveAs(path);
            }
            return path;
        }

        private static void WriteGrid(XLWorkbook wb, string title, string[] headers, IEnumerable<object[]> rows)
        {
            var ws = wb.AddWorksheet(title);
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cell(1, c + 1);
                cell.Value = headers[c];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E78");
                cell.Style.Font.FontColor = XLColor.White;
            }
            int r = 2;
            foreach (var row in rows) { for (int c = 0; c < row.Length; c++) SetCell(ws.Cell(r, c + 1), row[c]); r++; }
            ws.SheetView.FreezeRows(1);
            ws.Columns().AdjustToContents();
            foreach (var col in ws.ColumnsUsed()) if (col.Width > 60) col.Width = 60;
        }

        // ClosedXML guaranteed implicit conversions (avoids ambiguous object overloads).
        private static void SetCell(IXLCell cell, object v)
        {
            if (v == null) { cell.Value = ""; return; }
            if (v is decimal dm) { cell.Value = dm; return; }
            if (v is double db) { cell.Value = db; return; }
            if (v is int ii) { cell.Value = ii; return; }
            cell.Value = v.ToString();
        }

        // ==============================================================
        //  CRM helpers
        // ==============================================================
        private static EntityReference ResolveByName(CrmServiceClient svc, string entity, string nameField, string idField, string value)
        {
            var q = new QueryExpression(entity) { ColumnSet = new ColumnSet(idField, nameField), TopCount = 1, NoLock = true };
            q.Criteria.AddCondition(nameField, ConditionOperator.Equal, value);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var res = svc.RetrieveMultiple(q);
            if (res.Entities.Count == 0) return null;
            var e = res.Entities[0];
            return new EntityReference(entity, e.Id) { Name = e.GetAttributeValue<string>(nameField) };
        }

        // Find-or-create a product by name. When isPackage, guarantees new_ispackage = true (updates if needed).
        private static EntityReference FindOrCreateProduct(CrmServiceClient svc, string name, bool isPackage)
        {
            if (ProductCache.TryGetValue(Norm(name), out var cached))
            {
                if (isPackage) EnsureProductIsPackage(svc, cached.Id);
                return cached;
            }
            var q = new QueryExpression(ProductEntity) { ColumnSet = new ColumnSet(ProductNameField, IsPackageField), TopCount = 1, NoLock = true };
            q.Criteria.AddCondition(ProductNameField, ConditionOperator.Equal, name);
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var res = svc.RetrieveMultiple(q);

            EntityReference prodRef;
            if (res.Entities.Count > 0)
            {
                var e = res.Entities[0];
                prodRef = new EntityReference(ProductEntity, e.Id) { Name = name };
                if (isPackage && e.GetAttributeValue<bool>(IsPackageField) != true)
                    svc.Update(new Entity(ProductEntity, e.Id) { [IsPackageField] = true });
            }
            else
            {
                var prod = new Entity(ProductEntity) { [ProductNameField] = name };
                if (isPackage) prod[IsPackageField] = true;
                prodRef = new EntityReference(ProductEntity, svc.Create(prod)) { Name = name };
            }
            ProductCache[Norm(name)] = prodRef;
            return prodRef;
        }

        private static void EnsureProductIsPackage(CrmServiceClient svc, Guid productId)
        {
            var e = svc.Retrieve(ProductEntity, productId, new ColumnSet(IsPackageField));
            if (e.GetAttributeValue<bool>(IsPackageField) != true)
                svc.Update(new Entity(ProductEntity, productId) { [IsPackageField] = true });
        }

        // Component product ids already linked to this package (for idempotent re-runs).
        private static HashSet<Guid> GetExistingComponentProductIds(CrmServiceClient svc, Guid packageProductId)
        {
            var set = new HashSet<Guid>();
            var q = new QueryExpression(PkgComponentEntity) { ColumnSet = new ColumnSet(PkgComponentComponent), NoLock = true };
            q.Criteria.AddCondition(PkgComponentPackage, ConditionOperator.Equal, packageProductId);
            foreach (var e in svc.RetrieveMultiple(q).Entities)
            {
                var cref = e.GetAttributeValue<EntityReference>(PkgComponentComponent);
                if (cref != null) set.Add(cref.Id);
            }
            return set;
        }

        private static Dictionary<string, EntityReference> ResolveCollections(CrmServiceClient svc, List<string> names)
        {
            var map = new Dictionary<string, EntityReference>(StringComparer.OrdinalIgnoreCase);
            if (names.Count == 0) return map;
            var q = new QueryExpression(CollectionEntity) { ColumnSet = new ColumnSet(CollectionNameField), NoLock = true };
            q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
            var filter = new FilterExpression(LogicalOperator.Or);
            foreach (var n in names) filter.AddCondition(CollectionNameField, ConditionOperator.Equal, n);
            q.Criteria.AddFilter(filter);
            foreach (var e in svc.RetrieveMultiple(q).Entities)
            {
                string nm = e.GetAttributeValue<string>(CollectionNameField);
                if (!string.IsNullOrWhiteSpace(nm) && !map.ContainsKey(Norm(nm)))
                    map[Norm(nm)] = new EntityReference(CollectionEntity, e.Id) { Name = nm };
            }
            return map;
        }

        private static List<Entity> RetrieveSeasonInventory(CrmServiceClient svc, Guid seasonId)
        {
            var cols = new ColumnSet("new_inventoryid", "new_name", "new_rate", "new_expense",
                "new_quantity", "new_sold", "new_unsold", "new_collection", "new_division", "new_productid", "new_seasonid", IsPackageField);
            var q = new QueryExpression("new_inventory") { ColumnSet = cols, NoLock = true, PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 } };
            q.Criteria.AddCondition("new_seasonid", ConditionOperator.Equal, seasonId);
            var all = new List<Entity>();
            while (true)
            {
                var res = svc.RetrieveMultiple(q);
                all.AddRange(res.Entities);
                if (!res.MoreRecords) break;
                q.PageInfo.PageNumber++; q.PageInfo.PagingCookie = res.PagingCookie;
            }
            return all;
        }

        // Delta-only update. Does NOT touch new_expense (hard cost). Flags new_ispackage on package rows.
        private static List<string> UpdateExisting(CrmServiceClient svc, Entity cur, InvRow r, EntityReference coll)
        {
            var upd = new Entity("new_inventory", cur.Id);
            var changes = new List<string>();

            if (r.Rate.HasValue && CurMoney(cur, "new_rate") != r.Rate.Value) { upd["new_rate"] = new Money(r.Rate.Value); changes.Add("rate"); }
            if (r.Quantity.HasValue && CurDec(cur, "new_quantity") != r.Quantity.Value)
            {
                upd["new_quantity"] = r.Quantity.Value;
                upd["new_unsold"] = r.Quantity.Value - CurDec(cur, "new_sold");
                changes.Add("quantity");
            }
            if (coll != null)
            {
                var curColl = cur.GetAttributeValue<EntityReference>("new_collection");
                if (curColl == null || curColl.Id != coll.Id) { upd["new_collection"] = coll; changes.Add("collection"); }
            }
            if (r.IsPackage && cur.GetAttributeValue<bool>(IsPackageField) != true) { upd[IsPackageField] = true; changes.Add("ispackage"); }
            if (_division != null)
            {
                var curDiv = cur.GetAttributeValue<EntityReference>("new_division");
                if (curDiv == null || curDiv.Id != _division.Id) { upd["new_division"] = _division; changes.Add("division"); }
            }

            if (upd.Attributes.Count == 0) return changes;
            svc.Update(upd);
            return changes;
        }

        private static Guid CreateNetNew(CrmServiceClient svc, InvRow r, EntityReference seasonRef, EntityReference coll)
        {
            var e = new Entity("new_inventory");
            e["new_name"] = r.Name;
            e["new_seasonid"] = seasonRef;
            if (coll != null) e["new_collection"] = coll;
            if (r.Rate.HasValue) e["new_rate"] = new Money(r.Rate.Value);
            if (r.Quantity.HasValue)
            {
                e["new_quantity"] = r.Quantity.Value;
                e["new_unsold"] = r.Quantity.Value;   // net-new: sold = 0
                e["new_sold"] = 0m; e["new_pitched"] = 0m; e["new_allocated"] = 0m;
            }
            if (r.IsPackage) e[IsPackageField] = true; // also mirrored from product by the plugin
            if (_division != null) e["new_division"] = _division; // Storm (product has none -> workflow won't override)

            // Product: find-or-create by name; packages get new_ispackage = true so the mirror is correct.
            e["new_productid"] = FindOrCreateProduct(svc, r.Name, isPackage: r.IsPackage);
            return svc.Create(e);
        }

        private static void WriteBackupCsv(string path, List<Entity> snapshot)
        {
            using (var sw = new StreamWriter(path, false, Encoding.UTF8))
            {
                sw.WriteLine("new_inventoryid,new_name,new_ispackage,new_rate,new_expense,new_quantity,new_sold,new_unsold,new_collectionid,new_productid,new_seasonid");
                foreach (var e in snapshot)
                    sw.WriteLine(string.Join(",",
                        e.Id, Csv(e.GetAttributeValue<string>("new_name")), e.GetAttributeValue<bool>(IsPackageField),
                        Money(e, "new_rate"), Money(e, "new_expense"),
                        Dec(e, "new_quantity"), Dec(e, "new_sold"), Dec(e, "new_unsold"),
                        RefId(e, "new_collection"), RefId(e, "new_productid"), RefId(e, "new_seasonid")));
            }
        }

        // ==============================================================
        //  Package parent resolution (Entitlement rule + aliases + direct)
        // ==============================================================
        private static string ResolveParentKey(string name, Dictionary<string, List<Comp>> comps, out string how)
        {
            string n = Norm(name);
            if (n.Contains("entitlement game presenting"))
            {
                var p = comps.Keys.FirstOrDefault(k => k.Contains("entitlement game") && k.Contains("presenting"));
                if (p != null) { how = "entitlement-rule"; return p; }
            }
            if (n.Contains("entitlement game supporting"))
            {
                var p = comps.Keys.FirstOrDefault(k => k.Contains("entitlement game") && k.Contains("supporting"));
                if (p != null) { how = "entitlement-rule"; return p; }
            }
            if (PackageAlias.TryGetValue(n, out string alias) && comps.ContainsKey(Norm(alias))) { how = "alias-confirm"; return Norm(alias); }
            if (comps.ContainsKey(n)) { how = "direct"; return n; }
            how = "unmatched"; return null;
        }

        // ==============================================================
        //  Parsing / normalization helpers
        // ==============================================================
        private static IXLWorksheet FindSheet(XLWorkbook wb, string name)
            => wb.Worksheets.FirstOrDefault(w => string.Equals(w.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
        private static string GetStr(IXLCell c) => c == null || c.IsEmpty() ? null : c.GetValue<string>().Trim();
        private static string Collapse(string s) => string.IsNullOrWhiteSpace(s) ? s : Regex.Replace(s.Trim(), @"\s+", " ");
        private static string Norm(string s) => (Collapse(s ?? "") ?? "").ToLowerInvariant();

        private static string CanonCollection(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string c = Collapse(raw);
            return CollectionAlias.TryGetValue(c, out string alias) ? alias : c;
        }

        private static decimal? ParseRate(IXLCell cell, out string state)
        {
            state = "empty";
            if (cell == null || cell.IsEmpty()) return null;
            if (cell.TryGetValue(out double d)) { state = "ok"; return (decimal)d; }
            string s = cell.GetValue<string>().Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"^\$?\s*([\d,]+(?:\.\d+)?)$");
            if (m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v)) { state = "ok"; return v; }
            state = "unparseable"; return null;
        }

        private static decimal? ParseQty(IXLCell cell, out string state)
        {
            state = "empty";
            if (cell == null || cell.IsEmpty()) return null;
            if (cell.TryGetValue(out double d)) { state = "ok"; return Math.Round((decimal)d); }
            string s = cell.GetValue<string>().Trim();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (s.Equals("Unlimited", StringComparison.OrdinalIgnoreCase)) { state = "unlimited"; return UNLIMITED_QUANTITY; }
            if (Regex.IsMatch(s, @"^[\d,]+$") && decimal.TryParse(s.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal v)) { state = "ok"; return v; }
            state = "unparseable"; return null;
        }

        private static decimal? GetDec(IXLCell c)
        {
            if (c == null || c.IsEmpty()) return null;
            if (c.TryGetValue(out double d)) return (decimal)d;
            if (decimal.TryParse(c.GetValue<string>().Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out decimal m)) return m;
            return null;
        }

        private static decimal CurMoney(Entity e, string f) => e.GetAttributeValue<Money>(f)?.Value ?? 0m;
        private static decimal CurDec(Entity e, string f)
        {
            object v = e.Contains(f) ? e[f] : null;
            if (v == null) return 0m;
            try { return Convert.ToDecimal(v); } catch { return 0m; }
        }
        private static string Money(Entity e, string f) => (e.GetAttributeValue<Money>(f)?.Value ?? 0m).ToString(CultureInfo.InvariantCulture);
        private static string Dec(Entity e, string f) => CurDec(e, f).ToString(CultureInfo.InvariantCulture);
        private static string RefId(Entity e, string f) => e.GetAttributeValue<EntityReference>(f)?.Id.ToString() ?? "";
        private static string Csv(string s) => string.IsNullOrEmpty(s) ? "" : (s.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s);
        private static string InvKey(string name, Guid? collectionId) => Norm(name) + "|" + (collectionId?.ToString("N") ?? "");
        private static void Pause() { Console.WriteLine("\nDone. Press Enter to exit..."); Console.ReadLine(); }
    }
}
