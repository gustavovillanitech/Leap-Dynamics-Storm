// ============================================================
//  Storm Basketball – Dynamics 365 Inventory Rate-Card Updater
//  Purpose     : Bulk-update the new_inventory catalog from Zach's
//                Excel (rate / hard cost / quantity) and UPSERT the
//                net-new items (no Trak Inventory Id), all for a single
//                target Season. Designed to run UNCHANGED in Sandbox
//                first, then in Production (same binary, change ONLY
//                the connection consts).
//  Environment : https://stormbasketball.crm.dynamics.com/
//  SDK         : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  Excel       : ClosedXML  (NuGet: ClosedXML)
//  .NET        : Framework 4.6.2
//
//  WHAT IT DOES, IN ORDER
//   1. Connect to Dynamics (single environment).
//   2. Read the Inventory sheet from the Excel file.
//   3. Snapshot ALL inventory of the target Season into memory (1 query).
//   4. BACKUP that snapshot to CSV (revert artifact Ray asked for).
//   5. PRE-FLIGHT (read-only): resolve Season, resolve every Collection
//      name -> GUID, check net-new Products by name, validate that each
//      existing inventory Name matches its Product Name. Report blockers.
//   6. Ask for explicit YES confirmation (this MODIFIES LIVE DATA).
//   7. UPDATE existing (delta-only: write a field only if it changed).
//      - Recompute new_unsold = new_quantity - new_sold (live sold).
//      - Never touch new_sold / new_pitched / new_allocated.
//   8. UPSERT net-new (dedupe by Name+Season; find-or-create Product).
//   9. Summary + full log.
//
//  KEY DECISIONS BAKED IN (agreed with Gustavo)
//   - Sold/Unsold/Pitched/Allocated are plugin-maintained: NOT imported.
//   - new_unsold is recomputed in THIS console (the InventoryManagement
//     plugin only recomputes unsold when a Deal Line QUANTITY changes,
//     so a direct inventory update would otherwise leave unsold stale).
//   - "Unlimited" quantity -> UNLIMITED_QUANTITY = Int32.Max (prod convention).
//   - Descriptions are PHASE 2 (separate curated file): NOT imported here.
//   - Multi-year: processes every season in the TargetSeasons[] list in one run.
//     2026 (the source season) matches by Trak Id and writes the Trak Id; future
//     seasons match by Name+Collection ONLY and do NOT copy the Trak Id (the file's
//     Trak Ids belong to 2026). Collections/Products are resolved once (global tables).
//
//  ⚠ SECURITY: do not commit real credentials. Prefer env vars / args.
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

namespace UpdateRateCard
{
	// ================================================================
	//  Logger: console (colored) + .txt, mirrors the shell-creator tool
	// ================================================================
	internal sealed class Logger : IDisposable
	{
		private readonly StreamWriter _writer;
		private readonly object _lock = new object();

