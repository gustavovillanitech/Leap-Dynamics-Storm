using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PackageRateCardAudit
{
	/// <summary>
	/// Read-only audit. Writes nothing, ever. There is no live mode to turn on.
	///
	/// Answers three questions that were being guessed at:
	///
	///  1. WHICH SEASONS EXIST and how much inventory each one carries. Sandbox shows
	///     2029 and 2030; this says whether production does too.
	///
	///  2. WHY A RATE CARD LINE HAS NO PRICE. Ray, 2026-09-28: Storm look at a season's
	///     inventory, see items at zero dollars or blank, and suspect those are priced
	///     that way because they are only sold inside a package. 110 of the 283 items on
	///     the 2026 sheet have no rate, so the suspicion covers a lot of ground. This
	///     counts how many of them are genuinely package components, which decides
	///     whether an "in N packages" column answers the question or only a corner of it.
	///
	///  3. PACKAGES THAT SILENTLY COME OUT SHORT. DealLineBuilder_Package_PowerFx.txt,
	///     2026-09-11: "a package whose component has been deactivated now explodes
	///     WITHOUT that component, silently". A component is missing whenever its product
	///     has no ACTIVE inventory row in the season being sold. Nobody can see this
	///     today, which is the same blind spot as question 2 seen from the other side.
	///
	/// The package relationship only runs one way in the app: the Deal Line Builder goes
	/// package -> components. Every question above needs the inverse, component -> packages,
	/// which is why it is built here rather than read off a screen.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION
		// =====================================================================

		// PRODUCTION. This tool only reads, so this is safe, but change it deliberately.
		private const string ENV_URL = "https://stormbasketball.crm.dynamics.com/";

		/// <summary>The season the rate card questions are asked about.</summary>
		private const string TARGET_SEASON = "2026 - Storm";

		/// <summary>How many rows of each list to print before deferring to the CSV.</summary>
		private const int PRINT_LIMIT = 25;

		private const string SEASON_ENTITY = "new_season";
		private const string INV_ENTITY = "new_inventory";
		private const string PROD_ENTITY = "new_product";
		private const string PKGCOMP_ENTITY = "new_packagecomponent";

		private const string NAME = "new_name";
		private const string INV_RATE = "new_rate";
		private const string INV_SEASON = "new_seasonid";
		private const string INV_PRODUCT = "new_productid";
		private const string INV_COLLECTION = "new_collection";
		private const string INV_QUANTITY = "new_quantity";
		private const string PROD_ISPACKAGE = "new_ispackage";
		private const string PKG_PACKAGEPRODUCT = "new_packageproduct";
		private const string PKG_COMPONENTPRODUCT = "new_componentproduct";

		// Dynamics rollups and the associated view only see ACTIVE link rows.
		// Set to false to count every row, active or not.
		private const bool EXCLUDE_INACTIVE_LINKS = true;
		// =====================================================================

		private static bool IsProd { get { return ENV_URL.IndexOf("stormbasketball", StringComparison.OrdinalIgnoreCase) >= 0; } }
		private static string EnvTag() { return IsProd ? "PROD" : "SANDBOX"; }
		private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
		private static readonly string Stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

		private sealed class Inv
		{
			public Guid Id;
			public string Name;
			public Guid? ProductId;
			public Guid? SeasonId;
			public string Season;
			public decimal? Rate;
			public string Collection;
			public decimal? Quantity;
			public int State;
			public bool Active { get { return State == 0; } }
			public bool HasRate { get { return Rate.HasValue && Rate.Value != 0m; } }
			/// <summary>Ray asked whether writing an explicit $0 would resolve anything. It only
			/// means something if the two states are actually different in the data, so they are
			/// counted apart: "blank" is nobody filled it in, "$0" is somebody typed a zero.</summary>
			public string RateState { get { return !Rate.HasValue ? "blank" : (Rate.Value == 0m ? "$0" : "priced"); } }
		}

		private sealed class Prod
		{
			public Guid Id;
			public string Name;
			public bool IsPackage;
		}

		private static void Main()
		{
			Console.Title = "Package / Rate Card Audit - " + EnvTag();
			Console.WriteLine("=====================================================");
			Console.WriteLine(" PACKAGE / RATE CARD AUDIT   (read only, writes nothing)");
			Console.WriteLine("=====================================================");
			Console.WriteLine("Environment   : " + ENV_URL + "   [" + EnvTag() + "]");
			Console.WriteLine("Target season : " + TARGET_SEASON);

			Console.Write("\nType YES to continue: ");
			if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "YES")
			{ Console.WriteLine("\nCancelled."); Console.ReadLine(); return; }

			string cs = "AuthType=OAuth;Url=" + ENV_URL
				+ ";AppId=51f81489-12ee-4a9e-aaae-a2591f45987d"
				+ ";RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

			Console.WriteLine("\nConnecting...");
			CrmServiceClient service = new CrmServiceClient(cs);
			if (!service.IsReady)
			{ Console.WriteLine("Connection error: " + service.LastCrmError); Console.ReadLine(); return; }
			Console.WriteLine("Connected.\n");

			try { Run(service); }
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\nFATAL: " + ex.Message);
				Console.ResetColor();
			}

			Console.WriteLine("\nDone. Press Enter to exit...");
			Console.ReadLine();
		}

		private static void Run(IOrganizationService svc)
		{
			// ---- load ---------------------------------------------------------
			var seasonNames = new Dictionary<Guid, string>();
			var seasonState = new Dictionary<Guid, int>();
			foreach (Entity e in Page(svc, SEASON_ENTITY, new ColumnSet(true)))
			{
				seasonNames[e.Id] = e.GetAttributeValue<string>(NAME) ?? "";
				seasonState[e.Id] = e.Contains("statecode") ? e.GetAttributeValue<OptionSetValue>("statecode").Value : 0;
			}

			var prods = new Dictionary<Guid, Prod>();
			foreach (Entity e in Page(svc, PROD_ENTITY, new ColumnSet(NAME, PROD_ISPACKAGE)))
				prods[e.Id] = new Prod
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>(NAME) ?? "",
					IsPackage = e.GetAttributeValue<bool>(PROD_ISPACKAGE)
				};

			var invs = new List<Inv>();
			foreach (Entity e in Page(svc, INV_ENTITY, new ColumnSet(NAME, INV_RATE, INV_SEASON, INV_PRODUCT, INV_COLLECTION, INV_QUANTITY, "statecode")))
			{
				var pr = e.GetAttributeValue<EntityReference>(INV_PRODUCT);
				var sr = e.GetAttributeValue<EntityReference>(INV_SEASON);
				Money rate = e.GetAttributeValue<Money>(INV_RATE);
				invs.Add(new Inv
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>(NAME) ?? "",
					ProductId = pr == null ? (Guid?)null : pr.Id,
					SeasonId = sr == null ? (Guid?)null : sr.Id,
					Season = sr == null ? "(no season)" : (seasonNames.ContainsKey(sr.Id) ? seasonNames[sr.Id] : sr.Id.ToString()),
					Rate = rate == null ? (decimal?)null : rate.Value,
					Collection = e.GetAttributeValue<EntityReference>(INV_COLLECTION) == null
						? "" : e.GetAttributeValue<EntityReference>(INV_COLLECTION).Name,
					Quantity = ReadNumber(e, INV_QUANTITY),
					State = e.Contains("statecode") ? e.GetAttributeValue<OptionSetValue>("statecode").Value : 0
				});
			}

			// component product -> the packages that contain it   (the inverse lookup)
			var packagesOf = new Dictionary<Guid, List<Guid>>();
			// package product -> its component products
			var componentsOf = new Dictionary<Guid, List<Guid>>();
			int orphanLinks = 0;
			int inactiveLinks = 0;
			int totalLinkRows = 0;
			var dupPairs = new HashSet<string>();
			int duplicateRows = 0;
			foreach (Entity e in Page(svc, PKGCOMP_ENTITY, new ColumnSet(NAME, PKG_PACKAGEPRODUCT, PKG_COMPONENTPRODUCT, "statecode")))
			{
				totalLinkRows++;
				var pkg = e.GetAttributeValue<EntityReference>(PKG_PACKAGEPRODUCT);
				var cmp = e.GetAttributeValue<EntityReference>(PKG_COMPONENTPRODUCT);
				int linkState = e.Contains("statecode") && e["statecode"] != null
					? e.GetAttributeValue<OptionSetValue>("statecode").Value : 0;
				if (linkState != 0) inactiveLinks++;
				if (pkg != null && cmp != null)
				{
					string pair = pkg.Id.ToString() + "|" + cmp.Id.ToString();
					if (!dupPairs.Add(pair)) duplicateRows++;
				}
				if (pkg == null || cmp == null) { orphanLinks++; continue; }
				if (EXCLUDE_INACTIVE_LINKS && linkState != 0) continue;
				if (!packagesOf.ContainsKey(cmp.Id)) packagesOf[cmp.Id] = new List<Guid>();
				if (!packagesOf[cmp.Id].Contains(pkg.Id)) packagesOf[cmp.Id].Add(pkg.Id);
				if (!componentsOf.ContainsKey(pkg.Id)) componentsOf[pkg.Id] = new List<Guid>();
				if (!componentsOf[pkg.Id].Contains(cmp.Id)) componentsOf[pkg.Id].Add(cmp.Id);
			}

			Console.WriteLine("Seasons            : " + seasonNames.Count);
			Console.WriteLine("Products           : " + prods.Count + "   (packages: " + prods.Values.Count(p => p.IsPackage) + ")");
			Console.WriteLine("Inventory rows     : " + invs.Count);
			Console.WriteLine("Package components : " + packagesOf.Values.Sum(l => l.Count)
				+ (orphanLinks > 0 ? "   (" + orphanLinks + " link rows missing a lookup, ignored)" : ""));
			Console.WriteLine("Link rows (raw)    : " + totalLinkRows
				+ "   inactive: " + inactiveLinks
				+ "   duplicate package+component pairs: " + duplicateRows
				+ "   inactive excluded from counts: " + (EXCLUDE_INACTIVE_LINKS ? "YES" : "NO"));

			// ---- 1. seasons ---------------------------------------------------
			Console.WriteLine("\n---------------------------------------------------------------------");
			Console.WriteLine(" 1. SEASONS AND THEIR INVENTORY   (does this environment have 2029/2030?)");
			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine("{0,-22} {1,-8} {2,8} {3,8} {4,10}", "Season", "State", "Active", "Inactive", "Priced");

			foreach (var g in invs.GroupBy(i => i.Season).OrderBy(g2 => g2.Key, StringComparer.OrdinalIgnoreCase))
			{
				var sid = g.First().SeasonId;
				string st = sid.HasValue && seasonState.ContainsKey(sid.Value)
					? (seasonState[sid.Value] == 0 ? "on" : "OFF") : "?";
				Console.WriteLine("{0,-22} {1,-8} {2,8} {3,8} {4,10}",
					Trim(g.Key, 22), st, g.Count(i => i.Active), g.Count(i => !i.Active), g.Count(i => i.HasRate));
			}
			var emptySeasons = seasonNames.Where(kv => !invs.Any(i => i.SeasonId == kv.Key)).Select(kv => kv.Value).ToList();
			if (emptySeasons.Count > 0)
				Console.WriteLine("\nSeasons with NO inventory at all: " + string.Join(", ", emptySeasons));

			// ---- 2. the zero-rate question ------------------------------------
			var target = invs.Where(i => string.Equals(i.Season, TARGET_SEASON, StringComparison.OrdinalIgnoreCase)
										 && i.Active).ToList();

			Console.WriteLine("\n---------------------------------------------------------------------");
			Console.WriteLine(" 2. WHY DOES THIS LINE HAVE NO PRICE?   [" + TARGET_SEASON + ", active only]");
			Console.WriteLine("---------------------------------------------------------------------");

			var noRate = target.Where(i => !i.HasRate).ToList();
			Console.WriteLine("Items on the rate card       : " + target.Count);
			Console.WriteLine("  with a rate                : " + (target.Count - noRate.Count));
			Console.WriteLine("  with NO rate               : " + noRate.Count);
			Console.WriteLine("      of which blank         : " + noRate.Count(i => i.RateState == "blank"));
			Console.WriteLine("      of which an explicit $0: " + noRate.Count(i => i.RateState == "$0"));

			int inPkg = 0, notInPkg = 0, noProduct = 0, isPkgItself = 0;
			var rows = new List<string[]>();
			foreach (Inv i in noRate)
			{
				if (!i.ProductId.HasValue)
				{
					noProduct++;
					rows.Add(new[] { i.Id.ToString(), i.Name, i.Collection, i.RateState,
									 i.Quantity.HasValue ? i.Quantity.Value.ToString() : "", "(no product)", "0", "" });
					continue;
				}
				Guid pid = i.ProductId.Value;
				bool itemIsPackage = prods.ContainsKey(pid) && prods[pid].IsPackage;
				var pkgs = packagesOf.ContainsKey(pid) ? packagesOf[pid] : new List<Guid>();
				if (itemIsPackage) isPkgItself++;
				if (pkgs.Count > 0) inPkg++; else if (!itemIsPackage) notInPkg++;
				rows.Add(new[]
				{
					i.Id.ToString(),
					i.Name,
					i.Collection,
					i.RateState,
					Qty(i),
					itemIsPackage ? "IS a package" : (pkgs.Count > 0 ? "component" : "not in any package"),
					pkgs.Count.ToString(),
					string.Join(" | ", pkgs.Select(p => prods.ContainsKey(p) ? prods[p].Name : p.ToString()))
				});
			}

			Console.WriteLine("\nOf the " + noRate.Count + " with no rate:");
			Console.WriteLine("  component of at least one package : " + inPkg);
			Console.WriteLine("  is itself a package               : " + isPkgItself);
			Console.WriteLine("  in NO package at all              : " + notInPkg);
			if (noProduct > 0) Console.WriteLine("  no product on the inventory row   : " + noProduct);

			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("\n  -> An \"in N packages\" column would explain " + inPkg + " of " + noRate.Count
				+ " zero-rate items. The other " + (noRate.Count - inPkg) + " stay unexplained.");
			Console.ResetColor();

			Console.WriteLine("\nZero-rate items that ARE package components (first " + PRINT_LIMIT + "):");
			foreach (string[] r in rows.Where(r2 => r2[5] == "component").Take(PRINT_LIMIT))
				Console.WriteLine("    {0,-42} {1,-6} {2} pkg  {3}", Trim(r[1], 42), r[3], r[6], Trim(r[7], 44));

			WriteCsv("ZeroRate_" + EnvTag() + "_" + Stamp + ".csv",
				"InventoryId,Item,Collection,RateState,Quantity,Classification,PackageCount,Packages", rows);

			// ---- 3. packages that come out short -------------------------------
			Console.WriteLine("\n---------------------------------------------------------------------");
			Console.WriteLine(" 3. PACKAGES THAT EXPLODE SHORT   [" + TARGET_SEASON + "]");
			Console.WriteLine("---------------------------------------------------------------------");
			Console.WriteLine("A component is missing when its product has no ACTIVE inventory row in");
			Console.WriteLine("this season. The builder drops it silently and the seller is not told.\n");

			// product -> does it have an active inventory row in the target season
			var activeInSeason = new HashSet<Guid>(target.Where(i => i.ProductId.HasValue).Select(i => i.ProductId.Value));

			// The inventory row carries its own name, which is what anyone searching the
			// Inventories list will type. Reporting only the product name makes this tab
			// impossible to look up, so both are printed.
			var invNameInSeason = new Dictionary<Guid, string>();
			foreach (Inv i in target.Where(x => x.ProductId.HasValue))
				if (!invNameInSeason.ContainsKey(i.ProductId.Value)) invNameInSeason[i.ProductId.Value] = i.Name;

			// Why a component is missing decides the fix: reactivate one row, or create it.
			var anyRowInSeason = new Dictionary<Guid, Inv>();
			foreach (Inv i in invs.Where(x => x.ProductId.HasValue
					&& string.Equals(x.Season, TARGET_SEASON, StringComparison.OrdinalIgnoreCase)))
				if (!anyRowInSeason.ContainsKey(i.ProductId.Value)) anyRowInSeason[i.ProductId.Value] = i;
			var seasonsWithRow = new Dictionary<Guid, List<string>>();
			foreach (Inv i in invs.Where(x => x.ProductId.HasValue && x.Active))
			{
				if (!seasonsWithRow.ContainsKey(i.ProductId.Value)) seasonsWithRow[i.ProductId.Value] = new List<string>();
				if (!seasonsWithRow[i.ProductId.Value].Contains(i.Season)) seasonsWithRow[i.ProductId.Value].Add(i.Season);
			}

			var shortRows = new List<string[]>();
			int shortPkgs = 0;
			foreach (var kv in componentsOf.OrderBy(k => prods.ContainsKey(k.Key) ? prods[k.Key].Name : "", StringComparer.OrdinalIgnoreCase))
			{
				string pkgProduct = prods.ContainsKey(kv.Key) ? prods[kv.Key].Name : kv.Key.ToString();
				// only report packages that are actually sellable this season
				if (!activeInSeason.Contains(kv.Key)) continue;
				string pkgInvName = invNameInSeason.ContainsKey(kv.Key) ? invNameInSeason[kv.Key] : "";

				var missing = kv.Value.Where(c => !activeInSeason.Contains(c)).ToList();
				if (missing.Count == 0) continue;
				shortPkgs++;
				foreach (Guid c in missing)
				{
					string why, fix;
					if (anyRowInSeason.ContainsKey(c))
					{
						why = "the " + TARGET_SEASON + " row exists but is INACTIVE";
						fix = "reactivate that inventory row";
					}
					else
					{
						var elsewhere = seasonsWithRow.ContainsKey(c) ? seasonsWithRow[c] : new List<string>();
						if (elsewhere.Count > 0)
						{
							why = "no " + TARGET_SEASON + " row at all, active in " + string.Join(" / ", elsewhere.OrderBy(x => x));
							fix = "create the missing season row";
						}
						else
						{
							why = "no inventory row in any season";
							fix = "the component product should probably not be on this package";
						}
					}

					shortRows.Add(new[]
					{
						pkgInvName.Length > 0 ? pkgInvName : pkgProduct,
						pkgProduct,
						prods.ContainsKey(c) ? prods[c].Name : c.ToString(),
						why,
						fix,
						kv.Value.Count.ToString()
					});
				}
			}

			if (shortPkgs == 0)
			{
				Console.WriteLine("None. Every component of every sellable package has an active inventory row.");
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine(shortPkgs + " package(s) would explode short, " + shortRows.Count + " missing component(s) in total.");
				Console.ResetColor();
				foreach (var g in shortRows.GroupBy(r => r[2] + " || " + r[3] + " || " + r[4])
										   .OrderByDescending(g2 => g2.Count()))
				{
					string[] first = g.First();
					Console.WriteLine("  MISSING: {0}", first[2]);
					Console.WriteLine("     why : {0}", first[3]);
					Console.WriteLine("     fix : {0}", first[4]);
					Console.WriteLine("     hits: {0} package(s), e.g. {1}", g.Count(), Trim(first[0], 52));
					Console.WriteLine();
				}
				WriteCsv("PackagesShort_" + EnvTag() + "_" + Stamp + ".csv",
					"PackageInventoryName,PackageProductName,MissingComponent,Why,Fix,TotalComponents", shortRows);
			}

			// ---- 4. the full inverse map, for whoever builds the column --------
			var mapRows = new List<string[]>();
			foreach (Inv i in target.Where(x => x.ProductId.HasValue))
			{
				var pkgs = packagesOf.ContainsKey(i.ProductId.Value) ? packagesOf[i.ProductId.Value] : new List<Guid>();
				if (pkgs.Count == 0) continue;
				mapRows.Add(new[]
				{
					i.Name,
					i.HasRate ? i.Rate.Value.ToString("0.00", CultureInfo.InvariantCulture) : "",
					pkgs.Count.ToString(),
					string.Join(" | ", pkgs.Select(p => prods.ContainsKey(p) ? prods[p].Name : p.ToString()))
				});
			}
			Console.WriteLine("\nItems in this season that belong to at least one package: " + mapRows.Count);
			WriteCsv("ItemsInPackages_" + EnvTag() + "_" + Stamp + ".csv",
				"Item,Rate,PackageCount,Packages", mapRows);
		}

		// ---------------------------------------------------------------- helpers
		private static void WriteCsv(string file, string header, List<string[]> rows)
		{
			var sb = new StringBuilder();
			sb.AppendLine(header);
			foreach (string[] r in rows) sb.AppendLine(Csv(r));
			File.WriteAllText(file, sb.ToString(), Utf8NoBom);
			Console.WriteLine("  -> " + file + "   (" + rows.Count + " row(s))");
		}

		/// <summary>Numeric Dataverse fields are not all the same CLR type: new_quantity is a
		/// decimal on inventory even though it reads as a whole number, so asking for an int
		/// throws. Read the raw value and convert, rather than guessing the type per field.</summary>
		private static decimal? ReadNumber(Entity e, string field)
		{
			if (!e.Contains(field) || e[field] == null) return null;
			object v = e[field];
			if (v is Money) return ((Money)v).Value;
			try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
			catch { return null; }
		}

		private static string Qty(Inv i)
		{
			return i.Quantity.HasValue ? i.Quantity.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
		}

		private static string Csv(params string[] cells)
		{
			return string.Join(",", cells.Select(c =>
			{
				string v = c ?? "";
				return v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
			}));
		}

		private static string Trim(string s, int n)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Length <= n ? s : s.Substring(0, n - 1) + "~";
		}

		private static IEnumerable<Entity> Page(IOrganizationService svc, string entity, ColumnSet cols)
		{
			var q = new QueryExpression(entity)
			{
				ColumnSet = cols,
				PageInfo = new PagingInfo { Count = 500, PageNumber = 1 }
			};
			while (true)
			{
				EntityCollection page = svc.RetrieveMultiple(q);
				foreach (Entity e in page.Entities) yield return e;
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}
		}
	}
}
