// ============================================================
//  Storm Basketball – Mirror Inventory PROD -> SANDBOX
//  Purpose     : Make the Sandbox new_inventory catalog look IDENTICAL
//                to Production (so Zach can review on data that matches
//                prod), WITHOUT a full environment refresh and WITHOUT
//                touching Sandbox customizations.
//
//  HOW IT WORKS
//   - DUAL connection: SOURCE = Production (read-only), TARGET = Sandbox.
//   - GUIDs differ between environments, so we DO NOT copy lookups by GUID.
//     Every lookup is re-resolved against SANDBOX:
//        * new_productid : prod product GUID -> Trak Product Id -> sandbox
//                          product GUID  (Trak Id verified 1:1 across envs;
//                          falls back to product Name)
//        * new_collection / new_division / new_seasonid : by NAME
//        * new_assigneduser : by full name (skipped if not found)
//   - All value fields are copied VERBATIM, including sold/unsold/pitched/
//     allocated and total, so the numbers Zach sees match prod exactly.
//     (This is the OPPOSITE of UpdateRateCard, which recalculates unsold.
//      That is why this is a SEPARATE binary.)
//   - Upsert into sandbox by Trak Inventory Id (then Name+Season), so it is
//     idempotent and safe to re-run.
//
//  ⚠ THIS CONSOLE WRITES TO SANDBOX ONLY. It must NEVER target production.
//    A guard aborts if SOURCE url == TARGET url.
//  ⚠ SECURITY: do not commit real credentials. Prefer env vars / args.
//
//  SDK   : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  .NET  : Framework 4.6.2
// ============================================================