		public Logger(string filePath)
		{
			_writer = new StreamWriter(filePath, append: false, encoding: Encoding.UTF8) { AutoFlush = true };
			WriteLine($"[LOG START] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			WriteLine(new string('=', 72));
		}

		public void WriteLine(string message, ConsoleColor color = ConsoleColor.Gray)
		{
			lock (_lock)
			{
				Console.ForegroundColor = color;
				Console.WriteLine(message);
				Console.ResetColor();
				_writer.WriteLine(message);
			}
		}

		public void Info(string msg) => WriteLine($"[INFO]    {msg}", ConsoleColor.Cyan);
		public void Success(string msg) => WriteLine($"[SUCCESS] {msg}", ConsoleColor.Green);
		public void Warning(string msg) => WriteLine($"[WARNING] {msg}", ConsoleColor.Yellow);
		public void Error(string msg) => WriteLine($"[ERROR]   {msg}", ConsoleColor.Red);
		public void Step(string msg) => WriteLine($"  >> {msg}", ConsoleColor.DarkCyan);

		public void Dispose()
		{
			WriteLine(new string('=', 72));
			WriteLine($"[LOG END]  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			_writer.Dispose();
		}
	}

	// ================================================================
	//  Parsed Excel row
	// ================================================================
	internal class InvRow
	{
		public int ExcelRow { get; set; }
		public int? TrakId { get; set; }          // Inventory ID (col A)
		public string Name { get; set; }          // col B
		public string Season { get; set; }        // col C
		public string Collection { get; set; }    // col D
		public decimal? Rate { get; set; }        // col J  (Rate Card)
		public decimal? HardCost { get; set; }    // col K
		public decimal? Quantity { get; set; }    // col L  (resolved; Unlimited -> const)
		public bool IsUnlimited { get; set; }     // col L was "Unlimited"
		public bool IsNetNew => !TrakId.HasValue;
	}

	internal class Program
	{
		// ==============================================================
		//  CONFIGURATION – VERIFY BEFORE EVERY RUN
		// ==============================================================
		//private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/"; // <-- sandbox URL first!
		private const string EnvUrl = "https://org00bff505.crm.dynamics.com/"; // <-- sandbox URL first!
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// Excel source (Zach's file). Use the Inventory sheet only (Playoffs sheet is a redundant subset).
		private const string ExcelPath = @"C:\Customer Docs\Storm\Deal Options And PlayOff Automation\UpdateRateCard\traksoftware_inventory_2026_02_26_2026_Storm_Inventory_ZM_Updates_V2.xlsx";
		private const string ExcelSheet = "Inventory";

		// Seasons
		private const string SourceSeasonName = "2026 - Storm"; // the season the file's Trak Ids belong to

		// All target seasons processed in ONE run, in order. 2026 first (matched by Trak Id),
		// then future years (matched by Name+Collection). Practice Facility is a separate catalog
		// and is intentionally NOT here — this file is the Storm rate card.
		private static readonly string[] TargetSeasons =
		{
			"2026 - Storm", "2027 - Storm", "2028 - Storm", "2029 - Storm", "2030 - Storm",
			"2031 - Storm", "2032 - Storm", "2033 - Storm", "2034 - Storm", "2035 - Storm"
		};

		// Optional: Excel rows whose Name must NOT propagate to FUTURE years (created in 2026 only).
		// Leave empty until Zach decides on the 2026-only items. Applies only when target != source.
		private static readonly HashSet<string> ExcludeFromFutureYears =
			new HashSet<string>(StringComparer.OrdinalIgnoreCase)
			{
				// "Extra Premium", "Pre-Game Broadcast Spot", "Incremental Games", "Social Boost", "Product Placement",
			};

		// "Unlimited" quantity convention.
		// Production already stores Unlimited as Int32.Max (2147483647) — 887 rows use it.
		// We align to that existing convention instead of inventing a new value.
		private const decimal UNLIMITED_QUANTITY = 2147483647m;

		// Entity / field names that are worth confirming once (primary name fields of custom tables)
		private const string CollectionEntity = "new_collection";
		private const string CollectionNameField = "new_name";   // confirm: primary name of new_collection
		private const string ProductEntity = "new_product";
		private const string ProductNameField = "new_name";      // confirm: primary name of new_product

		// Toggle: set inventory description from "Specifications" col E (default OFF — descriptions are Phase 2)
		private const bool ImportDescriptions = false;
		// ==============================================================

		static void Main(string[] args)
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string logFile = $"log_update_ratecard_{stamp}.txt";

			using (Logger log = new Logger(logFile))
			{
				// ── Banner ───────────────────────────────────────────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
				Console.WriteLine("║      STORM BASKETBALL – DYNAMICS 365  INVENTORY RATE-CARD UPDATER     ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine($"║  Environment   : {EnvUrl,-52}║");
				Console.WriteLine($"║  User          : {CrmUsername,-52}║");
				Console.WriteLine($"║  Source season : {SourceSeasonName,-52}║");
				Console.WriteLine($"║  Target seasons: {TargetSeasons.Length + " seasons (listed below)",-52}║");
				Console.WriteLine($"║  Excel file    : {Path.GetFileName(ExcelPath),-52}║");
				Console.WriteLine($"║  Unlimited qty : {UNLIMITED_QUANTITY + " (Int32.Max, prod convention)",-52}║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  ⚠  This operation MODIFIES LIVE DATA. Verify environment above.      ║");
				Console.WriteLine("║  A CSV backup per season is taken before writes.                     ║");
				Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
				Console.ResetColor();
				Console.WriteLine();

				try
				{
					// ── 1. Connect (once) ────────────────────────────────
					log.Info("Connecting to Dynamics 365 via CrmServiceClient...");
					string conn =
						$"AuthType=OAuth;Url={EnvUrl};Username={CrmUsername};Password={CrmPassword};" +
						$"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
					CrmServiceClient svc = new CrmServiceClient(conn);
					if (!svc.IsReady) { log.Error($"Connection failed: {svc.LastCrmError}"); Pause(); return; }
					log.Success($"Connected. Org: {svc.ConnectedOrgUniqueName}");
					Console.WriteLine();

					// ── 2. Read Excel (once) ─────────────────────────────
					log.Info($"Reading Excel '{Path.GetFileName(ExcelPath)}' sheet '{ExcelSheet}'...");
					List<InvRow> rows = ReadExcel(ExcelPath, ExcelSheet, log);
					log.Success($"Parsed {rows.Count} usable rows " +
						$"({rows.Count(r => !r.IsNetNew)} with Trak Id, {rows.Count(r => r.IsNetNew)} net-new).");
					int unlimitedCount = rows.Count(r => r.IsUnlimited);
					if (unlimitedCount > 0) log.Warning($"{unlimitedCount} rows 'Unlimited' -> {UNLIMITED_QUANTITY}.");
					Console.WriteLine();

					// ── 3. PRE-FLIGHT: resolve GLOBAL lookups (once) ─────
					//      Collections and Products are global tables (not per-season),
					//      so they are resolved a single time before the season loop.
					log.Info("PRE-FLIGHT (read-only): resolving global lookups...");
					var distinctCollections = rows.Where(r => !string.IsNullOrWhiteSpace(r.Collection))
						.Select(r => r.Collection.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, EntityReference> collMap = ResolveCollections(svc, distinctCollections, log);
					var missingColls = distinctCollections.Where(c => !collMap.ContainsKey(Norm(c))).ToList();

					var netNewNames = rows.Where(r => r.IsNetNew && !string.IsNullOrWhiteSpace(r.Name))
						.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, EntityReference> productMap = ResolveProducts(svc, netNewNames);
					int productsToCreate = netNewNames.Count(n => !productMap.ContainsKey(Norm(n)));

					log.Step($"Collections in file: {distinctCollections.Count} | missing in CRM: {missingColls.Count}");
					if (missingColls.Count > 0)
						foreach (var m in missingColls) log.Warning($"   Missing collection: '{m}'");
					log.Step($"Net-new products: {netNewNames.Count} | to create on first season: {productsToCreate}");
					Console.WriteLine();

					// BLOCKER: missing collections would null out new_collection. Stop before any write.
					if (missingColls.Count > 0)
					{
						log.Error("Aborting: create the missing Collection records (or fix names in the file) first. No data modified.");
						Pause(); return;
					}

					// ── 4. Show plan + single confirmation for the whole batch ──
					log.Info("Seasons to process (in order):");
					foreach (var s in TargetSeasons)
						log.Step($"  {s}  [{(string.Equals(s, SourceSeasonName, StringComparison.OrdinalIgnoreCase) ? "Trak Id match" : "Name+Collection match")}]");
					Console.WriteLine();
					Console.Write($"Process ALL {TargetSeasons.Length} seasons on '{svc.ConnectedOrgUniqueName}'? Type YES to confirm: ");
					if (!(Console.ReadLine() ?? "").Trim().Equals("YES", StringComparison.OrdinalIgnoreCase))
					{ log.Warning("Cancelled by user. No changes were made."); Pause(); return; }
					Console.WriteLine();
					log.Info("User confirmed. Processing seasons...");
					Console.WriteLine();

					// ── 5. Iterate seasons ───────────────────────────────
					int gUpd = 0, gCre = 0, gUnch = 0, gFail = 0, done = 0, skipped = 0;
					foreach (var seasonName in TargetSeasons)
					{
						log.WriteLine(new string('#', 72));
						log.Info($"SEASON: {seasonName}");
						SeasonResult r = ProcessSeason(svc, seasonName, rows, collMap, productMap, stamp, log);
						if (r == null) { skipped++; continue; }
						gUpd += r.Updated; gCre += r.Created; gUnch += r.Unchanged; gFail += r.Failed;
						done++;
					}

					// ── Grand summary ────────────────────────────────────
					Console.WriteLine();
					log.WriteLine(new string('=', 72));
					log.Info("ALL SEASONS COMPLETE");
					log.Info($"  Seasons processed : {done} | skipped (not found): {skipped}");
					log.Success($"  Total updated     : {gUpd}");
					log.Info($"  Total unchanged   : {gUnch}");
					log.Success($"  Total created     : {gCre}");
					if (gFail > 0) log.Error($"  Total failed      : {gFail}"); else log.Info($"  Total failed      : 0");
					log.Info($"  Log               : {logFile}");
					log.WriteLine(new string('=', 72));
				}
				catch (Exception ex)
				{
					log.Error($"FATAL: {ex.Message}");
					if (ex.InnerException != null) log.Error($"Inner: {ex.InnerException.Message}");
					log.Error(ex.StackTrace);
				}

				Pause();
			}
		}

		// Per-season tally
		private class SeasonResult { public int Updated, Created, Unchanged, Failed; }

		// ==============================================================
		//  Process ONE season: resolve -> snapshot -> backup -> upsert rows.
		//  Returns null (skipped) if the season can't be resolved.
		//  collMap / productMap are SHARED across seasons (global tables);
		//  productMap is mutated as net-new products get created, so later
		//  seasons reuse the products created during the 2026 pass.
		// ==============================================================
		private static SeasonResult ProcessSeason(CrmServiceClient svc, string seasonName, List<InvRow> rows,
			Dictionary<string, EntityReference> collMap, Dictionary<string, EntityReference> productMap,
			string stamp, Logger log)
		{
			bool isSourceSeason = string.Equals(seasonName, SourceSeasonName, StringComparison.OrdinalIgnoreCase);

			EntityReference seasonRef = ResolveByName(svc, "new_season", "new_name", "new_seasonid", seasonName);
			if (seasonRef == null) { log.Warning($"Season '{seasonName}' not found in CRM. Skipping."); return null; }

			// Snapshot this season's inventory + build match indexes
			List<Entity> snapshot = RetrieveSeasonInventory(svc, seasonRef.Id);
			var byTrak = snapshot.Where(e => e.Contains("new_trakinventoryid"))
				.GroupBy(e => e.GetAttributeValue<int>("new_trakinventoryid"))
				.ToDictionary(g => g.Key, g => g.First());
			// Name is NOT unique within a season (PLAYOFFS duplicates regular items) -> Name+Collection.
			var byKey = snapshot.Where(e => e.Contains("new_name"))
				.GroupBy(e => InvKey(e.GetAttributeValue<string>("new_name"),
									 e.GetAttributeValue<EntityReference>("new_collection")?.Name))
				.ToDictionary(g => g.Key, g => g.First());

			// Per-season backup (revert artifact)
			string backupFile = $"backup_inventory_{seasonName.Replace(" ", "").Replace("-", "")}_{stamp}.csv";
			WriteBackupCsv(backupFile, snapshot);
			log.Step($"Snapshot {snapshot.Count} records | backup -> {backupFile} | match: {(isSourceSeason ? "Trak Id" : "Name+Collection")}");

			// Name<>Product validation only makes sense on the source season (warning only)
			if (isSourceSeason)
				log.Step($"Existing Name<>Product mismatches: {ValidateInventoryProductNames(svc, snapshot, log)}");

			var res = new SeasonResult();
			foreach (var r in rows)
			{
				// In future years, skip rows Zach flagged as 2026-only (empty set by default)
				if (!isSourceSeason && r.Name != null && ExcludeFromFutureYears.Contains(r.Name.Trim()))
					continue;

				try
				{
					Entity match = null;
					if (isSourceSeason && r.TrakId.HasValue && byTrak.TryGetValue(r.TrakId.Value, out var et))
						match = et;
					else if (!string.IsNullOrWhiteSpace(r.Name) && byKey.TryGetValue(InvKey(r.Name, r.Collection), out var en))
						match = en; // net-new dedupe + future-year matching + trak fallback (Name+Collection)

					EntityReference coll = !string.IsNullOrWhiteSpace(r.Collection)
						? collMap[Norm(r.Collection.Trim())] : null;

					if (match != null)
					{
						bool changed = UpdateExisting(svc, match, r, coll, log);
						if (changed) res.Updated++; else res.Unchanged++;
					}
					else
					{
						Guid newId = CreateNetNew(svc, r, seasonRef, coll, isSourceSeason, productMap, log);
						// register so a duplicate row in the same file won't create twice
						byKey[InvKey(r.Name, r.Collection)] = new Entity("new_inventory", newId) { ["new_name"] = r.Name };
						res.Created++;
					}
				}
				catch (Exception ex)
				{
					res.Failed++;
					log.Error($"[{seasonName}] Row {r.ExcelRow} '{r.Name}' FAILED: {ex.Message}");
					if (ex.InnerException != null) log.Error($"   Inner: {ex.InnerException.Message}");
				}
			}

			log.Success($"[{seasonName}] updated:{res.Updated} created:{res.Created} unchanged:{res.Unchanged} failed:{res.Failed}");
			Console.WriteLine();
			return res;
		}

		// ==============================================================
		//  Excel reading (ClosedXML)
		//  Columns: A=TrakId B=Name C=Season D=Collection E=Specs
		//           J=RateCard K=HardCost L=Quantity M=Sold N=Unsold
		// ==============================================================
		private static List<InvRow> ReadExcel(string path, string sheet, Logger log)
		{
			var list = new List<InvRow>();
			using (var wb = new XLWorkbook(path))
			{
				var ws = wb.Worksheet(sheet);
				foreach (var row in ws.RowsUsed().Skip(1)) // skip header
				{
					string name = GetStr(row.Cell(2));
					string season = GetStr(row.Cell(3));
					// skip fully empty / headerless rows
					if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(season)) continue;

					var r = new InvRow
					{
						ExcelRow = row.RowNumber(),
						TrakId = GetInt(row.Cell(1)),
						Name = name,
						Season = season,
						Collection = GetStr(row.Cell(4)),
						Rate = GetDec(row.Cell(10)),
						HardCost = GetDec(row.Cell(11)),
					};

					// Quantity: number or "Unlimited"
					string qtyRaw = GetStr(row.Cell(12));
					if (!string.IsNullOrWhiteSpace(qtyRaw) &&
						qtyRaw.Trim().Equals("Unlimited", StringComparison.OrdinalIgnoreCase))
					{
						r.IsUnlimited = true;
						r.Quantity = UNLIMITED_QUANTITY;
					}
					else
					{
						r.Quantity = GetDec(row.Cell(12));
					}

					list.Add(r);
				}
			}
			return list;
		}

		// ==============================================================
		//  Snapshot all inventory of a Season (single query, NoLock)
		// ==============================================================
		private static List<Entity> RetrieveSeasonInventory(CrmServiceClient svc, Guid seasonId)
		{
			var cols = new ColumnSet("new_inventoryid", "new_trakinventoryid", "new_name",
				"new_rate", "new_expense", "new_quantity", "new_sold", "new_unsold",
				"new_pitched", "new_allocated", "new_collection", "new_productid", "new_seasonid");

			var q = new QueryExpression("new_inventory")
			{
				ColumnSet = cols,
				NoLock = true,
				PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
			};
			q.Criteria.AddCondition("new_seasonid", ConditionOperator.Equal, seasonId);

			var all = new List<Entity>();
			while (true)
			{
				var res = svc.RetrieveMultiple(q);
				all.AddRange(res.Entities);
				if (!res.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = res.PagingCookie;
			}
			return all;
		}

		// ==============================================================
		//  BACKUP -> CSV (revert artifact)
		// ==============================================================
		private static void WriteBackupCsv(string path, List<Entity> snapshot)
		{
			using (var sw = new StreamWriter(path, false, Encoding.UTF8))
			{
				sw.WriteLine("new_inventoryid,new_trakinventoryid,new_name,new_rate,new_expense," +
					"new_quantity,new_sold,new_unsold,new_pitched,new_allocated,new_collectionid,new_productid,new_seasonid");
				foreach (var e in snapshot)
				{
					string[] f =
					{
						e.Id.ToString(),
						e.GetAttributeValue<int>("new_trakinventoryid").ToString(),
						Csv(e.GetAttributeValue<string>("new_name")),
						Money(e, "new_rate"), Money(e, "new_expense"),
						Dec(e, "new_quantity"), Dec(e, "new_sold"), Dec(e, "new_unsold"),
						Dec(e, "new_pitched"), Dec(e, "new_allocated"),
						RefId(e, "new_collection"), RefId(e, "new_productid"), RefId(e, "new_seasonid")
					};
					sw.WriteLine(string.Join(",", f));
				}
			}
		}

		// ==============================================================
		//  Lookup resolution helpers
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

		private static Dictionary<string, EntityReference> ResolveCollections(CrmServiceClient svc, List<string> names, Logger log)
		{
			var map = new Dictionary<string, EntityReference>();
			if (names.Count == 0) return map;

			var q = new QueryExpression(CollectionEntity)
			{
				ColumnSet = new ColumnSet(CollectionNameField),
				NoLock = true
			};
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
			var filter = new FilterExpression(LogicalOperator.Or);
			foreach (var n in names) filter.AddCondition(CollectionNameField, ConditionOperator.Equal, n);
			q.Criteria.AddFilter(filter);

			foreach (var e in svc.RetrieveMultiple(q).Entities)
			{
				string nm = e.GetAttributeValue<string>(CollectionNameField);
				if (string.IsNullOrWhiteSpace(nm)) continue;
				map[Norm(nm)] = new EntityReference(CollectionEntity, e.Id) { Name = nm };
			}
			return map;
		}

		private static Dictionary<string, EntityReference> ResolveProducts(CrmServiceClient svc, List<string> names)
		{
			var map = new Dictionary<string, EntityReference>();
			if (names.Count == 0) return map;
			// chunk OR-filters to stay well under condition limits
			foreach (var chunk in Chunk(names, 200))
			{
				var q = new QueryExpression(ProductEntity) { ColumnSet = new ColumnSet(ProductNameField), NoLock = true };
				q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
				var filter = new FilterExpression(LogicalOperator.Or);
				foreach (var n in chunk) filter.AddCondition(ProductNameField, ConditionOperator.Equal, n);
				q.Criteria.AddFilter(filter);
				foreach (var e in svc.RetrieveMultiple(q).Entities)
				{
					string nm = e.GetAttributeValue<string>(ProductNameField);
					if (string.IsNullOrWhiteSpace(nm)) continue;
					map[Norm(nm)] = new EntityReference(ProductEntity, e.Id) { Name = nm };
				}
			}
			return map;
		}

		// Warning-only: confirm existing inventory Name matches its Product Name
		private static int ValidateInventoryProductNames(CrmServiceClient svc, List<Entity> snapshot, Logger log)
		{
			var withProduct = snapshot.Where(e => e.Contains("new_productid")).ToList();
			int mismatches = 0;
			foreach (var e in withProduct)
			{
				var pr = e.GetAttributeValue<EntityReference>("new_productid");
				string invName = e.GetAttributeValue<string>("new_name");
				if (pr != null && !string.IsNullOrWhiteSpace(pr.Name) && !string.IsNullOrWhiteSpace(invName)
					&& !Norm(pr.Name).Equals(Norm(invName)))
				{
					mismatches++;
					if (mismatches <= 15) log.Warning($"   Name<>Product: inv '{invName}' vs product '{pr.Name}'");
				}
			}
			return mismatches;
		}

		// ==============================================================
		//  UPDATE existing (delta-only). Returns true if anything changed.
		// ==============================================================
		private static bool UpdateExisting(CrmServiceClient svc, Entity cur, InvRow r, EntityReference coll, Logger log)
		{
			var upd = new Entity("new_inventory", cur.Id);
			var changes = new List<string>();

			// Name
			if (!string.IsNullOrWhiteSpace(r.Name) &&
				!Norm(r.Name).Equals(Norm(cur.GetAttributeValue<string>("new_name"))))
			{
				upd["new_name"] = r.Name; changes.Add("name");
			}

			// Rate (Money)
			if (r.Rate.HasValue && CurMoney(cur, "new_rate") != r.Rate.Value)
			{
				upd["new_rate"] = new Money(r.Rate.Value); changes.Add("rate");
			}

			// Hard cost -> new_expense (Money)
			if (r.HardCost.HasValue && CurMoney(cur, "new_expense") != r.HardCost.Value)
			{
				upd["new_expense"] = new Money(r.HardCost.Value); changes.Add("expense");
			}

			// Quantity (Decimal) + recompute Unsold = Quantity - Sold (live sold)
			if (r.Quantity.HasValue && CurDec(cur, "new_quantity") != r.Quantity.Value)
			{
				upd["new_quantity"] = r.Quantity.Value;
				decimal sold = CurDec(cur, "new_sold");
				upd["new_unsold"] = r.Quantity.Value - sold;
				changes.Add($"qty({r.Quantity.Value}),unsold({r.Quantity.Value - sold})");
			}

			// Collection (lookup)
			if (coll != null)
			{
				var curColl = cur.GetAttributeValue<EntityReference>("new_collection");
				if (curColl == null || curColl.Id != coll.Id)
				{
					upd["new_collection"] = coll; changes.Add("collection");
				}
			}

			if (upd.Attributes.Count == 0)
				return false;

			svc.Update(upd);
			log.Step($"UPDATED Trak {(r.TrakId?.ToString() ?? "-")} '{r.Name}': {string.Join(", ", changes)}");
			return true;
		}

		// ==============================================================
		//  CREATE net-new (find-or-create Product, set lookups)
		// ==============================================================
		private static Guid CreateNetNew(CrmServiceClient svc, InvRow r, EntityReference seasonRef,
			EntityReference coll, bool isSourceSeason, Dictionary<string, EntityReference> productMap, Logger log)
		{
			var e = new Entity("new_inventory");
			e["new_name"] = r.Name;
			e["new_seasonid"] = seasonRef;
			if (coll != null) e["new_collection"] = coll;
			if (r.Rate.HasValue) e["new_rate"] = new Money(r.Rate.Value);
			if (r.HardCost.HasValue) e["new_expense"] = new Money(r.HardCost.Value);
			if (r.Quantity.HasValue)
			{
				e["new_quantity"] = r.Quantity.Value;
				e["new_unsold"] = r.Quantity.Value; // net-new: sold = 0
				e["new_sold"] = 0m;
				e["new_pitched"] = 0m;
				e["new_allocated"] = 0m;
			}

			// Only carry the Trak Id when loading the SOURCE season (2026).
			// For future years the file's Trak Ids belong to 2026, so we leave it null.
			if (isSourceSeason && r.TrakId.HasValue) e["new_trakinventoryid"] = r.TrakId.Value;

			// Product: find-or-create by name (new_productid is optional but we want it set)
			if (!string.IsNullOrWhiteSpace(r.Name))
			{
				EntityReference prodRef;
				if (productMap.TryGetValue(Norm(r.Name), out var existing))
				{
					prodRef = existing;
				}
				else
				{
					var prod = new Entity(ProductEntity);
					prod[ProductNameField] = r.Name;
					Guid pid = svc.Create(prod);
					prodRef = new EntityReference(ProductEntity, pid) { Name = r.Name };
					productMap[Norm(r.Name)] = prodRef; // cache so duplicates in file reuse it
					log.Step($"   + Product created '{r.Name}'");
				}
				e["new_productid"] = prodRef;
			}

			Guid id = svc.Create(e);
			log.Step($"CREATED net-new '{r.Name}'{(r.IsUnlimited ? " [Unlimited qty]" : "")} -> {id}");
			return id;
		}

		// ==============================================================
		//  Small helpers
		// ==============================================================
		private static string GetStr(IXLCell c) => c == null || c.IsEmpty() ? null : c.GetValue<string>().Trim();

		private static int? GetInt(IXLCell c)
		{
			if (c == null || c.IsEmpty()) return null;
			if (c.TryGetValue(out double d)) return (int)Math.Round(d);
			if (int.TryParse(c.GetValue<string>().Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out int i)) return i;
			return null;
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
		private static string Csv(string s) => string.IsNullOrEmpty(s) ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

		private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();
		// Within a season, inventory Name is NOT unique (PLAYOFFS duplicates regular items).
		// Name+Collection is unique. Use it for matching and dedupe.
		private static string InvKey(string name, string collection) => Norm(name) + "|" + Norm(collection);

		private static IEnumerable<List<T>> Chunk<T>(List<T> src, int size)
		{
			for (int i = 0; i < src.Count; i += size)
				yield return src.GetRange(i, Math.Min(size, src.Count - i));
		}

		private static void Pause()
		{
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}
	}
}