using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CreateFlatAmountExample
{
	/// <summary>
	/// Builds the sandbox records behind Example B of the incremental-games user manual:
	/// a two-year contract priced with a FLAT AMOUNT for the extra games, so the reader can open
	/// the very deal the manual screenshots and see the same numbers.
	///
	/// Why a separate account instead of reusing Coho Winery: Coho is Example A (Per Game Rate).
	/// Rewriting its clause to Flat Amount for a screenshot would leave Example A pointing at a
	/// deal that no longer matches it. Two examples, two deals.
	///
	/// WHAT THIS TOOL DOES AND DELIBERATELY DOES NOT DO
	/// It creates the 2026 side only: account, opportunity, deal, deal lines. The 2027 deal is NOT
	/// created here. You close the opportunity as Won in the UI and let CloneMultiYearDeals build
	/// it, because the whole point of Example B is what the clone does with a flat amount - it
	/// copies it instead of recalculating it. A 2027 deal fabricated by this console would prove
	/// nothing and could quietly disagree with what the plugin actually does.
	///
	/// ESCALATOR IS ZERO ON PURPOSE. An escalator would raise every line on the clone, and the
	/// manual's allocation table would stop matching what the reader sees by a few cents per line.
	///
	/// Safe to run twice: if the opportunity already exists, nothing is created.
	/// Start with DRY_RUN = true.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// Sandbox. Change deliberately, never by accident.
		private const string ENV_URL = "https://org00bff505.crm.dynamics.com/";

		private const bool DRY_RUN = false;

		// The deal whose lines are copied, so the example has a realistic spread of values
		// including lines worth nothing. Its own clause is never touched.
		private const string TEMPLATE_DEAL_NAME = "Coho Winery (sample) - 2026 - Storm";

		private const string ACCOUNT_NAME = "Flat Amount Example (sample)";
		private const string SEASON_NAME = "2026 - Storm";

		// The clause, exactly as Example B describes it.
		private const int CONTRACTED_HOME_GAMES = 22;
		private const int CONTRACTED_AWAY_GAMES = 22;
		private const decimal FLAT_AMOUNT = 10000m;

		// The one line held out of the split, so the manual can show that the divisor is the
		// eligible lines and not the deal total. Matched on the line's product/inventory name.
		private const string EXCLUDE_FROM_PRORATION_CONTAINS = "Jersey Badge";

		// =====================================================================

		private const string DEAL_ENTITY = "new_deals";
		private const string LINE_ENTITY = "new_deallines";
		private const string LINE_DEAL_LOOKUP = "new_dealid";

		// Option set values, read from the plugin source - see CloneMultiYearDeals.cs and
		// DistributeIncrementalRevenue.cs.
		private const int CLAUSE_IN = 100000000;
		private const int METHOD_FLAT_AMOUNT = 100000002;
		private const int CONTRACTLENGTH_2YR = 100000001;   // totalYears = (value - 100000000) + 1
		private const int OPPTYPE_CP_PROSPECT = 100000003;
		private const int LEADSOURCE_INBOUND = 100000002;
		private const int CONFIDENCE_100_CLOSED_WON = 100000005;
		private const int SALESSTAGE_INITIAL_PROSPECT = 100000000;
		private const int PITCHTYPE_NEW = 100000000;
		private const int OPTOUTTYPE_NO_OPTION = 100000000;
		private const int PLAYOFFOPTIONSTATUS_OUT = 100000003;

		private static readonly HashSet<string> SKIP_ALWAYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"new_deallinesid", "new_name",
			"ownerid", "owningbusinessunit", "owninguser", "owningteam",
			"statecode", "statuscode", "versionnumber",
			"createdon", "createdby", "modifiedon", "modifiedby",
			"createdonbehalfby", "modifiedonbehalfby",
			"importsequencenumber", "overriddencreatedon",
			"timezoneruleversionnumber", "utcconversiontimezonecode",
			// Belongs to the season the revenue is earned in, never copied.
			"new_incrementalrateperhomegame", "new_incrementalrateperawaygame", "new_incrementalrevenue"
		};

		static void Main(string[] args)
		{
			Console.WriteLine("=========================================================");
			Console.WriteLine("  MANUAL EXAMPLE B - FLAT AMOUNT (2026 side only)");
			Console.WriteLine("=========================================================");
			Console.WriteLine($"  Environment  : {ENV_URL}");
			Console.WriteLine($"  Account      : {ACCOUNT_NAME}");
			Console.WriteLine($"  Lines copied : {TEMPLATE_DEAL_NAME}");
			Console.WriteLine($"  Flat amount  : {FLAT_AMOUNT:C}");
			Console.Write("  Mode         : ");

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

			if (!ENV_URL.Contains("org00bff505"))
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\n  *** NOT the sandbox URL. This creates demo records. Stop. ***");
				Console.ResetColor();
			}

			Console.WriteLine("=========================================================");
			Console.Write("\nType YES to continue: ");
			if ((Console.ReadLine() ?? string.Empty).Trim().ToUpperInvariant() != "YES")
			{
				Console.WriteLine("\nCancelled. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			string connectionString =
				$@"AuthType=OAuth;Url={ENV_URL};AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;" +
				 @"RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

			Console.WriteLine("\nConnecting...");
			CrmServiceClient service = new CrmServiceClient(connectionString);

			if (!service.IsReady)
			{
				Console.WriteLine("Connection error: " + service.LastCrmError);
				Console.ReadLine();
				return;
			}
			Console.WriteLine("Connected.\n");

			try
			{
				Entity template = FindByName(service, DEAL_ENTITY, TEMPLATE_DEAL_NAME);
				if (template == null)
				{
					Fail($"Template deal not found: {TEMPLATE_DEAL_NAME}");
					return;
				}

				List<Entity> templateLines = GetDealLines(service, template.Id);
				Console.WriteLine($"Template deal  : {templateLines.Count} line(s)");

				if (templateLines.Count == 0)
				{
					Fail("The template deal has no lines. There would be nothing to allocate against.");
					return;
				}

				EntityReference seasonRef = ResolveSeason(service, SEASON_NAME);
				if (seasonRef == null)
				{
					Fail($"Season not found: {SEASON_NAME}");
					return;
				}

				ReportSeasonGames(service, seasonRef);

				string oppName = $"{ACCOUNT_NAME} - 2026 - Storm";
				if (FindByName(service, "opportunity", oppName, "name") != null)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine($"\nOpportunity '{oppName}' already exists. Nothing to do.");
					Console.WriteLine("Delete it (and its deal) first if you want to rebuild the example.");
					Console.ResetColor();
					Console.ReadLine();
					return;
				}

				// Preview the split before writing anything: this is the table that goes in the
				// manual, and it is the reason to run the dry run at all.
				PreviewAllocation(templateLines);

				if (DRY_RUN)
				{
					Console.WriteLine("\nDRY RUN - would create: 1 account (if missing), 1 opportunity, 1 deal, " +
									  $"{templateLines.Count} deal line(s).");
					Done();
					return;
				}

				EntityReference accountRef = EnsureAccount(service);
				Console.WriteLine($"Account        : {accountRef.Id}");

				Guid oppId = CreateOpportunity(service, oppName, accountRef, seasonRef);
				Console.WriteLine($"Opportunity    : {oppId}  (2 Years, escalator 0)");

				Guid dealId = CreateDeal(service, oppName, oppId, accountRef, seasonRef);
				Console.WriteLine($"Deal 2026      : {dealId}");

				HashSet<string> lineCreatable = CreatableAttributes(service, LINE_ENTITY);
				int n = 0, excluded = 0;

				foreach (Entity line in templateLines)
				{
					Entity clone = new Entity(LINE_ENTITY);

					foreach (var kv in line.Attributes)
					{
						if (SKIP_ALWAYS.Contains(kv.Key)) continue;
						if (!lineCreatable.Contains(kv.Key)) continue;
						if (kv.Value == null) continue;
						if (kv.Key.EndsWith("_base", StringComparison.OrdinalIgnoreCase)) continue;

						clone[kv.Key] = kv.Value;
					}

					clone[LINE_DEAL_LOOKUP] = new EntityReference(DEAL_ENTITY, dealId);

					if (LineLabel(line).IndexOf(EXCLUDE_FROM_PRORATION_CONTAINS, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						clone["new_includeinautoproration"] = false;
						excluded++;
					}
					else
					{
						clone["new_includeinautoproration"] = true;
					}

					service.Create(clone);
					n++;
				}

				Console.WriteLine($"Deal lines     : {n} created, {excluded} held out of the split");

				Console.ForegroundColor = ConsoleColor.Green;
				Console.WriteLine("\nThe 2026 side is ready. Finish it by hand, in this order:");
				Console.ResetColor();
				Console.WriteLine("  1. Open the deal. Incremental Contract Value should read $10,000.00 and");
				Console.WriteLine("     Incremental Home Games 0 - the 2026 season has no extra games. Screenshot 18.");
				Console.WriteLine("  2. Close the opportunity as Won. CloneMultiYearDeals builds the 2027 deal.");
				Console.WriteLine("  3. On the 2027 deal: contract value $10,000.00 COPIED (not recalculated),");
				Console.WriteLine("     variance $10,000.00. Screenshot 19.");
				Console.WriteLine("  4. Press Distribute Incremental Revenue. Screenshots 20 and 21.");
				Console.WriteLine("  5. Send me the real per-line amounts - the manual's table gets rebuilt from");
				Console.WriteLine("     what the button actually wrote, not from what we predicted.");
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\nError: " + ex);
				Console.ResetColor();
			}

			Done();
		}

		/// <summary>
		/// What the Distribute button is expected to do, worked out from the line values before
		/// anything is written. Printing it first means a surprise shows up here rather than in a
		/// screenshot that has already gone into the manual.
		/// </summary>
		private static void PreviewAllocation(List<Entity> lines)
		{
			var eligible = new List<Tuple<string, decimal>>();
			decimal excludedValue = 0m;

			foreach (Entity l in lines)
			{
				string label = LineLabel(l);
				decimal value = LineValue(l);

				if (label.IndexOf(EXCLUDE_FROM_PRORATION_CONTAINS, StringComparison.OrdinalIgnoreCase) >= 0)
					excludedValue += value;
				else
					eligible.Add(Tuple.Create(label, value));
			}

			decimal pool = eligible.Sum(e => e.Item2);

			Console.WriteLine("\n--- Expected allocation of " + FLAT_AMOUNT.ToString("C") + " ---");
			Console.WriteLine($"{"Line",-42}{"Line value",15}{"Share",15}");

			decimal assigned = 0m;
			foreach (var e in eligible.OrderByDescending(e => e.Item2))
			{
				decimal share = pool == 0 ? 0 : Math.Round(FLAT_AMOUNT * e.Item2 / pool, 2);
				assigned += share;
				Console.WriteLine($"{Trim(e.Item1, 40),-42}{e.Item2,15:C}{share,15:C}");
			}

			Console.WriteLine($"{"ELIGIBLE TOTAL",-42}{pool,15:C}{assigned,15:C}");
			Console.WriteLine($"{"held out (" + EXCLUDE_FROM_PRORATION_CONTAINS + ")",-42}{excludedValue,15:C}{0m,15:C}");

			if (assigned != FLAT_AMOUNT)
			{
				Console.WriteLine($"  Rounding remainder of {(FLAT_AMOUNT - assigned):C} goes to the largest eligible line.");
			}

			if (pool == 0)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("  Every eligible line is worth zero. There is nothing to prorate against.");
				Console.ResetColor();
			}
		}

		private static string LineLabel(Entity line)
		{
			var inv = line.GetAttributeValue<EntityReference>("new_inventory");
			if (inv != null && !string.IsNullOrEmpty(inv.Name)) return inv.Name;

			var prod = line.GetAttributeValue<EntityReference>("new_productid");
			if (prod != null && !string.IsNullOrEmpty(prod.Name)) return prod.Name;

			return line.GetAttributeValue<string>("new_name") ?? "(unnamed line)";
		}

		private static decimal LineValue(Entity line)
		{
			var overrideTotal = line.GetAttributeValue<Money>("new_linetotaloverride");
			if (overrideTotal != null && overrideTotal.Value != 0) return overrideTotal.Value;

			var total = line.GetAttributeValue<Money>("new_total");
			if (total != null) return total.Value;

			var rate = line.GetAttributeValue<Money>("new_rate");
			int qty = line.GetAttributeValue<int?>("new_quantity") ?? 0;
			return (rate?.Value ?? 0m) * qty;
		}

		private static EntityReference EnsureAccount(CrmServiceClient svc)
		{
			Entity existing = FindByName(svc, "account", ACCOUNT_NAME, "name");
			if (existing != null)
				return new EntityReference("account", existing.Id) { Name = ACCOUNT_NAME };

			Entity acc = new Entity("account");
			acc["name"] = ACCOUNT_NAME;
			Guid id = svc.Create(acc);

			return new EntityReference("account", id) { Name = ACCOUNT_NAME };
		}

		private static Guid CreateOpportunity(CrmServiceClient svc, string name,
			EntityReference accountRef, EntityReference seasonRef)
		{
			Entity opp = new Entity("opportunity");
			opp["name"] = name;
			opp["parentaccountid"] = accountRef;
			opp["new_opportunitytype"] = new OptionSetValue(OPPTYPE_CP_PROSPECT);
			opp["new_leadsource"] = new OptionSetValue(LEADSOURCE_INBOUND);
			opp["new_pitchdate"] = DateTime.Today;
			opp["new_pitchedcontractlength"] = new OptionSetValue(CONTRACTLENGTH_2YR);
			opp["new_escalator"] = 0m;    // see the note at the top of this file
			opp["new_confidencelevel"] = new OptionSetValue(CONFIDENCE_100_CLOSED_WON);
			opp["new_pitchtype"] = new OptionSetValue(PITCHTYPE_NEW);
			opp["estimatedclosedate"] = DateTime.Today;
			opp["new_salesstage"] = new OptionSetValue(SALESSTAGE_INITIAL_PROSPECT);
			opp["new_basketballseason"] = seasonRef;

			return svc.Create(opp);
		}

		private static Guid CreateDeal(CrmServiceClient svc, string name, Guid oppId,
			EntityReference accountRef, EntityReference seasonRef)
		{
			Entity deal = new Entity(DEAL_ENTITY);
			deal["new_name"] = name;
			deal["new_opportunity"] = new EntityReference("opportunity", oppId);
			deal["new_accountid"] = accountRef;
			deal["new_season"] = seasonRef;
			deal["new_optouttype"] = new OptionSetValue(OPTOUTTYPE_NO_OPTION);
			deal["new_playoffoptionstatus"] = new OptionSetValue(PLAYOFFOPTIONSTATUS_OUT);

			// The clause. Under Flat Amount the contract value is typed, not derived, so it is
			// written here rather than left for the platform to calculate.
			deal["new_incrementalgamesclause"] = new OptionSetValue(CLAUSE_IN);
			deal["new_incrementalpricingmethod"] = new OptionSetValue(METHOD_FLAT_AMOUNT);
			deal["new_awaygamebenefits"] = true;
			deal["new_contractedhomegames"] = CONTRACTED_HOME_GAMES;
			deal["new_contractedawaygames"] = CONTRACTED_AWAY_GAMES;
			deal["new_incrementalcontractvalue"] = new Money(FLAT_AMOUNT);

			return svc.Create(deal);
		}

		/// <summary>
		/// The 2026 season is supposed to have NO headroom - that is what makes Example B's point
		/// that a flat amount is a contract term rather than a calculation. Worth stating out loud
		/// so a changed season record does not silently ruin the example.
		/// </summary>
		private static void ReportSeasonGames(IOrganizationService svc, EntityReference seasonRef)
		{
			try
			{
				Entity s = svc.Retrieve(seasonRef.LogicalName, seasonRef.Id,
					new ColumnSet("new_name", "new_homegames", "new_awaygames"));

				int home = s.GetAttributeValue<int?>("new_homegames") ?? 0;
				int away = s.GetAttributeValue<int?>("new_awaygames") ?? 0;

				Console.WriteLine($"Season         : {s.GetAttributeValue<string>("new_name")} - {home} home / {away} away");

				if (home != CONTRACTED_HOME_GAMES || away != CONTRACTED_AWAY_GAMES)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine($"  NOTE: Example B expects {CONTRACTED_HOME_GAMES}/{CONTRACTED_AWAY_GAMES} here so the");
					Console.WriteLine("  source deal shows zero incremental games. It will show a non-zero count instead.");
					Console.ResetColor();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Season         : could not be read - " + ex.Message);
			}
		}

		private static HashSet<string> CreatableAttributes(IOrganizationService service, string logicalName)
		{
			var resp = (RetrieveEntityResponse)service.Execute(new RetrieveEntityRequest
			{
				LogicalName = logicalName,
				EntityFilters = EntityFilters.Attributes,
				RetrieveAsIfPublished = true
			});

			return new HashSet<string>(
				resp.EntityMetadata.Attributes
					.Where(a => a.IsValidForCreate == true && a.AttributeType != AttributeTypeCode.Virtual)
					.Select(a => a.LogicalName),
				StringComparer.OrdinalIgnoreCase);
		}

		private static EntityReference ResolveSeason(IOrganizationService svc, string name)
		{
			Entity s = FindByName(svc, "new_season", name);
			return s == null ? null : new EntityReference("new_season", s.Id) { Name = name };
		}

		private static Entity FindByName(IOrganizationService svc, string entity, string name, string field = "new_name")
		{
			var q = new QueryExpression(entity) { ColumnSet = new ColumnSet(true), TopCount = 1 };
			q.Criteria.AddCondition(field, ConditionOperator.Equal, name);

			return svc.RetrieveMultiple(q).Entities.FirstOrDefault();
		}

		private static List<Entity> GetDealLines(IOrganizationService svc, Guid dealId)
		{
			var q = new QueryExpression(LINE_ENTITY) { ColumnSet = new ColumnSet(true) };
			q.Criteria.AddCondition(LINE_DEAL_LOOKUP, ConditionOperator.Equal, dealId);
			q.PageInfo = new PagingInfo { Count = 500, PageNumber = 1 };

			var all = new List<Entity>();
			while (true)
			{
				var page = svc.RetrieveMultiple(q);
				all.AddRange(page.Entities);
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}

			return all;
		}

		private static string Trim(string s, int n)
		{
			return string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
		}

		private static void Fail(string message)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine(message);
			Console.ResetColor();
			Done();
		}

		private static void Done()
		{
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}
	}
}
