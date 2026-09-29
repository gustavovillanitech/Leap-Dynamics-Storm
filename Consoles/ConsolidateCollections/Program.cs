using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ConsolidateCollections
{
	/// <summary>
	/// Consolidates the new_collection master list.
	///
	/// Ray, 2026-09-25: Storm want the collection list cleaned up - the capitalised names
	/// Nate introduced in the update template are the ones that survive, and the old
	/// lower-case ones should go away.
	///
	/// WHERE A COLLECTION ACTUALLY LIVES (see PROJECT_STATE and ApplyInventoryUpdates):
	/// the collection is owned by the PRODUCT. A real-time workflow, "Set Collection-Division
	/// on Inventory", copies it from the product onto the inventory row on every inventory
	/// create/update, so a collection written straight onto an inventory row is silently
	/// reverted. Consolidating therefore means repointing PRODUCTS, then stamping
	/// new_updondemandcollectiondivision on the inventory rows so the workflow pushes the new
	/// value down. That stamp is applied to every inventory row of a repointed product in
	/// EVERY season, which is what covers the 552 rows cloned into 2027 and 2028.
	///
	/// The workflow, read from the definition on 2026-09-28 (real time, on Create and on field
	/// change, scope Organization), is exactly two steps:
	///     If Inventory:Product Id contains data AND Product Id (Product):Collection contains
	///     data, then Update Inventory          ... and the same again for Division.
	/// Two consequences the code below depends on. It only ever WRITES, never clears, so the
	/// 456 inventory rows whose product has no collection keep the value they are showing.
	/// And a stamp on a row whose product is still blank does nothing at all, which is why the
	/// backfill is written before the stamping and not after it.
	///
	/// Two shapes of change, and they are not the same operation:
	///   RENAME - no record exists under the target name. One update on the collection record.
	///            Every product and inventory row already points at it, so nothing else moves.
	///   MERGE  - a record already exists under the target name. Products must be repointed,
	///            inventory stamped, and only then is the emptied record deactivated.
	///
	/// Nothing is ever deleted. Ray said "delete the old ones"; a deactivated collection is
	/// invisible in new records and can be switched back on, a deleted one cannot be recovered
	/// and takes its GUID with it. Deactivate is the same outcome with a way back.
	///
	/// Run order: AUDIT_ONLY = true first. That writes nothing and prints the census the plan
	/// is built from. Fill MERGES / RENAMES from it, run again with DRY_RUN = true to read the
	/// plan, and only then set DRY_RUN = false.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// PRODUCTION. Change deliberately, never by accident.
		private const string ENV_URL = "https://stormbasketball.crm.dynamics.com/";

		// true = print the census of every collection and stop. No plan, no writes.
		private const bool AUDIT_ONLY = true;

		// true  = report what would happen and write nothing.
		// false = actually apply. Requires typing YES as well.
		// Left back at true after the live run of 2026-09-28. The next person to open this file
		// gets a console that cannot write until they deliberately change this line.
		private const bool DRY_RUN = true;

		// Deactivate a source collection once it has been emptied. Never deletes.
		private const bool DEACTIVATE_EMPTIED = true;

		// Set the collection on a product that has none, copying it from that product's own
		// inventory rows. 157 of the 280 products are in that state: the update template left
		// column E blank on 122 rows, meaning "keep what is there", but what was there lived on
		// the INVENTORY and the collection is owned by the PRODUCT, so it was never pushed up.
		// Nothing is guessed - a product whose inventory rows disagree is reported, not written.
		private static readonly bool BACKFILL_PRODUCT_FROM_INVENTORY = true;

		// RENAME: the record keeps its GUID and changes its name. Every product and inventory row
		// already points at it, so nothing moves - a collection is a lookup and the name follows.
		// Refused if a record already exists under the target name; that is a merge.
		private static readonly Dictionary<string, string> RENAMES = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "Tickets & Hospitality", "TICKETS & HOSPITALITY" },
			{ "Other",                 "OTHER" },
			{ "Game Day Promotions",   "GAME DAY PROMOTIONS" },
		};

		// MERGE: the source name on the left is absorbed into the target on the right.
		// The target MUST already exist, and it is named here by its name AS IT IS TODAY - the
		// renames above have not run yet when this map is resolved, and the record is then held
		// by id, so "Tickets & Hospitality" below and the rename above are the same record.
		private static readonly Dictionary<string, string> MERGES = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "TICKETS AND HOSPITALITY", "Tickets & Hospitality" },   // 1 product, 3 inventory rows
			{ "Community Programs",      "COMMUNITY" },               // 0 products, 3 orphan items
		};

		// Collections that hold nothing at all - no product, no inventory, in any season.
		// Deactivated outright. Nothing points at them, so there is nothing to move first.
		private static readonly string[] DEACTIVATE =
		{
			"COMMUNITY PROGRAMS & EVENTS",
			"Digital - App",
			"Digital - Email",
			"Digital - Social Media",
			"Digital - Text",
			"Digital - Website",
			"PLAYOFFS",
			"PROMOTIONS",
			"SOCIAL MEDIA",
		};

		// ---- entities / attributes ----
		private const string COLL_ENTITY = "new_collection";
		private const string PROD_ENTITY = "new_product";
		private const string INV_ENTITY = "new_inventory";
		private const string SEASON_ENTITY = "new_season";

		private const string NAME = "new_name";
		private const string PROD_COLLECTION = "new_collection";
		private const string INV_COLLECTION = "new_collection";
		private const string INV_PRODUCT = "new_productid";
		private const string INV_SEASON = "new_seasonid";
		private const string INV_STAMP = "new_updondemandcollectiondivision";
		// =====================================================================

		private static bool IsProd { get { return ENV_URL.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0; } }
		private static string EnvTag() { return IsProd ? "PROD" : "SANDBOX"; }
		private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
		private static readonly string Stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
		private static readonly List<string> Log = new List<string>();

		// ---------------------------------------------------------------- models
		private sealed class Coll
		{
			public Guid Id;
			public string Name;
			public int State;                       // 0 active, 1 inactive
			public int Products;
			public int Inventory;
			public readonly SortedDictionary<string, int> BySeason = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			public bool Active { get { return State == 0; } }
		}

		private sealed class Prod
		{
			public Guid Id;
			public string Name;
			public Guid? CollectionId;
		}

		private sealed class Inv
		{
			public Guid Id;
			public string Name;
			public Guid? CollectionId;
			public Guid? ProductId;
			public string Season;
		}

		// ---------------------------------------------------------------- main
		private static void Main()
		{
			Console.Title = "Consolidate Collections - " + EnvTag();
			Console.WriteLine("=====================================================");
			Console.WriteLine(" CONSOLIDATE COLLECTIONS");
			Console.WriteLine("=====================================================");
			Console.WriteLine("Environment : " + ENV_URL + "   [" + EnvTag() + "]");
			Console.WriteLine("Mode        : " + (AUDIT_ONLY ? "AUDIT ONLY (no plan, no writes)"
								: DRY_RUN ? "DRY RUN (plan only, no writes)" : "LIVE - RECORDS WILL BE CHANGED"));
			if (!AUDIT_ONLY)
			{
				Console.WriteLine("Renames     : " + RENAMES.Count);
				Console.WriteLine("Merges      : " + MERGES.Count);
				Console.WriteLine("Deactivate  : " + DEACTIVATE.Length + " empty"
					+ (DEACTIVATE_EMPTIED ? " + merge sources once emptied" : ""));
				Console.WriteLine("Backfill    : " + (BACKFILL_PRODUCT_FROM_INVENTORY
					? "yes - products with no collection take it from their inventory" : "no"));
			}
			if (!AUDIT_ONLY && !DRY_RUN && IsProd)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\n*** THIS IS PRODUCTION AND WRITES ARE ENABLED ***");
				Console.ResetColor();
			}

			Console.Write("\nType YES to continue: ");
			if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "YES")
			{
				Console.WriteLine("\nCancelled. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// Interactive sign-in. No credentials live in this file.
			string cs = "AuthType=OAuth;Url=" + ENV_URL
				+ ";AppId=51f81489-12ee-4a9e-aaae-a2591f45987d"
				+ ";RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

			Console.WriteLine("\nConnecting...");
			CrmServiceClient service = new CrmServiceClient(cs);
			if (!service.IsReady)
			{
				Console.WriteLine("Connection error: " + service.LastCrmError);
				Console.ReadLine();
				return;
			}
			Console.WriteLine("Connected.\n");

			try
			{
				Dictionary<Guid, Coll> colls;
				List<Prod> prods;
				List<Inv> invs;
				LoadAll(service, out colls, out prods, out invs);

				Census(colls, prods, invs);
				WriteAuditCsv(colls);
				LookAlikes(colls);
				Orphans(colls, prods, invs);

				if (AUDIT_ONLY)
				{
					Console.WriteLine("\nAudit only. Nothing was changed.");
				}
				else
				{
					Apply(service, colls, prods, invs);
				}
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\nFATAL: " + ex.Message);
				Console.ResetColor();
				Say("FATAL: " + ex);
			}

			WriteLog();
			Console.WriteLine("\nDone. Press Enter to exit...");
			Console.ReadLine();
		}

		// ---------------------------------------------------------------- load
		private static void LoadAll(IOrganizationService svc, out Dictionary<Guid, Coll> colls,
			out List<Prod> prods, out List<Inv> invs)
		{
			colls = new Dictionary<Guid, Coll>();
			// ColumnSet(true): there are only a handful of collections, and asking for "statecode"
			// by name would throw if this entity turned out not to be state-enabled.
			foreach (Entity e in Page(svc, COLL_ENTITY, new ColumnSet(true), null))
			{
				var c = new Coll
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>(NAME) ?? "",
					State = e.Contains("statecode") ? e.GetAttributeValue<OptionSetValue>("statecode").Value : 0
				};
				colls[c.Id] = c;
			}
			Console.WriteLine("Collections : " + colls.Count);

			prods = new List<Prod>();
			foreach (Entity e in Page(svc, PROD_ENTITY, new ColumnSet(NAME, PROD_COLLECTION), null))
			{
				var r = e.GetAttributeValue<EntityReference>(PROD_COLLECTION);
				prods.Add(new Prod
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>(NAME) ?? "",
					CollectionId = r == null ? (Guid?)null : r.Id
				});
			}
			Console.WriteLine("Products    : " + prods.Count);

			// season names in one pass, so the census can group by season without a lookup per row
			var seasonNames = new Dictionary<Guid, string>();
			foreach (Entity e in Page(svc, SEASON_ENTITY, new ColumnSet(NAME), null))
				seasonNames[e.Id] = e.GetAttributeValue<string>(NAME) ?? "";

			invs = new List<Inv>();
			foreach (Entity e in Page(svc, INV_ENTITY, new ColumnSet(NAME, INV_COLLECTION, INV_PRODUCT, INV_SEASON), null))
			{
				var cr = e.GetAttributeValue<EntityReference>(INV_COLLECTION);
				var pr = e.GetAttributeValue<EntityReference>(INV_PRODUCT);
				var sr = e.GetAttributeValue<EntityReference>(INV_SEASON);
				string season = "(no season)";
				if (sr != null) season = seasonNames.ContainsKey(sr.Id) ? seasonNames[sr.Id] : sr.Id.ToString();
				invs.Add(new Inv
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>(NAME) ?? "",
					CollectionId = cr == null ? (Guid?)null : cr.Id,
					ProductId = pr == null ? (Guid?)null : pr.Id,
					Season = season
				});
			}
			Console.WriteLine("Inventory   : " + invs.Count);
			Console.WriteLine();
		}

		// ---------------------------------------------------------------- census
		private static void Census(Dictionary<Guid, Coll> colls, List<Prod> prods, List<Inv> invs)
		{
			foreach (Prod p in prods)
				if (p.CollectionId.HasValue && colls.ContainsKey(p.CollectionId.Value))
					colls[p.CollectionId.Value].Products++;

			foreach (Inv i in invs)
			{
				if (!i.CollectionId.HasValue || !colls.ContainsKey(i.CollectionId.Value)) continue;
				Coll c = colls[i.CollectionId.Value];
				c.Inventory++;
				if (!c.BySeason.ContainsKey(i.Season)) c.BySeason[i.Season] = 0;
				c.BySeason[i.Season]++;
			}

			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine(" COLLECTION CENSUS");
			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine("{0,-38} {1,-6} {2,6} {3,6}  {4}", "Collection", "State", "Prod", "Inv", "By season");

			foreach (Coll c in colls.Values.OrderByDescending(x => x.Inventory).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
			{
				string seasons = string.Join(", ", c.BySeason.Select(kv => kv.Key + ":" + kv.Value));
				Console.WriteLine("{0,-38} {1,-6} {2,6} {3,6}  {4}",
					Trim(c.Name, 38), c.Active ? "on" : "OFF", c.Products, c.Inventory, seasons);
			}

			int noColl = prods.Count(p => !p.CollectionId.HasValue);
			int invNoColl = invs.Count(i => !i.CollectionId.HasValue);
			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine("Products with no collection  : " + noColl);
			Console.WriteLine("Inventory with no collection : " + invNoColl);
		}

		/// <summary>Names that differ only in case, punctuation, or "and" versus "&amp;". These are the
		/// consolidation candidates: the census above shows which of the pair holds the records.</summary>
		private static void LookAlikes(Dictionary<Guid, Coll> colls)
		{
			var fam = new Dictionary<string, List<Coll>>();
			foreach (Coll c in colls.Values)
			{
				string k = Fold(c.Name);
				if (k.Length == 0) continue;
				if (!fam.ContainsKey(k)) fam[k] = new List<Coll>();
				fam[k].Add(c);
			}

			var dupes = fam.Where(kv => kv.Value.Count > 1).ToList();
			Console.WriteLine("\n---------------------------------------------------------------------");
			Console.WriteLine(" LOOK-ALIKE NAMES (same name once case, spacing and \"and\"/& are ignored)");
			Console.WriteLine("---------------------------------------------------------------------");
			if (dupes.Count == 0) { Console.WriteLine("None."); return; }

			foreach (var kv in dupes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
			{
				Console.WriteLine("  " + kv.Key);
				foreach (Coll c in kv.Value.OrderByDescending(x => x.Products + x.Inventory))
					Console.WriteLine("      \"{0}\"  state={1}  products={2}  inventory={3}",
						c.Name, c.Active ? "on" : "OFF", c.Products, c.Inventory);
			}
			Console.WriteLine("\nOne of each pair is the survivor. Which one is a Storm decision, not ours -");
			Console.WriteLine("\"TICKETS & HOSPITALITY\" and \"TICKETS AND HOSPITALITY\" are not the same string.");
		}

		/// <summary>An inventory row whose collection does not match its product's collection cannot be
		/// fixed by stamping, because the workflow copies the product's value down. A row with no
		/// product cannot be fixed at all. Both are reported rather than guessed at.</summary>
		private static void Orphans(Dictionary<Guid, Coll> colls, List<Prod> prods, List<Inv> invs)
		{
			var byId = prods.ToDictionary(p => p.Id, p => p);
			var mismatched = new List<Inv>();
			var noProduct = new List<Inv>();

			foreach (Inv i in invs)
			{
				if (!i.ProductId.HasValue) { if (i.CollectionId.HasValue) noProduct.Add(i); continue; }
				if (!byId.ContainsKey(i.ProductId.Value)) continue;
				Guid? pc = byId[i.ProductId.Value].CollectionId;
				if (pc != i.CollectionId) mismatched.Add(i);
			}

			Console.WriteLine("\n---------------------------------------------------------------------");
			Console.WriteLine(" ROWS THE WORKFLOW CANNOT FIX");
			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine("Inventory whose collection differs from its product : " + mismatched.Count);
			foreach (Inv i in mismatched.Take(25))
				Console.WriteLine("    {0} [{1}]  inv={2}  product={3}",
					Trim(i.Name, 44), i.Season, CollName(colls, i.CollectionId),
					CollName(colls, byId[i.ProductId.Value].CollectionId));
			if (mismatched.Count > 25) Console.WriteLine("    ... and " + (mismatched.Count - 25) + " more, see the CSV.");

			Console.WriteLine("Inventory with a collection but no product          : " + noProduct.Count);
			foreach (Inv i in noProduct.Take(15))
				Console.WriteLine("    {0} [{1}]  {2}", Trim(i.Name, 44), i.Season, CollName(colls, i.CollectionId));

			if (mismatched.Count > 0 || noProduct.Count > 0)
			{
				var sb = new StringBuilder();
				sb.AppendLine("Issue,InventoryId,Inventory,Season,InventoryCollection,ProductCollection");
				foreach (Inv i in mismatched)
					sb.AppendLine(Csv("collection differs from product", i.Id.ToString(), i.Name, i.Season,
						CollName(colls, i.CollectionId), CollName(colls, byId[i.ProductId.Value].CollectionId)));
				foreach (Inv i in noProduct)
					sb.AppendLine(Csv("no product", i.Id.ToString(), i.Name, i.Season,
						CollName(colls, i.CollectionId), ""));
				string f = "Collections_Exceptions_" + EnvTag() + "_" + Stamp + ".csv";
				File.WriteAllText(f, sb.ToString(), Utf8NoBom);
				Console.WriteLine("  -> " + f);
			}
		}

		private static void WriteAuditCsv(Dictionary<Guid, Coll> colls)
		{
			var sb = new StringBuilder();
			sb.AppendLine("CollectionId,Collection,State,Products,Inventory,BySeason");
			foreach (Coll c in colls.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
				sb.AppendLine(Csv(c.Id.ToString(), c.Name, c.Active ? "Active" : "Inactive",
					c.Products.ToString(), c.Inventory.ToString(),
					string.Join(" | ", c.BySeason.Select(kv => kv.Key + ":" + kv.Value))));
			string f = "Collections_Audit_" + EnvTag() + "_" + Stamp + ".csv";
			File.WriteAllText(f, sb.ToString(), Utf8NoBom);
			Console.WriteLine("\n-> " + f);
		}

		// ---------------------------------------------------------------- apply
		private static void Apply(IOrganizationService svc, Dictionary<Guid, Coll> colls,
			List<Prod> prods, List<Inv> invs)
		{
			var byName = new Dictionary<string, List<Coll>>(StringComparer.OrdinalIgnoreCase);
			foreach (Coll c in colls.Values)
			{
				if (!byName.ContainsKey(c.Name)) byName[c.Name] = new List<Coll>();
				byName[c.Name].Add(c);
			}

			Console.WriteLine("\n=====================================================================");
			Console.WriteLine(" PLAN");
			Console.WriteLine("=====================================================================");

			// ---- renames -----------------------------------------------------
			var renames = new List<KeyValuePair<Coll, string>>();
			foreach (var kv in RENAMES)
			{
				Coll src = One(byName, kv.Key, "rename source");
				if (src == null) continue;

				// byName is case-insensitive, so "Other" -> "OTHER" finds the very record being
				// renamed. That is a re-capitalisation, not a collision. Only a DIFFERENT record
				// under the target name makes this a merge.
				if (byName.ContainsKey(kv.Value)
					&& !(byName[kv.Value].Count == 1 && byName[kv.Value][0].Id == src.Id))
				{
					Refuse("\"" + kv.Key + "\" -> \"" + kv.Value + "\": another collection already exists "
						+ "under the target name. That is a merge, move it to MERGES.");
					continue;
				}
				if (string.Equals(src.Name, kv.Value, StringComparison.Ordinal))
				{
					Console.WriteLine("SKIP    \"{0}\" is already spelled that way.", src.Name);
					continue;
				}
				renames.Add(new KeyValuePair<Coll, string>(src, kv.Value));
				Console.WriteLine("RENAME  \"{0}\" -> \"{1}\"   (products {2}, inventory {3} - nothing moves, the "
					+ "record keeps its id)", src.Name, kv.Value, src.Products, src.Inventory);
			}

			// ---- merges ------------------------------------------------------
			var prodById = prods.ToDictionary(p => p.Id, p => p);
			var invByProduct = new Dictionary<Guid, List<Inv>>();
			foreach (Inv i in invs)
			{
				if (!i.ProductId.HasValue) continue;
				if (!invByProduct.ContainsKey(i.ProductId.Value)) invByProduct[i.ProductId.Value] = new List<Inv>();
				invByProduct[i.ProductId.Value].Add(i);
			}

			var repoint = new List<KeyValuePair<Prod, Coll>>();   // product -> target collection
			var stamp = new HashSet<Guid>();                      // inventory rows to re-trigger
			var emptied = new List<Coll>();
			// A collection being merged away must never be the destination of a backfill, or the
			// orphans land on the very record this run is trying to empty.
			var redirect = new Dictionary<Guid, Guid>();
			var stuckByMerge = new List<KeyValuePair<Coll, List<Inv>>>();

			foreach (var kv in MERGES)
			{
				Coll src = One(byName, kv.Key, "merge source");
				Coll tgt = One(byName, kv.Value, "merge target");
				if (src == null || tgt == null) continue;
				if (src.Id == tgt.Id) { Refuse("\"" + kv.Key + "\" merges into itself."); continue; }
				if (!tgt.Active) { Refuse("target \"" + tgt.Name + "\" is deactivated. Reactivate it first."); continue; }

				redirect[src.Id] = tgt.Id;

				var moving = prods.Where(p => p.CollectionId == src.Id).ToList();
				var seasons = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				foreach (Prod p in moving)
				{
					repoint.Add(new KeyValuePair<Prod, Coll>(p, tgt));
					if (!invByProduct.ContainsKey(p.Id)) continue;
					foreach (Inv i in invByProduct[p.Id])
					{
						stamp.Add(i.Id);
						if (!seasons.ContainsKey(i.Season)) seasons[i.Season] = 0;
						seasons[i.Season]++;
					}
				}

				Console.WriteLine("MERGE   \"{0}\" -> \"{1}\"", src.Name, tgt.Name);
				Console.WriteLine("            products repointed : {0}", moving.Count);
				Console.WriteLine("            inventory stamped  : {0}  ({1})",
					seasons.Values.Sum(), seasons.Count == 0 ? "none" : string.Join(", ", seasons.Select(s => s.Key + ":" + s.Value)));

				// Inventory sitting on the source whose product is somewhere else. Stamping alone
				// will not move it, because the workflow copies the product's value down. Held
				// and reported AFTER the backfill, which rescues most of them.
				stuckByMerge.Add(new KeyValuePair<Coll, List<Inv>>(src,
					invs.Where(i => i.CollectionId == src.Id
						&& (!i.ProductId.HasValue || !prodById.ContainsKey(i.ProductId.Value)
							|| prodById[i.ProductId.Value].CollectionId != src.Id)).ToList()));

				if (DEACTIVATE_EMPTIED) emptied.Add(src);
			}

			// ---- collections that hold nothing at all --------------------------
			var deadEmpty = new List<Coll>();
			foreach (string name in DEACTIVATE)
			{
				Coll c = One(byName, name, "deactivate");
				if (c == null) continue;
				if (c.Products > 0 || c.Inventory > 0)
				{
					Refuse("\"" + c.Name + "\" is not empty (" + c.Products + " product(s), "
						+ c.Inventory + " inventory row(s)). Merge it instead of deactivating it.");
					continue;
				}
				if (!c.Active) continue;
				deadEmpty.Add(c);
			}
			if (deadEmpty.Count > 0)
				Console.WriteLine("DEACTIVATE (empty)  {0}", string.Join(", ", deadEmpty.Select(c => "\"" + c.Name + "\"")));

			// ---- products with no collection -----------------------------------
			// The value is not invented: it is read off that product's own inventory rows, which
			// is where it has been sitting all along. Normally no stamp is needed, because the
			// inventory already shows this collection and only the product was missing it. The
			// exception is a value being merged away: that one is redirected to the merge target,
			// which IS a change, so those inventory rows do get stamped.
			var backfill = new List<KeyValuePair<Prod, Guid>>();
			var conflicted = new List<Prod>();
			int redirected = 0;
			if (BACKFILL_PRODUCT_FROM_INVENTORY)
			{
				foreach (Prod p in prods.Where(x => !x.CollectionId.HasValue))
				{
					if (!invByProduct.ContainsKey(p.Id)) continue;
					var seen = invByProduct[p.Id].Where(i => i.CollectionId.HasValue)
						.Select(i => i.CollectionId.Value).Distinct().ToList();
					if (seen.Count == 0) continue;
					if (seen.Count > 1) { conflicted.Add(p); continue; }

					Guid want = seen[0];
					if (redirect.ContainsKey(want))
					{
						want = redirect[want];
						redirected++;
						foreach (Inv i in invByProduct[p.Id]) stamp.Add(i.Id);
					}
					backfill.Add(new KeyValuePair<Prod, Guid>(p, want));
				}
				Console.WriteLine("BACKFILL            {0} product(s) take the collection from their own inventory",
					backfill.Count);
				foreach (var g in backfill.GroupBy(kv => CollName(colls, kv.Value)).OrderByDescending(g2 => g2.Count()))
					Console.WriteLine("                        {0,-28} {1}", g.Key, g.Count());
				if (redirected > 0)
					Console.WriteLine("                    {0} of them pointed at a collection being merged away, so they "
						+ "go to its target and their inventory is stamped.", redirected);
				if (conflicted.Count > 0)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine("                    {0} product(s) whose inventory rows disagree on the "
						+ "collection. Left alone, listed in the log.", conflicted.Count);
					Console.ResetColor();
					foreach (Prod p in conflicted) Say("CONFLICT product " + p.Name + " [" + p.Id + "]");
				}
			}

			// ---- what is still stuck once the backfill has run -------------------
			var rescued = new HashSet<Guid>(backfill.Select(kv => kv.Key.Id));
			foreach (var sm in stuckByMerge)
			{
				var left = sm.Value.Where(i => !i.ProductId.HasValue || !rescued.Contains(i.ProductId.Value)).ToList();
				foreach (Inv i in sm.Value)
					Say((left.Contains(i) ? "STUCK " : "rescued by backfill ") + i.Name + " [" + i.Season + "] " + i.Id);
				if (left.Count == 0) continue;
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("STILL STUCK         {0} inventory row(s) on \"{1}\" whose product the backfill does "
					+ "not reach. Listed in the log.", left.Count, sm.Key.Name);
				Console.ResetColor();
			}

			if (renames.Count == 0 && repoint.Count == 0 && deadEmpty.Count == 0 && backfill.Count == 0)
			{
				Console.WriteLine("\nNothing to do. Fill RENAMES / MERGES / DEACTIVATE from the audit above.");
				return;
			}

			Console.WriteLine("\nTotals: {0} rename(s), {1} product(s) repointed, {2} inventory row(s) stamped, "
				+ "{3} product(s) backfilled, {4} collection(s) deactivated.",
				renames.Count, repoint.Count, stamp.Count, backfill.Count, emptied.Count + deadEmpty.Count);

			if (DRY_RUN) { Console.WriteLine("\nDRY RUN - nothing was written."); return; }

			Console.Write("\nThis writes to " + EnvTag() + ". Type APPLY to proceed: ");
			if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "APPLY")
			{ Console.WriteLine("Cancelled, nothing written."); return; }

			var toDeactivate = emptied.Concat(deadEmpty).ToList();
			Backup(colls, repoint, renames, toDeactivate, backfill);

			// 1) renames
			int okR = 0;
			foreach (var kv in renames)
			{
				try
				{
					svc.Update(new Entity(COLL_ENTITY, kv.Key.Id) { [NAME] = kv.Value });
					okR++;
					Say("RENAMED " + kv.Key.Name + " -> " + kv.Value + " [" + kv.Key.Id + "]");
				}
				catch (Exception ex) { Say("ERROR rename " + kv.Key.Name + ": " + ex.Message); }
			}
			Console.WriteLine("Collections renamed : " + okR + " of " + renames.Count);

			// 2) products. A product that fails must not have its inventory stamped, or the
			//    workflow copies the OLD collection back down and the run looks successful.
			int okP = 0;
			var failedProducts = new HashSet<Guid>();
			foreach (var kv in repoint)
			{
				try
				{
					svc.Update(new Entity(PROD_ENTITY, kv.Key.Id) { [PROD_COLLECTION] = new EntityReference(COLL_ENTITY, kv.Value.Id) });
					okP++;
				}
				catch (Exception ex)
				{
					failedProducts.Add(kv.Key.Id);
					Say("ERROR product " + kv.Key.Name + " [" + kv.Key.Id + "]: " + ex.Message);
				}
				if ((okP + failedProducts.Count) % 25 == 0) Console.Write(".");
			}
			Console.WriteLine("\nProducts repointed  : " + okP + " ok, " + failedProducts.Count + " failed");

			// 3) products that had no collection at all. Written BEFORE the stamping, because the
			//    workflow's own condition is "Product Id contains data AND Product.Collection
			//    contains data". Stamp a row whose product is still blank and the step simply does
			//    not run.
			if (backfill.Count > 0)
			{
				int okB = 0, failB = 0;
				foreach (var kv in backfill)
				{
					try
					{
						svc.Update(new Entity(PROD_ENTITY, kv.Key.Id) { [PROD_COLLECTION] = new EntityReference(COLL_ENTITY, kv.Value) });
						okB++;
					}
					catch (Exception ex)
					{
						failB++;
						failedProducts.Add(kv.Key.Id);
						Say("ERROR backfill " + kv.Key.Name + " [" + kv.Key.Id + "]: " + ex.Message);
					}
					if ((okB + failB) % 25 == 0) Console.Write(".");
				}
				Console.WriteLine("\nProducts backfilled : " + okB + " ok, " + failB + " failed");
			}

			if (failedProducts.Count > 0)
			{
				int held = 0;
				foreach (Inv i in invs)
					if (i.ProductId.HasValue && failedProducts.Contains(i.ProductId.Value) && stamp.Remove(i.Id)) held++;
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("  " + held + " inventory row(s) NOT stamped because their product failed.");
				Console.ResetColor();
			}

			// 4) stamp the inventory so the workflow copies the new collection down
			int okI = 0, failI = 0;
			foreach (Guid id in stamp)
			{
				try { svc.Update(new Entity(INV_ENTITY, id) { [INV_STAMP] = DateTime.UtcNow }); okI++; }
				catch (Exception ex) { failI++; Say("ERROR inventory " + id + ": " + ex.Message); }
				if ((okI + failI) % 50 == 0) Console.Write(".");
			}
			Console.WriteLine("\nInventory stamped   : " + okI + " ok, " + failI + " failed");

			// 5) deactivate: the empty ones, plus any merge source that is genuinely empty now
			if (toDeactivate.Count > 0)
			{
				foreach (Coll c in toDeactivate)
				{
					int left = CountProducts(svc, c.Id);
					if (left > 0)
					{
						Console.WriteLine("  \"" + c.Name + "\" still has " + left + " product(s). Left active.");
						Say("NOT DEACTIVATED " + c.Name + " - " + left + " products remain");
						continue;
					}
					try
					{
						svc.Execute(new SetStateRequest
						{
							EntityMoniker = new EntityReference(COLL_ENTITY, c.Id),
							State = new OptionSetValue(1),
							Status = new OptionSetValue(2)
						});
						Console.WriteLine("  deactivated \"" + c.Name + "\"");
						Say("DEACTIVATED " + c.Name + " [" + c.Id + "]");
					}
					catch (Exception ex)
					{
						Console.WriteLine("  could not deactivate \"" + c.Name + "\": " + ex.Message);
						Say("ERROR deactivate " + c.Name + ": " + ex.Message);
					}
				}
			}

			Console.WriteLine("\nRe-run with AUDIT_ONLY = true to confirm the census is what you expect.");
		}

		/// <summary>The collection is copied down by a workflow, so the reversal that matters is the
		/// product's old collection. Renames and deactivations are recorded in the same file.</summary>
		private static void Backup(Dictionary<Guid, Coll> colls, List<KeyValuePair<Prod, Coll>> repoint,
			List<KeyValuePair<Coll, string>> renames, List<Coll> emptied, List<KeyValuePair<Prod, Guid>> backfill)
		{
			var sb = new StringBuilder();
			sb.AppendLine("Kind,Id,Name,OldValue,NewValue");
			foreach (var kv in repoint)
				sb.AppendLine(Csv("product", kv.Key.Id.ToString(), kv.Key.Name,
					CollName(colls, kv.Key.CollectionId), kv.Value.Name));
			foreach (var kv in backfill)
				sb.AppendLine(Csv("product backfill", kv.Key.Id.ToString(), kv.Key.Name,
					"(none)", CollName(colls, kv.Value)));
			foreach (var kv in renames)
				sb.AppendLine(Csv("collection rename", kv.Key.Id.ToString(), kv.Key.Name, kv.Key.Name, kv.Value));
			foreach (Coll c in emptied)
				sb.AppendLine(Csv("collection deactivate", c.Id.ToString(), c.Name, "Active", "Inactive"));
			string f = "Collections_BACKUP_" + EnvTag() + "_" + Stamp + ".csv";
			File.WriteAllText(f, sb.ToString(), Utf8NoBom);
			Console.WriteLine("\nBackup -> " + f);
		}

		private static int CountProducts(IOrganizationService svc, Guid collectionId)
		{
			var q = new QueryExpression(PROD_ENTITY) { ColumnSet = new ColumnSet(false) };
			q.Criteria.AddCondition(PROD_COLLECTION, ConditionOperator.Equal, collectionId);
			return svc.RetrieveMultiple(q).Entities.Count;
		}

		// ---------------------------------------------------------------- helpers
		private static Coll One(Dictionary<string, List<Coll>> byName, string name, string what)
		{
			if (!byName.ContainsKey(name))
			{
				Refuse("no collection is named \"" + name + "\" (" + what + "). Copy the name exactly from the audit.");
				return null;
			}
			List<Coll> hits = byName[name];
			if (hits.Count > 1)
			{
				Refuse("\"" + name + "\" matches " + hits.Count + " collection records (" + what
					+ "). Resolve the duplicate by hand first.");
				return null;
			}
			return hits[0];
		}

		private static void Refuse(string msg)
		{
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("REFUSED " + msg);
			Console.ResetColor();
			Say("REFUSED " + msg);
		}

		private static string CollName(Dictionary<Guid, Coll> colls, Guid? id)
		{
			if (!id.HasValue) return "(none)";
			return colls.ContainsKey(id.Value) ? colls[id.Value].Name : id.Value.ToString();
		}

		/// <summary>Uppercase, "and" read as "&amp;", and everything that is not a letter or a digit
		/// dropped. It is only used to spot look-alikes, never to decide that two records are
		/// the same thing.</summary>
		private static string Fold(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			string t = " " + s.ToUpperInvariant().Trim() + " ";
			t = t.Replace(" AND ", " & ");
			var sb = new StringBuilder();
			foreach (char ch in t) if (char.IsLetterOrDigit(ch)) sb.Append(ch);
			return sb.ToString();
		}

		private static string Trim(string s, int n)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Length <= n ? s : s.Substring(0, n - 1) + "~";
		}

		private static string Csv(params string[] cells)
		{
			return string.Join(",", cells.Select(c =>
			{
				string v = c ?? "";
				return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
			}));
		}

		private static IEnumerable<Entity> Page(IOrganizationService svc, string entity, ColumnSet cols, FilterExpression filter)
		{
			var q = new QueryExpression(entity)
			{
				ColumnSet = cols,
				PageInfo = new PagingInfo { Count = 500, PageNumber = 1 }
			};
			if (filter != null) q.Criteria = filter;

			while (true)
			{
				EntityCollection page = svc.RetrieveMultiple(q);
				foreach (Entity e in page.Entities) yield return e;
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}
		}

		private static void Say(string line) { Log.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line); }

		private static void WriteLog()
		{
			if (Log.Count == 0) return;
			string f = "Collections_Log_" + EnvTag() + "_" + Stamp + ".txt";
			File.WriteAllLines(f, Log, Utf8NoBom);
			Console.WriteLine("Log -> " + f);
		}
	}
}
