using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LoadClosedLostGrids
{
	/// <summary>
	/// Loads the 2026 closed-lost pricing grids into Dynamics as Opportunity + Deal + Deal Lines.
	///
	/// Source: the grids Storm keeps in SharePoint under Closed Lost Pricing Grids (NS and Tiffany
	/// folders). They are fifteen spreadsheets that only look alike, so they are NOT read here.
	/// They are flattened into one tab-separated file first, and this console reads that. The
	/// input is therefore auditable and diffable, and a re-run after Storm corrects a mapping
	/// touches only what changed.
	///
	/// Ray's instruction, 2026-09-22: do not spend hours guessing which inventory item a
	/// deliverable means. So a line whose inventory item is blank or unresolvable is REPORTED,
	/// never guessed at, and the deal is still created with the rest.
	///
	/// Safe to re-run: a client that already has a 2026 deal is skipped, and within a deal an
	/// existing line for the same inventory item is skipped rather than duplicated.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// PRODUCTION. Change deliberately, never by accident.
		private const string ENV_URL = "https://stormbasketball.crm.dynamics.com/";

		// true  = report what would happen and write nothing.
		// false = actually create the records.
		private const bool DRY_RUN = false;

		// Flattened grids. One row per deliverable. Produced from the mapping workbook.
		private const string INPUT_TSV = @"C:\Customer Docs\Storm\Deal Options And PlayOff Automation\Update Opps\ClosedLostGrids_Input.tsv";

		private const string SEASON_NAME = "2026 - Storm";

		// Clients whose records already exist in Dynamics under some other status. Creating a
		// second record for them is a decision for Ray, not for this console, so they stay out
		// until he answers. Remove a name from this list once he does.
		// Nothing is on hold any more. Tiffany confirmed on 2026-09-23 that the grids should
		// attach to the existing records, get their deal lines, and then be marked Closed Lost,
		// and on 2026-09-24 she said Bonneville is Bonneville International. Kept as an empty
		// dictionary rather than deleted, because the next batch of grids will need it again.
		// Empty again. Tiffany cleared the last three on 2026-09-25: their deals may be moved to
		// the 2026 - Storm season. Kept rather than deleted, the next batch of grids will need it.
		private static readonly Dictionary<string, string> SKIP_CLIENTS = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
		};

		// Upsells to an existing partner. Ray settled this on 2026-09-24 for BECU and framed it as
		// the general pattern: the won deal is left alone and the upsell goes in as a deal of its
		// own on the same account and season, marked Closed Lost.
		//
		// That breaks the usual "one deal per account per season" assumption on purpose, so these
		// cases are named here and nowhere else. For a client on this list the console finds and
		// tops up the deal WITH THIS EXACT NAME, instead of whatever single deal the account has
		// in the season. The name is one WE set and control - this is not the same thing as
		// identifying a deal by a name SetName generated, which is never unique (see the Starbucks
		// playoff pair).
		private static readonly Dictionary<string, string> ADDITIONAL_DEALS = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "BECU Upsell", "BECU - 2026 - Storm Upsell" }
		};

		// The grid's client name is not always the account name.
		private static readonly Dictionary<string, string> ACCOUNT_OVERRIDES = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
		{
			{ "BECU Upsell", "BECU" },
			// Two accounts nearly match this grid. Tiffany confirmed which: the one whose contact
			// is Rachelle Severns.
			{ "Bonneville", "Bonneville International" },
			// Two Kia accounts exist. Tiffany: "Please use Kia Motors America for mine", which is
			// also the account her existing deal hangs from.
			{ "KIA", "Kia Motors America, Inc." }
		};

		// An account that does not exist is reported, not invented - that is how duplicate accounts
		// get made from spelling variants. Turned on for this run only after Gustavo searched
		// Accounts by hand on 2026-09-23: A Advanced Services, GM Envolve and Thumbtack have no
		// near-name match of any kind, so they really are new. Bonneville did, and is held above.
		private const bool CREATE_MISSING_ACCOUNTS = true;

		// The opportunity's own state. The sales stage is always set; flipping the record to the
		// system-level Lost state is a separate, harder-to-undo act, so it is opt-in.
		private const bool ALSO_CLOSE_OPPORTUNITY_STATE = false;

		// The DEAL's status is NOT an option set - it is a lookup to the new_dealstatus entity,
		// whose records carry a new_code. Pl.Deal.DealOptionAutomation closes a deal as lost the
		// same way, by looking up this code, so the console follows the plugin rather than
		// inventing its own route.
		private const string DEALSTATUS_LOST_CODE = "DS-1009";   // 9 - Closed Lost

		// The OPPORTUNITY cannot be resolved by label, and this is the part worth reading.
		//
		// There is no "Closed Lost" sales stage. The equivalent is "11 - Declined", and the option
		// set carries TWO of them because the stage list is filtered by Opportunity Type - see
		// OpportunityFormCP.filterSalesStageCP in Javascript\new_opportunity_form.js:
		//
		//     Corporate Partnership - Prospect (100000003)  ->  11 - Declined = 100000004
		//     Corporate Partnership - Current  (100000006)  ->  11 - Declined = 100000018
		//
		// Resolving "Declined" by label would pick whichever comes first and would be wrong half
		// the time. These are lost pitches for prospects, so the Prospect pair is the right one.
		// The values are pinned here and VERIFIED against metadata at startup, so a renumbered
		// option still fails loudly rather than writing into the wrong stage.
		private const int OPPTYPE_CP_PROSPECT = 100000003;
		private const int SALESSTAGE_DECLINED_PROSPECT = 100000004;
		private const string SALESSTAGE_DECLINED_LABEL = "11 - Declined";
		private const string OPPTYPE_CP_PROSPECT_LABEL = "Prospect";

		// Carried over from CreateOppAndDealShell, which is the proven field set for this org.
		private const int LEADSOURCE_INBOUND = 100000002;
		private const int PITCHTYPE_NEW = 100000000;
		private const int CONTRACTLENGTH_1YR = 100000000;
		private const int OPTOUTTYPE_NO_OPTION = 100000000;
		private const int PLAYOFFOPTIONSTATUS_OUT = 100000003;

		// Lost Reason is made mandatory by the form script whenever the stage is Declined
		// (toggleLostReasonVisibilityCP). The grids do not say why anything was lost, so nothing
		// is invented here: it is left empty and Storm is prompted for it the first time they open
		// the record. Set a value only if they give us one. The CP options are Budget (100000016),
		// Timing (100000017), Invested with another team (100000018), ghosted/lost comms
		// (100000019), Assets not available (100000020), Shifted marketing priorities (100000021).
		private static readonly int? LOST_REASON = null;

		// =====================================================================

		private const string DEAL_ENTITY = "new_deals";
		private const string LINE_ENTITY = "new_deallines";
		private const string INVENTORY_ENTITY = "new_inventory";
		private const string SEASON_ENTITY = "new_season";

		private sealed class GridLine
		{
			public string Reason = "";
			public string Client;
			public string Deliverable;
			public decimal? Qty;
			public decimal? Rate;
			public decimal? Fees;
			public string InventoryName;
		}

		private sealed class Outcome
		{
			public string Client = "";
			public string Status = "";
			public string Detail = "";
			public int LinesCreated;
			public int LinesSkipped;
			public decimal Total;
		}

		static void Main()
		{
			Console.OutputEncoding = Encoding.UTF8;
			Console.WriteLine("=========================================================");
			Console.WriteLine("  LOAD CLOSED-LOST PRICING GRIDS");
			Console.WriteLine("=========================================================");
			Console.WriteLine("  Environment : " + ENV_URL);
			Console.WriteLine("  Season      : " + SEASON_NAME);
			Console.WriteLine("  Input       : " + INPUT_TSV);
			Console.Write("  Mode        : ");
			if (DRY_RUN)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("DRY RUN - nothing will be written");
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("LIVE - records will be created");
			}
			Console.ResetColor();
			Console.WriteLine("=========================================================");

			if (!File.Exists(INPUT_TSV))
			{
				Console.WriteLine("\nInput file not found. Nothing to do.");
				Console.ReadLine();
				return;
			}

			Console.Write("\nType YES to continue: ");
			if ((Console.ReadLine() ?? "").Trim().ToUpperInvariant() != "YES")
			{
				Console.WriteLine("\nCancelled. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			List<GridLine> lines = ReadInput(INPUT_TSV);
			Console.WriteLine("\nRead " + lines.Count + " deliverable row(s) for "
				+ lines.Select(l => l.Client).Distinct().Count() + " client(s).");

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

			var report = new List<Outcome>();
			var unmapped = new List<GridLine>();

			try
			{
				EntityReference season = FindSeason(service, SEASON_NAME);
				if (season == null)
				{
					Console.WriteLine("Season '" + SEASON_NAME + "' not found. Nothing to do.");
					Console.ReadLine();
					return;
				}

				VerifyOption(service, "opportunity", "new_salesstage", SALESSTAGE_DECLINED_PROSPECT, SALESSTAGE_DECLINED_LABEL);
				VerifyOption(service, "opportunity", "new_opportunitytype", OPPTYPE_CP_PROSPECT, OPPTYPE_CP_PROSPECT_LABEL);
				int stageLost = SALESSTAGE_DECLINED_PROSPECT;
				Guid dealLost = FindDealStatus(service, DEALSTATUS_LOST_CODE);
				Console.WriteLine("Opportunity sales stage = " + stageLost + " (" + SALESSTAGE_DECLINED_LABEL + ", Prospect variant)");
				Console.WriteLine("Deal status " + DEALSTATUS_LOST_CODE + " = " + dealLost + "\n");

				// One lookup of the season's inventory, by name, instead of a query per line.
				Dictionary<string, Guid> inventory = LoadInventory(service, season.Id);
				Console.WriteLine("Season inventory: " + inventory.Count + " item(s).\n");

				foreach (var group in lines.GroupBy(l => l.Client).OrderBy(g => g.Key))
				{
					var o = new Outcome { Client = group.Key };
					report.Add(o);
					Console.WriteLine("===== " + group.Key + " =====");

					string skipReason;
					if (SKIP_CLIENTS.TryGetValue(group.Key, out skipReason))
					{
						o.Status = "SKIPPED";
						o.Detail = skipReason;
						Console.WriteLine("  Skipped: " + skipReason + "\n");
						continue;
					}

					string accountName;
					if (!ACCOUNT_OVERRIDES.TryGetValue(group.Key, out accountName)) accountName = group.Key;
					EntityReference account = FindAccount(service, accountName);
					if (account == null)
					{
						if (!CREATE_MISSING_ACCOUNTS)
						{
							o.Status = "NO ACCOUNT";
							o.Detail = "no account matched '" + accountName + "'";
							Console.WriteLine("  " + o.Detail + " - skipped.\n");
							continue;
						}
						if (!DRY_RUN)
						{
							Guid newAcc = service.Create(new Entity("account") { ["name"] = group.Key });
							account = new EntityReference("account", newAcc) { Name = group.Key };
						}
						// Only the name is set. Account Type is left for a person: the Corporate
						// Partnership views filter on it, so a new account is invisible there until
						// somebody classifies it. Guessing the value would hide that.
						Console.WriteLine(DRY_RUN
							? "  Account does not exist - WOULD be created, and would need Account Type set by hand."
							: "  Account created - NEEDS Account Type set by hand.");
						o.Detail = DRY_RUN ? "account would be created" : "account created, Account Type still unset";
					}

					// A deal that already exists is TOPPED UP, not skipped. Storm returns the
					// inventory mapping in waves, so the run that matters is the second one: it has
					// to add the lines that could not be mapped the first time. Skipping the client
					// would make that pass do nothing at all.
					string dealName;
					bool isAdditional = ADDITIONAL_DEALS.TryGetValue(group.Key, out dealName);
					if (!isAdditional) dealName = group.Key + " - 2026 - Storm";

					Guid? existingDeal = account == null
						? null
						: FindDeal(service, account.Id, season.Id, isAdditional ? dealName : null);

					if (isAdditional)
						Console.WriteLine("  Additional deal on an existing partner: '" + dealName + "'");

					decimal total = group.Sum(l => l.Fees ?? 0m);
					o.Total = total;
					// Counted over ROWS, because a deal line now stands for a grid row and several
					// lines may share an inventory item. This line is printed before the deal is
					// even looked up, so it has to answer one question only: how many rows of this
					// grid have an item we can resolve. Counting distinct items here, left over
					// from the old rule, reported mapped rows as unmapped.
					int willCreate = group.Count(l => !string.IsNullOrWhiteSpace(l.InventoryName)
						&& inventory.ContainsKey(Key(l.InventoryName)));
					int willSkip = group.Count() - willCreate;

					Console.WriteLine("  Account : " + (account == null ? "(would be created)" : account.Name));
					Console.WriteLine("  Total   : " + total.ToString("C", CultureInfo.GetCultureInfo("en-US")));
					Console.WriteLine("  Lines   : " + willCreate + " to create, " + willSkip + " without a mapped item");

					foreach (var l in group.Where(x => string.IsNullOrWhiteSpace(x.InventoryName)
						|| !inventory.ContainsKey(Key(x.InventoryName))))
					{
						l.Reason = string.IsNullOrWhiteSpace(l.InventoryName)
							? "no inventory item suggested"
							: "the suggested item does not exist in the 2026 season";
						unmapped.Add(l);
					}

					// IDENTITY IS THE LINE NAME, NOT THE INVENTORY ITEM.
					//
					// The first design allowed one line per inventory item, so a second grid row
					// pointing at the same item was dropped. Nate's mapping showed what that costs:
					// on the BECU upsell he deliberately sent three different deliverables to
					// "Cove Corner" and two to "Activation Fund", which would have silently
					// discarded $100,000 of a $791,100 deal. And the rule was never a Dynamics
					// constraint, it was mine, adopted to make re-runs safe.
					//
					// So a deal line now stands for a GRID ROW, named with Storm's own wording, and
					// several lines may share an inventory item. Re-runs stay safe because the name
					// identifies the line. Pl.DealLines.InventoryManagement never writes new_name,
					// so the name is ours to rely on (checked before making it load-bearing).
					Dictionary<string, Guid> existingByName;
					Dictionary<Guid, List<Guid>> existingByInventory;
					ExistingLines(service, existingDeal, out existingByName, out existingByInventory);

					if (existingDeal.HasValue)
					{
						willCreate = group.Count(l => !string.IsNullOrWhiteSpace(l.InventoryName)
							&& inventory.ContainsKey(Key(l.InventoryName))
							&& !existingByName.ContainsKey(Key(l.Deliverable)));
						willSkip = group.Count() - willCreate;
						Console.WriteLine("  Deal    : already exists (" + existingByName.Count
							+ " line(s) on it) - topping up " + willCreate + " line(s)");
					}

					if (DRY_RUN || account == null)
					{
						o.Status = existingDeal.HasValue ? "WOULD TOP UP" : "WOULD CREATE";
						o.LinesCreated = willCreate;
						o.LinesSkipped = willSkip;
						Console.WriteLine();
						continue;
					}

					Guid dealId;
					if (existingDeal.HasValue)
					{
						dealId = existingDeal.Value;
					}
					else
					{
						Guid oppId = CreateOpportunity(service, account, season, dealName, total, stageLost);
						dealId = CreateDeal(service, account, season, oppId, dealName, dealLost);
					}

					var claimed = new HashSet<Guid>();

					foreach (GridLine l in group)
					{
						if (string.IsNullOrWhiteSpace(l.InventoryName)
							|| !inventory.ContainsKey(Key(l.InventoryName)))
						{
							o.LinesSkipped++;
							continue;
						}

						// Already there under this exact name. Claim it, do not just skip it: an
						// unclaimed line stays visible to the legacy branch below, and a later row
						// sharing the same inventory item will "adopt" it and rename it. That is
						// how Georgetown's $43,284 Milestone Moments line ended up labelled
						// "Milestone Moments - social", which is an $8,325 row.
						if (existingByName.ContainsKey(Key(l.Deliverable)))
						{
							claimed.Add(existingByName[Key(l.Deliverable)]);
							continue;
						}

						Guid invId = inventory[Key(l.InventoryName)];

						// One-time migration. Lines written before this change were named after the
						// inventory item. Such a line IS this row, so it is renamed rather than
						// duplicated. Self-limiting: once renamed, the branch above catches it.
						Guid legacyId;
						if (TakeLegacyLine(existingByName, existingByInventory, claimed,
							Key(l.InventoryName), invId, out legacyId))
						{
							service.Update(new Entity(LINE_ENTITY, legacyId) { ["new_name"] = l.Deliverable });
							existingByName[Key(l.Deliverable)] = legacyId;
							Console.WriteLine("    renamed existing line to '" + l.Deliverable + "'");
							continue;
						}

						CreateLine(service, dealId, season, invId, l);
						existingByName[Key(l.Deliverable)] = Guid.Empty;
						o.LinesCreated++;
					}

					if (ALSO_CLOSE_OPPORTUNITY_STATE && !existingDeal.HasValue)
					{
						Entity dealRow = service.Retrieve(DEAL_ENTITY, dealId, new ColumnSet("new_opportunity"));
						EntityReference oppRef = dealRow.GetAttributeValue<EntityReference>("new_opportunity");
						service.Execute(new Microsoft.Crm.Sdk.Messages.SetStateRequest
						{
							EntityMoniker = oppRef,
							State = new OptionSetValue(2),   // Lost
							Status = new OptionSetValue(4)   // Lost
						});
					}

					o.Status = existingDeal.HasValue ? "TOPPED UP" : "CREATED";
					o.Detail = "deal " + dealId;
					Console.WriteLine("  Created. " + o.Detail + "\n");
				}

				WriteReport(report, unmapped);

				Console.WriteLine("=========================================================");
				Console.WriteLine(DRY_RUN ? "  DRY RUN COMPLETE - nothing was written" : "  LOAD COMPLETE");
				Console.WriteLine("  Clients created : " + report.Count(r => r.Status == "CREATED" || r.Status == "WOULD CREATE"));
				Console.WriteLine("  Skipped         : " + report.Count(r => r.Status == "SKIPPED"));
				Console.WriteLine("  No account      : " + report.Count(r => r.Status == "NO ACCOUNT"));
				Console.WriteLine("  Topped up       : " + report.Count(r => r.Status == "TOPPED UP" || r.Status == "WOULD TOP UP"));
				Console.WriteLine("  Lines to create : " + report.Sum(r => r.LinesCreated));
				// The CSV is the one true list. Summing LinesSkipped counted a line that is already
				// on the deal as "unmapped", which reads as work still to do when there is none:
				// the summary said 107 while the file held 89.
				Console.WriteLine("  Lines unmapped  : " + unmapped.Count + "  (see the CSV)");
				Console.WriteLine("=========================================================");
				if (DRY_RUN) Console.WriteLine("\nSet DRY_RUN = false to apply these changes.");
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\nERROR: " + ex.Message);
				Console.ResetColor();
				Console.WriteLine(ex.StackTrace);
			}

			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}

		// -----------------------------------------------------------------
		private static string Key(string s)
		{
			return new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
		}

		private static List<GridLine> ReadInput(string path)
		{
			var list = new List<GridLine>();
			string[] all = File.ReadAllLines(path, Encoding.UTF8);
			if (all.Length == 0) return list;

			string[] header = all[0].Split('\t');
			Func<string, int> col = name => Array.FindIndex(header,
				h => string.Equals(h.Trim(), name, StringComparison.OrdinalIgnoreCase));

			int cClient = col("Client"), cDel = col("Deliverable"), cQty = col("Qty"),
				cRate = col("Rate"), cFees = col("Fees"), cInv = col("InventoryItem");

			for (int i = 1; i < all.Length; i++)
			{
				if (string.IsNullOrWhiteSpace(all[i])) continue;
				string[] f = all[i].Split('\t');
				Func<int, string> get = j => (j >= 0 && j < f.Length) ? f[j].Trim() : "";
				Func<int, decimal?> num = j =>
				{
					decimal d;
					return decimal.TryParse(get(j), NumberStyles.Any, CultureInfo.InvariantCulture, out d)
						? (decimal?)d : null;
				};
				list.Add(new GridLine
				{
					Client = get(cClient),
					Deliverable = get(cDel),
					Qty = num(cQty),
					Rate = num(cRate),
					Fees = num(cFees),
					InventoryName = get(cInv)
				});
			}
			return list;
		}

		private static int ResolveOption(IOrganizationService svc, string entity, string attribute, string label)
		{
			var resp = (RetrieveAttributeResponse)svc.Execute(new RetrieveAttributeRequest
			{
				EntityLogicalName = entity,
				LogicalName = attribute,
				RetrieveAsIfPublished = true
			});

			var meta = resp.AttributeMetadata as EnumAttributeMetadata;
			if (meta == null)
				throw new Exception(entity + "." + attribute + " is not an option set.");

			var options = meta.OptionSet.Options
				.Select(o => new { Value = o.Value ?? -1, Label = (o.Label.UserLocalizedLabel != null ? o.Label.UserLocalizedLabel.Label : "") })
				.ToList();

			var hits = options.Where(o =>
				o.Label.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

			if (hits.Count == 0)
				throw new Exception("No option on " + entity + "." + attribute + " matches '" + label
					+ "'. Available: " + string.Join(" | ", options.Select(o => o.Value + "=" + o.Label)));

			// Several options can share a label when the picklist is filtered by another field.
			// Taking the first would be a coin flip, so this refuses instead.
			if (hits.Count > 1)
				throw new Exception(hits.Count + " options on " + entity + "." + attribute
					+ " match '" + label + "': " + string.Join(" | ", hits.Select(o => o.Value + "=" + o.Label))
					+ ". Pin the right value explicitly instead of resolving by label.");

			return hits[0].Value;
		}

		/// <summary>
		/// Confirms a pinned option value still carries the label we expect. A renumbered or
		/// relabelled option then stops the run instead of quietly writing the wrong thing.
		/// </summary>
		private static void VerifyOption(IOrganizationService svc, string entity, string attribute,
			int value, string expectedLabel)
		{
			var resp = (RetrieveAttributeResponse)svc.Execute(new RetrieveAttributeRequest
			{
				EntityLogicalName = entity,
				LogicalName = attribute,
				RetrieveAsIfPublished = true
			});

			var meta = resp.AttributeMetadata as EnumAttributeMetadata;
			if (meta == null)
				throw new Exception(entity + "." + attribute + " is not an option set.");

			var opt = meta.OptionSet.Options.FirstOrDefault(o => (o.Value ?? -1) == value);
			if (opt == null)
				throw new Exception("Option " + value + " no longer exists on " + entity + "." + attribute + ".");

			string label = opt.Label.UserLocalizedLabel != null ? opt.Label.UserLocalizedLabel.Label : "";
			if (label.IndexOf(expectedLabel, StringComparison.OrdinalIgnoreCase) < 0)
				throw new Exception("Option " + value + " on " + entity + "." + attribute
					+ " reads '" + label + "', expected something containing '" + expectedLabel
					+ "'. The option set changed - check before running.");
		}

		private static EntityReference FindSeason(IOrganizationService svc, string name)
		{
			var q = new QueryExpression(SEASON_ENTITY) { ColumnSet = new ColumnSet("new_name") };
			q.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
			Entity e = svc.RetrieveMultiple(q).Entities.FirstOrDefault();
			return e == null ? null : new EntityReference(SEASON_ENTITY, e.Id) { Name = name };
		}

		/// <summary>
		/// Deal Status is a lookup, not a picklist. Records are identified by new_code - the same
		/// key Pl.Deal.DealOptionAutomation uses - because the display name has changed before.
		/// </summary>
		private static Guid FindDealStatus(IOrganizationService svc, string code)
		{
			var q = new QueryExpression("new_dealstatus") { ColumnSet = new ColumnSet("new_name") };
			q.Criteria.AddCondition("new_code", ConditionOperator.Equal, code);
			q.TopCount = 2;
			var found = svc.RetrieveMultiple(q).Entities;

			if (found.Count == 0)
				throw new Exception("No new_dealstatus record has code '" + code + "'.");
			if (found.Count > 1)
				throw new Exception("More than one new_dealstatus record has code '" + code + "'.");

			return found[0].Id;
		}

		private static EntityReference FindAccount(IOrganizationService svc, string client)
		{
			var q = new QueryExpression("account") { ColumnSet = new ColumnSet("name") };
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);
			var all = svc.RetrieveMultiple(q).Entities;

			string k = Key(client);
			Entity exact = all.FirstOrDefault(a => Key(a.GetAttributeValue<string>("name")) == k);
			if (exact != null)
				return new EntityReference("account", exact.Id) { Name = exact.GetAttributeValue<string>("name") };

			// Containment, but only one candidate. Two candidates means a human has to choose.
			var near = all.Where(a =>
			{
				string ak = Key(a.GetAttributeValue<string>("name"));
				return ak.Length >= 6 && k.Length >= 6 && (ak.Contains(k) || k.Contains(ak));
			}).ToList();

			if (near.Count == 1)
				return new EntityReference("account", near[0].Id) { Name = near[0].GetAttributeValue<string>("name") };

			return null;
		}

		private static Guid? FindDeal(IOrganizationService svc, Guid accountId, Guid seasonId, string exactName)
		{
			var q = new QueryExpression(DEAL_ENTITY) { ColumnSet = new ColumnSet("new_name") };
			q.Criteria.AddCondition("new_accountid", ConditionOperator.Equal, accountId);
			q.Criteria.AddCondition("new_season", ConditionOperator.Equal, seasonId);

			// An upsell deliberately sits beside the partner's existing deal, so for those the
			// account and season alone no longer identify one record. The name narrows it, and it
			// is a name this console sets, not one a plugin generated.
			if (exactName != null)
				q.Criteria.AddCondition("new_name", ConditionOperator.Equal, exactName);

			var found = svc.RetrieveMultiple(q).Entities;

			// Two deals for one account and season is the Starbucks shape: a base deal plus an
			// upsell. Topping up the wrong one would put lines on a signed deal, so this stops.
			if (found.Count > 1)
				throw new Exception("Account has " + found.Count + " deals in this season ("
					+ string.Join(" / ", found.Select(e => e.GetAttributeValue<string>("new_name")))
					+ "). Resolve by hand - the console will not guess which one the grid belongs to.");

			return found.Count == 1 ? (Guid?)found[0].Id : null;
		}

		/// <summary>
		/// The season's inventory by normalised name. One query instead of one per grid row.
		/// </summary>
		private static Dictionary<string, Guid> LoadInventory(IOrganizationService svc, Guid seasonId)
		{
			var map = new Dictionary<string, Guid>();
			var q = new QueryExpression(INVENTORY_ENTITY)
			{
				ColumnSet = new ColumnSet("new_name"),
				PageInfo = new PagingInfo { Count = 500, PageNumber = 1 }
			};
			q.Criteria.AddCondition("new_seasonid", ConditionOperator.Equal, seasonId);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

			while (true)
			{
				EntityCollection page = svc.RetrieveMultiple(q);
				foreach (Entity e in page.Entities)
				{
					string k = Key(e.GetAttributeValue<string>("new_name"));
					if (k.Length > 0 && !map.ContainsKey(k)) map[k] = e.Id;
				}
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}
			return map;
		}

		/// <summary>
		/// The lines already on a deal, indexed two ways: by name, which is how a row is matched to
		/// its line, and by inventory item, which is only needed to recognise lines written before
		/// the name became the identity.
		/// </summary>
		private static void ExistingLines(IOrganizationService svc, Guid? dealId,
			out Dictionary<string, Guid> byName, out Dictionary<Guid, List<Guid>> byInventory)
		{
			byName = new Dictionary<string, Guid>();
			byInventory = new Dictionary<Guid, List<Guid>>();
			if (!dealId.HasValue) return;

			var q = new QueryExpression(LINE_ENTITY) { ColumnSet = new ColumnSet("new_name", "new_inventory") };
			q.Criteria.AddCondition("new_dealid", ConditionOperator.Equal, dealId.Value);

			foreach (Entity e in svc.RetrieveMultiple(q).Entities)
			{
				string n = Key(e.GetAttributeValue<string>("new_name"));
				if (n.Length > 0 && !byName.ContainsKey(n)) byName[n] = e.Id;

				EntityReference r = e.GetAttributeValue<EntityReference>("new_inventory");
				if (r == null) continue;
				if (!byInventory.ContainsKey(r.Id)) byInventory[r.Id] = new List<Guid>();
				byInventory[r.Id].Add(e.Id);
			}
		}

		/// <summary>
		/// Finds a line left over from the old naming, where the name was the inventory item rather
		/// than the deliverable. Only one such line per item can be claimed, so two rows sharing an
		/// item do not both try to adopt it.
		/// </summary>
		private static bool TakeLegacyLine(Dictionary<string, Guid> byName,
			Dictionary<Guid, List<Guid>> byInventory, HashSet<Guid> claimed,
			string inventoryKey, Guid inventoryId, out Guid lineId)
		{
			lineId = Guid.Empty;
			if (!byName.ContainsKey(inventoryKey)) return false;

			Guid candidate = byName[inventoryKey];
			if (claimed.Contains(candidate)) return false;
			if (!byInventory.ContainsKey(inventoryId) || !byInventory[inventoryId].Contains(candidate)) return false;

			claimed.Add(candidate);
			byName.Remove(inventoryKey);
			lineId = candidate;
			return true;
		}

		private static Guid CreateOpportunity(IOrganizationService svc, EntityReference account,
			EntityReference season, string dealName, decimal total, int stageLost)
		{
			var opp = new Entity("opportunity");
			opp["name"] = dealName;
			opp["parentaccountid"] = account;
			opp["new_basketballseason"] = season;
			opp["new_opportunitytype"] = new OptionSetValue(OPPTYPE_CP_PROSPECT);
			opp["new_salesstage"] = new OptionSetValue(stageLost);
			opp["new_leadsource"] = new OptionSetValue(LEADSOURCE_INBOUND);
			opp["new_pitchtype"] = new OptionSetValue(PITCHTYPE_NEW);
			opp["new_pitchedcontractlength"] = new OptionSetValue(CONTRACTLENGTH_1YR);
			opp["estimatedclosedate"] = DateTime.Today;
			opp["estimatedvalue"] = new Money(total);
			opp["description"] = "Created from the 2026 closed-lost pricing grid.";
			if (LOST_REASON.HasValue) opp["new_lostreason"] = new OptionSetValue(LOST_REASON.Value);
			return svc.Create(opp);
		}

		private static Guid CreateDeal(IOrganizationService svc, EntityReference account,
			EntityReference season, Guid oppId, string dealName, Guid dealLost)
		{
			var deal = new Entity(DEAL_ENTITY);
			deal["new_accountid"] = account;
			deal["new_season"] = season;
			deal["new_opportunity"] = new EntityReference("opportunity", oppId);
			deal["new_dealstatus"] = new EntityReference("new_dealstatus", dealLost);
			// Pre-filled so the option validator does not block the record, same as
			// CreateOppAndDealShell does.
			deal["new_optouttype"] = new OptionSetValue(OPTOUTTYPE_NO_OPTION);
			deal["new_playoffoptionstatus"] = new OptionSetValue(PLAYOFFOPTIONSTATUS_OUT);

			Guid id = svc.Create(deal);

			// Pl.Deal.SetName runs pre-op on Create and rebuilds new_name from Account + Season,
			// discarding whatever we send. The name has to be set afterwards.
			svc.Update(new Entity(DEAL_ENTITY, id) { ["new_name"] = dealName });
			return id;
		}

		private static void CreateLine(IOrganizationService svc, Guid dealId, EntityReference season,
			Guid inventoryId, GridLine l)
		{
			var line = new Entity(LINE_ENTITY);
			line["new_name"] = l.Deliverable;
			line["new_dealid"] = new EntityReference(DEAL_ENTITY, dealId);
			line["new_inventory"] = new EntityReference(INVENTORY_ENTITY, inventoryId);
			line["new_seasonid"] = season;

			// new_quantity is a WHOLE NUMBER, not a decimal - see TrakImport/DynamicsService.cs,
			// which is the proven import path for this entity. Sending a decimal fails with
			// "Incorrect attribute value type System.Decimal". new_rate is Currency.
			if (l.Qty.HasValue) line["new_quantity"] = (int)Math.Round(l.Qty.Value, MidpointRounding.AwayFromZero);
			if (l.Rate.HasValue) line["new_rate"] = new Money(l.Rate.Value);

			// new_total is computed by the InventoryManagement pre-op step from quantity x rate.
			// Writing it here would be overwritten, so it is left alone.
			svc.Create(line);
		}

		private static void WriteReport(List<Outcome> report, List<GridLine> unmapped)
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string dir = AppDomain.CurrentDomain.BaseDirectory;

			string p1 = Path.Combine(dir, "ClosedLostGrids_Report_" + stamp + ".csv");
			using (var w = new StreamWriter(p1, false, Encoding.UTF8))
			{
				w.WriteLine("Client,Status,Detail,LinesCreated,LinesUnmapped,Total");
				foreach (Outcome o in report)
					w.WriteLine(Csv(o.Client) + "," + Csv(o.Status) + "," + Csv(o.Detail) + ","
						+ o.LinesCreated + "," + o.LinesSkipped + ","
						+ o.Total.ToString(CultureInfo.InvariantCulture));
			}

			string p2 = Path.Combine(dir, "ClosedLostGrids_Unmapped_" + stamp + ".csv");
			using (var w = new StreamWriter(p2, false, Encoding.UTF8))
			{
				w.WriteLine("Client,Deliverable,Qty,Rate,Fees,InventoryItemGiven,Reason");
				foreach (GridLine l in unmapped)
					w.WriteLine(Csv(l.Client) + "," + Csv(l.Deliverable) + ","
						+ (l.Qty.HasValue ? l.Qty.Value.ToString(CultureInfo.InvariantCulture) : "") + ","
						+ (l.Rate.HasValue ? l.Rate.Value.ToString(CultureInfo.InvariantCulture) : "") + ","
						+ (l.Fees.HasValue ? l.Fees.Value.ToString(CultureInfo.InvariantCulture) : "") + ","
						+ Csv(l.InventoryName) + "," + Csv(l.Reason));
			}

			Console.WriteLine("\nReport   : " + p1);
			Console.WriteLine("Unmapped : " + p2 + "  (" + unmapped.Count + " row(s) for Storm to map)\n");
		}

		private static string Csv(string s)
		{
			s = s ?? "";
			return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
		}
	}
}