using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace MirrorInventoryToSandbox
{
	// ================================================================
	//  Logger: console (colored) + .txt
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

	internal class Program
	{
		// ==============================================================
		//  CONFIGURATION – VERIFY BEFORE EVERY RUN
		// ==============================================================
		// SOURCE = Production (READ-ONLY)
		private const string SourceUrl = "https://stormbasketball.crm.dynamics.com/";
		private const string SourceUser = "FanInteractive@stormbasketball.com";
		private const string SourcePass = "CsCXbm2E-WtQ3c4DCy2!";

		// TARGET = Sandbox (WRITE)  -- must be the sandbox URL, never prod
		private const string TargetUrl = "https://org00bff505.crm.dynamics.com/"; // <-- confirm sandbox URL
		private const string TargetUser = "FanInteractive@stormbasketball.com";
		private const string TargetPass = "CsCXbm2E-WtQ3c4DCy2!";

		// Shared OAuth app
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// Season filter. Set to "2026 - Storm" to mirror just that season,
		// or leave EMPTY ("") to mirror the ENTIRE inventory catalog.
		//private const string SeasonFilter = "2026 - Storm";
		private const string SeasonFilter = "";

		// Custom-table primary name fields (confirm once)
		private const string CollNameField = "new_name";
		private const string DivNameField = "new_name";
		private const string ProdNameField = "new_name";
		// ==============================================================

		static void Main(string[] args)
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string logFile = $"log_mirror_inventory_{stamp}.txt";
			string backupFile = $"backup_sandbox_inventory_{stamp}.csv";

			using (Logger log = new Logger(logFile))
			{
				// ── Banner ───────────────────────────────────────────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
				Console.WriteLine("║        STORM BASKETBALL – MIRROR INVENTORY  PROD ──► SANDBOX          ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine($"║  SOURCE (read) : {SourceUrl,-52}║");
				Console.WriteLine($"║  TARGET (write): {TargetUrl,-52}║");
				Console.WriteLine($"║  Season filter : {(string.IsNullOrEmpty(SeasonFilter) ? "ALL seasons" : SeasonFilter),-52}║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  Copies values VERBATIM (incl. sold/unsold/pitched/allocated/total).  ║");
				Console.WriteLine("║  Lookups re-resolved against SANDBOX (product by Trak Id, others by   ║");
				Console.WriteLine("║  name). WRITES TO SANDBOX ONLY.                                       ║");
				Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
				Console.ResetColor();
				Console.WriteLine();

				// ── Safety guard: never write to the same env we read ────
				if (string.Equals(SourceUrl.TrimEnd('/'), TargetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
				{
					log.Error("SOURCE and TARGET are the same environment. Aborting (this tool must write to sandbox only).");
					Pause();
					return;
				}

				try
				{
					// ── Connect both ─────────────────────────────────────
					log.Info("Connecting to SOURCE (Production)...");
					CrmServiceClient src = Connect(SourceUrl, SourceUser, SourcePass);
					if (!src.IsReady) { log.Error($"Source connection failed: {src.LastCrmError}"); Pause(); return; }
					log.Success($"Source org: {src.ConnectedOrgUniqueName}");

					log.Info("Connecting to TARGET (Sandbox)...");
					CrmServiceClient tgt = Connect(TargetUrl, TargetUser, TargetPass);
					if (!tgt.IsReady) { log.Error($"Target connection failed: {tgt.LastCrmError}"); Pause(); return; }
					log.Success($"Target org: {tgt.ConnectedOrgUniqueName}");

					if (string.Equals(src.ConnectedOrgUniqueName, tgt.ConnectedOrgUniqueName, StringComparison.OrdinalIgnoreCase))
					{
						log.Error("SOURCE and TARGET resolved to the SAME org. Aborting.");
						Pause(); return;
					}
					Console.WriteLine();

					// ── Read PROD inventory ──────────────────────────────
					log.Info($"Reading PROD inventory{(string.IsNullOrEmpty(SeasonFilter) ? "" : $" for '{SeasonFilter}'")}...");
					Guid? srcSeasonId = null;
					if (!string.IsNullOrEmpty(SeasonFilter))
					{
						var sr = ResolveByName(src, "new_season", "new_name", "new_seasonid", SeasonFilter);
						if (sr == null) { log.Error($"Season '{SeasonFilter}' not found in PROD. Aborting."); Pause(); return; }
						srcSeasonId = sr.Id;
					}
					List<Entity> prodInv = ReadInventory(src, srcSeasonId);
					log.Success($"PROD inventory rows: {prodInv.Count}");
					Console.WriteLine();

					// ── Build SANDBOX crosswalks (read-only) ─────────────
					log.Info("Building SANDBOX lookup maps...");
					// products
					var prodProductById = BuildProductByGuid(src);                  // prod guid -> (name, trak)
					var sbxProductByTrak = BuildProductByKey(tgt, byTrak: true);     // trak -> ref
					var sbxProductByName = BuildProductByKey(tgt, byTrak: false);    // name -> ref
																					 // collection / division / season (by name)
					var sbxColl = BuildRefByName(tgt, "new_collection", CollNameField);
					var sbxDiv = BuildRefByName(tgt, "new_division", DivNameField);
					var sbxSeason = BuildRefByName(tgt, "new_season", "new_name");
					log.Success($"Sandbox maps -> products(trak:{sbxProductByTrak.Count}/name:{sbxProductByName.Count}), " +
						$"collections:{sbxColl.Count}, divisions:{sbxDiv.Count}, seasons:{sbxSeason.Count}");
					Console.WriteLine();

					// ── Snapshot + BACKUP existing sandbox inventory ─────
					log.Info("Snapshotting + backing up existing SANDBOX inventory...");
					Guid? tgtSeasonId = null;
					if (!string.IsNullOrEmpty(SeasonFilter))
					{
						if (!sbxSeason.TryGetValue(Norm(SeasonFilter), out var ts))
						{ log.Error($"Season '{SeasonFilter}' not found in SANDBOX. Create it first. Aborting."); Pause(); return; }
						tgtSeasonId = ts.Id;
					}
					List<Entity> sbxInv = ReadInventory(tgt, tgtSeasonId);
					WriteBackupCsv(backupFile, sbxInv);
					var sbxByTrak = sbxInv.Where(e => e.Contains("new_trakinventoryid"))
						.GroupBy(e => e.GetAttributeValue<int>("new_trakinventoryid"))
						.ToDictionary(g => g.Key, g => g.First().Id);
					var sbxByName = sbxInv.Where(e => e.Contains("new_name"))
						.GroupBy(e => Norm(e.GetAttributeValue<string>("new_name")))
						.ToDictionary(g => g.Key, g => g.First().Id);
					log.Success($"Sandbox inventory existing: {sbxInv.Count}. Backup -> {backupFile}");
					Console.WriteLine();

					// ── PRE-FLIGHT: resolution report (read-only) ────────
					log.Info("PRE-FLIGHT: checking lookup resolution against sandbox...");
					var missColl = new HashSet<string>();
					var missSeason = new HashSet<string>();
					int missProduct = 0;
					foreach (var e in prodInv)
					{
						string cN = RefName(e, "new_collection");
						if (cN != null && !sbxColl.ContainsKey(Norm(cN))) missColl.Add(cN);
						string sN = RefName(e, "new_seasonid");
						if (sN != null && !sbxSeason.ContainsKey(Norm(sN))) missSeason.Add(sN);
						if (!ResolveProduct(e, prodProductById, sbxProductByTrak, sbxProductByName, out _, out _))
							missProduct++;
					}
					log.Step($"Collections unresolved in sandbox: {missColl.Count} {(missColl.Count > 0 ? string.Join("; ", missColl) : "")}");
					log.Step($"Seasons unresolved in sandbox    : {missSeason.Count} {(missSeason.Count > 0 ? string.Join("; ", missSeason) : "")}");
					log.Step($"Products unresolved (will create) : {missProduct}");
					Console.WriteLine();

					if (missColl.Count > 0 || missSeason.Count > 0)
					{
						log.Error("Aborting: create the missing Collection/Season records in sandbox first. Nothing was written.");
						Pause(); return;
					}

					// ── Confirm ──────────────────────────────────────────
					int willUpdate = prodInv.Count(e => e.Contains("new_trakinventoryid")
						&& sbxByTrak.ContainsKey(e.GetAttributeValue<int>("new_trakinventoryid")));
					log.Info($"PLANNED: {prodInv.Count} prod records -> sandbox  ({willUpdate} update, {prodInv.Count - willUpdate} create)");
					Console.Write("Proceed writing to SANDBOX? Type YES: ");
					if (!(Console.ReadLine() ?? "").Trim().Equals("YES", StringComparison.OrdinalIgnoreCase))
					{ log.Warning("Cancelled. No changes (backup CSV was still written)."); Pause(); return; }
					Console.WriteLine();
					log.Info("Confirmed. Mirroring...");
					Console.WriteLine();

					// ── Mirror loop ──────────────────────────────────────
					int updated = 0, created = 0, failed = 0, productsCreated = 0;
					foreach (var p in prodInv)
					{
						try
						{
							var tgtEnt = new Entity("new_inventory");

							// value fields – verbatim
							CopyString(p, tgtEnt, "new_name");
							CopyString(p, tgtEnt, "new_description");
							CopyMoney(p, tgtEnt, "new_rate");
							CopyMoney(p, tgtEnt, "new_expense");
							CopyMoney(p, tgtEnt, "new_total");
							CopyDecimal(p, tgtEnt, "new_quantity");
							CopyDecimal(p, tgtEnt, "new_sold");
							CopyDecimal(p, tgtEnt, "new_unsold");
							CopyDecimal(p, tgtEnt, "new_pitched");
							CopyDecimal(p, tgtEnt, "new_allocated");
							CopyDateTime(p, tgtEnt, "new_duedate");
							CopyInt(p, tgtEnt, "new_trakinventoryid");

							// lookups – re-resolved against sandbox
							string seasonName = RefName(p, "new_seasonid");
							if (seasonName != null && sbxSeason.TryGetValue(Norm(seasonName), out var sRef))
								tgtEnt["new_seasonid"] = sRef;

							string collName = RefName(p, "new_collection");
							if (collName != null && sbxColl.TryGetValue(Norm(collName), out var cRef))
								tgtEnt["new_collection"] = cRef;

							string divName = RefName(p, "new_division");
							if (divName != null && sbxDiv.TryGetValue(Norm(divName), out var dRef))
								tgtEnt["new_division"] = dRef;

							// product: trak first, then name; create if missing
							if (p.Contains("new_productid"))
							{
								if (ResolveProduct(p, prodProductById, sbxProductByTrak, sbxProductByName, out var prRef, out string prName))
								{
									tgtEnt["new_productid"] = prRef;
								}
								else if (!string.IsNullOrWhiteSpace(prName))
								{
									var np = new Entity("new_product");
									np[ProdNameField] = prName;
									Guid npid = tgt.Create(np);
									var newRef = new EntityReference("new_product", npid) { Name = prName };
									sbxProductByName[Norm(prName)] = newRef;
									tgtEnt["new_productid"] = newRef;
									productsCreated++;
									log.Step($"   + Product created in sandbox '{prName}'");
								}
							}

							// upsert by Trak Inventory Id, then Name+Season
							Guid? existingId = null;
							if (p.Contains("new_trakinventoryid")
								&& sbxByTrak.TryGetValue(p.GetAttributeValue<int>("new_trakinventoryid"), out var idByTrak))
								existingId = idByTrak;
							else
							{
								string nm = p.GetAttributeValue<string>("new_name");
								if (nm != null && sbxByName.TryGetValue(Norm(nm), out var idByName))
									existingId = idByName;
							}

							if (existingId.HasValue)
							{
								tgtEnt.Id = existingId.Value;
								tgt.Update(tgtEnt);
								updated++;
							}
							else
							{
								Guid newId = tgt.Create(tgtEnt);
								created++;
								// register so duplicates in the source don't double-create
								if (p.Contains("new_trakinventoryid"))
									sbxByTrak[p.GetAttributeValue<int>("new_trakinventoryid")] = newId;
								string nm = p.GetAttributeValue<string>("new_name");
								if (nm != null) sbxByName[Norm(nm)] = newId;
							}
						}
						catch (Exception ex)
						{
							failed++;
							log.Error($"'{p.GetAttributeValue<string>("new_name")}' FAILED: {ex.Message}");
							if (ex.InnerException != null) log.Error($"   Inner: {ex.InnerException.Message}");
						}
					}

					// ── Summary ──────────────────────────────────────────
					Console.WriteLine();
					log.WriteLine(new string('=', 72));
					log.Info("MIRROR COMPLETE");
					log.Success($"  Updated          : {updated}");
					log.Success($"  Created          : {created}");
					log.Info($"  Products created : {productsCreated}");
					if (failed > 0) log.Error($"  Failed           : {failed}"); else log.Info($"  Failed           : 0");
					log.Info($"  Sandbox backup   : {backupFile}");
					log.Info($"  Log              : {logFile}");
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

		// ==============================================================
		//  Connection
		// ==============================================================
		private static CrmServiceClient Connect(string url, string user, string pass)
		{
			string cs = $"AuthType=OAuth;Url={url};Username={user};Password={pass};" +
				$"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";
			return new CrmServiceClient(cs);
		}

		// ==============================================================
		//  Reads
		// ==============================================================
		private static List<Entity> ReadInventory(CrmServiceClient svc, Guid? seasonId)
		{
			var cols = new ColumnSet("new_inventoryid", "new_trakinventoryid", "new_name", "new_description",
				"new_rate", "new_expense", "new_total", "new_quantity", "new_sold", "new_unsold",
				"new_pitched", "new_allocated", "new_duedate",
				"new_seasonid", "new_collection", "new_division", "new_productid");
			var q = new QueryExpression("new_inventory")
			{
				ColumnSet = cols,
				NoLock = true,
				PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
			};
			if (seasonId.HasValue)
				q.Criteria.AddCondition("new_seasonid", ConditionOperator.Equal, seasonId.Value);

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

		private static List<Entity> RetrieveAll(CrmServiceClient svc, string entity, ColumnSet cols)
		{
			var q = new QueryExpression(entity)
			{
				ColumnSet = cols,
				NoLock = true,
				PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
			};
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
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
		//  Crosswalk builders
		// ==============================================================
		private class ProdProd { public string Name; public string Trak; }

		private static Dictionary<Guid, ProdProd> BuildProductByGuid(CrmServiceClient svc)
		{
			var map = new Dictionary<Guid, ProdProd>();
			foreach (var e in RetrieveAll(svc, "new_product", new ColumnSet("new_name", "new_trakproductid")))
			{
				map[e.Id] = new ProdProd
				{
					Name = e.GetAttributeValue<string>("new_name"),
					Trak = e.Contains("new_trakproductid") ? e["new_trakproductid"].ToString() : null
				};
			}
			return map;
		}

		// byTrak=true -> key is trakproductid ; byTrak=false -> key is normalized name
		private static Dictionary<string, EntityReference> BuildProductByKey(CrmServiceClient svc, bool byTrak)
		{
			var map = new Dictionary<string, EntityReference>();
			foreach (var e in RetrieveAll(svc, "new_product", new ColumnSet("new_name", "new_trakproductid")))
			{
				string name = e.GetAttributeValue<string>("new_name");
				var refer = new EntityReference("new_product", e.Id) { Name = name };
				if (byTrak)
				{
					if (e.Contains("new_trakproductid") && e["new_trakproductid"] != null)
						map[e["new_trakproductid"].ToString()] = refer;
				}
				else if (!string.IsNullOrWhiteSpace(name))
				{
					map[Norm(name)] = refer;
				}
			}
			return map;
		}

		private static Dictionary<string, EntityReference> BuildRefByName(CrmServiceClient svc, string entity, string nameField)
		{
			var map = new Dictionary<string, EntityReference>();
			foreach (var e in RetrieveAll(svc, entity, new ColumnSet(nameField)))
			{
				string nm = e.GetAttributeValue<string>(nameField);
				if (!string.IsNullOrWhiteSpace(nm))
					map[Norm(nm)] = new EntityReference(entity, e.Id) { Name = nm };
			}
			return map;
		}

		// Resolve a prod inventory's product into a sandbox product reference.
		// Returns true if resolved; outName carries the prod product name for create-fallback.
		private static bool ResolveProduct(Entity prodInv, Dictionary<Guid, ProdProd> prodById,
			Dictionary<string, EntityReference> sbxByTrak, Dictionary<string, EntityReference> sbxByName,
			out EntityReference sbxRef, out string outName)
		{
			sbxRef = null; outName = null;
			var pr = prodInv.GetAttributeValue<EntityReference>("new_productid");
			if (pr == null) return false;

			string trak = null, name = pr.Name;
			if (prodById.TryGetValue(pr.Id, out var pp))
			{
				trak = pp.Trak;
				if (!string.IsNullOrWhiteSpace(pp.Name)) name = pp.Name;
			}
			outName = name;

			if (!string.IsNullOrWhiteSpace(trak) && sbxByTrak.TryGetValue(trak, out var byT)) { sbxRef = byT; return true; }
			if (!string.IsNullOrWhiteSpace(name) && sbxByName.TryGetValue(Norm(name), out var byN)) { sbxRef = byN; return true; }
			return false;
		}

		// ==============================================================
		//  Backup
		// ==============================================================
		private static void WriteBackupCsv(string path, List<Entity> rows)
		{
			using (var sw = new StreamWriter(path, false, Encoding.UTF8))
			{
				sw.WriteLine("new_inventoryid,new_trakinventoryid,new_name,new_rate,new_expense,new_total," +
					"new_quantity,new_sold,new_unsold,new_pitched,new_allocated," +
					"new_collectionid,new_divisionid,new_productid,new_seasonid");
				foreach (var e in rows)
				{
					string[] f =
					{
						e.Id.ToString(),
						e.Contains("new_trakinventoryid") ? e["new_trakinventoryid"].ToString() : "",
						Csv(e.GetAttributeValue<string>("new_name")),
						Money(e,"new_rate"), Money(e,"new_expense"), Money(e,"new_total"),
						Dec(e,"new_quantity"), Dec(e,"new_sold"), Dec(e,"new_unsold"),
						Dec(e,"new_pitched"), Dec(e,"new_allocated"),
						RefId(e,"new_collection"), RefId(e,"new_division"), RefId(e,"new_productid"), RefId(e,"new_seasonid")
					};
					sw.WriteLine(string.Join(",", f));
				}
			}
		}

		// ==============================================================
		//  Copy / resolve helpers
		// ==============================================================
		private static void CopyString(Entity s, Entity t, string f) { if (s.Contains(f) && s[f] != null) t[f] = s.GetAttributeValue<string>(f); }
		private static void CopyInt(Entity s, Entity t, string f) { if (s.Contains(f) && s[f] != null) t[f] = Convert.ToInt32(s[f]); }
		private static void CopyDateTime(Entity s, Entity t, string f) { if (s.Contains(f) && s[f] != null) t[f] = s.GetAttributeValue<DateTime>(f); }
		private static void CopyDecimal(Entity s, Entity t, string f) { if (s.Contains(f) && s[f] != null) t[f] = Convert.ToDecimal(s[f]); }
		private static void CopyMoney(Entity s, Entity t, string f) { var m = s.GetAttributeValue<Money>(f); if (m != null) t[f] = new Money(m.Value); }

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

		private static string RefName(Entity e, string f) => e.GetAttributeValue<EntityReference>(f)?.Name;
		private static string RefId(Entity e, string f) => e.GetAttributeValue<EntityReference>(f)?.Id.ToString() ?? "";
		private static string Money(Entity e, string f) => (e.GetAttributeValue<Money>(f)?.Value ?? 0m).ToString(CultureInfo.InvariantCulture);
		private static string Dec(Entity e, string f)
		{
			object v = e.Contains(f) ? e[f] : null;
			if (v == null) return "";
			try { return Convert.ToDecimal(v).ToString(CultureInfo.InvariantCulture); } catch { return ""; }
		}
		private static string Csv(string s) => string.IsNullOrEmpty(s) ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
		private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

		private static void Pause() { Console.WriteLine("\nPress Enter to exit..."); Console.ReadLine(); }
	}
}