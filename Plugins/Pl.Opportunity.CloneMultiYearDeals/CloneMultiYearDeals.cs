using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Opportunity.CloneMultiYearDeals
{
	public class CloneMultiYearDeals : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = factory.CreateOrganizationService(context.UserId);
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			tracingService.Trace($"--- PLUGIN STARTED: CloneMultiYearDeals (Full Architecture) ---");
			tracingService.Trace($"Current Depth: {context.Depth}, Message: {context.MessageName}");

			try
			{
				// Increased depth tolerance to 2 to allow synchronous workflows/Flows to trigger the plugin
				if (context.Depth > 2)
				{
					tracingService.Trace("ABORT: Context Depth is greater than 2. Exiting to prevent infinite loops.");
					return;
				}

				Guid oppId = Guid.Empty;

				if (context.MessageName.ToLower() == "win" && context.InputParameters.Contains("OpportunityClose") && context.InputParameters["OpportunityClose"] is Entity)
				{
					oppId = ((Entity)context.InputParameters["OpportunityClose"]).GetAttributeValue<EntityReference>("opportunityid").Id;
				}
				else if (context.MessageName.ToLower() == "update" && context.PrimaryEntityName == "opportunity")
				{
					Entity target = (Entity)context.InputParameters["Target"];
					if (!target.Contains("statecode") || target.GetAttributeValue<OptionSetValue>("statecode").Value != 1)
					{
						tracingService.Trace("ABORT: Update message but statecode is not Won (1).");
						return;
					}
					oppId = target.Id;
				}
				else
				{
					tracingService.Trace("ABORT: Message is neither Win nor a Won Update.");
					return;
				}

				if (oppId == Guid.Empty) return;

				// 1. Retrieve Master Opportunity 
				Entity opp = service.Retrieve("opportunity", oppId, new ColumnSet(
					"name", "parentaccountid", "parentcontactid", "new_pitchdate", "estimatedclosedate",
					"new_opportunitytype", "new_pitchedcontractlength", "new_escalator",
					"campaignid", "new_leadsource", "budgetstatus", "new_pitchtype", "new_confidencelevel", "estimatedvalue"
				));

				if (!opp.Contains("new_opportunitytype"))
				{
					tracingService.Trace("ABORT: Opportunity does not have new_opportunitytype.");
					return;
				}

				int oppType = opp.GetAttributeValue<OptionSetValue>("new_opportunitytype").Value;
				if (oppType != 100000003 && oppType != 100000006)
				{
					tracingService.Trace($"ABORT: OppType {oppType} is not Prospect or Current.");
					return;
				}

				if (!opp.Contains("new_pitchedcontractlength"))
				{
					tracingService.Trace("ABORT: Opportunity does not have Pitched Contract Length.");
					return;
				}

				int optionValue = opp.GetAttributeValue<OptionSetValue>("new_pitchedcontractlength").Value;
				int totalYears = (optionValue - 100000000) + 1;

				if (totalYears <= 1)
				{
					tracingService.Trace($"ABORT: Contract length is {totalYears} year(s). No cloning needed.");
					return;
				}

				// 2. Retrieve Base Deal
				QueryExpression dealQuery = new QueryExpression("new_deals")
				{
					ColumnSet = new ColumnSet(
					"new_name", "new_accountid", "new_dealstatus", "new_dealtype",
					"new_partnershipassigneeemail", "new_salesperson", "new_serviceperson", "new_season",
					// Incremental games clause: negotiated once, inherited by every year of the contract.
					// These MUST be listed here or dealFieldsToCopy silently copies nothing.
					"new_incrementalgamesclause", "new_awaygamebenefits",
					"new_contractedhomegames", "new_contractedawaygames",
					"new_investmentperhomegame", "new_investmentperawaygame",
					"new_incrementalpricingmethod", "new_incrementalgamesnotes",
					// Needed for the Flat Amount case, where the agreed figure travels with the
					// contract instead of being recalculated from the season.
					"new_incrementalcontractvalue"
				)
				};
				dealQuery.Criteria.AddCondition("new_opportunity", ConditionOperator.Equal, opp.Id);
				Entity baseDeal = service.RetrieveMultiple(dealQuery).Entities.FirstOrDefault();

				// Validation rule to prevent closing without a Deal
				if (baseDeal == null)
				{
					// Throwing this exception stops the save process and shows a popup error to the user.
					throw new InvalidPluginExecutionException("Validation Error: You cannot close a Multi-Year Opportunity (Contract Length > 1) as Won without at least one associated Deal.");
				}

				if (!baseDeal.Contains("new_season"))
				{
					// Also preventing save if the Deal exists but lacks a season, since math depends on it.
					throw new InvalidPluginExecutionException("Validation Error: The associated Deal is missing a 'Season'. A Season is required to accurately clone future Multi-Year Deals.");
				}

				// 3. Season Math
				EntityReference baseSeasonRef = baseDeal.GetAttributeValue<EntityReference>("new_season");
				Entity baseSeason = service.Retrieve("new_season", baseSeasonRef.Id, new ColumnSet("new_name", "new_seasonyear"));

				int startYear = baseSeason.GetAttributeValue<int>("new_seasonyear");
				string seasonName = baseSeason.GetAttributeValue<string>("new_name");
				string seasonSuffix = seasonName.Replace(startYear.ToString(), "").Trim(' ', '-');

				// 4. Financial Math setup 
				decimal escalatorPercent = opp.Contains("new_escalator") ? opp.GetAttributeValue<decimal>("new_escalator") : 0m;
				decimal multiplier = 1m + (escalatorPercent / 100m);
				decimal currentMultiplier = 1m;

				// 5. Retrieve Base Deal Lines 
				QueryExpression lineQuery = new QueryExpression("new_deallines")
				{
					ColumnSet = new ColumnSet(
					"new_name", "new_inventory", "new_quantity", "new_discount", "new_rate", "new_notes",
					"new_seasonid", "new_ratecard",
					// new_linetotaloverride was missing here as well as from lineFieldsToCopy: a line
					// priced through the override was cloned at quantity x escalated rate instead.
					"new_linetotaloverride",
					"new_includeinautoproration",
					"new_incrementalrateperhomegame", "new_incrementalrateperawaygame"
				)
				};
				lineQuery.Criteria.AddCondition("new_dealid", ConditionOperator.Equal, baseDeal.Id);
				EntityCollection baseLines = service.RetrieveMultiple(lineQuery);

				// --- VALIDATION PASS: every year is checked before a single record is created ---
				//
				// Each cloned line has to point at its own season's inventory record for the same
				// asset. Copying the source reference would leave a 2027 line consuming 2026
				// availability, and a three-year deal would draw down the same stock three times.
				//
				// The check runs for ALL target years up front rather than inside the cloning loop.
				// Validating year by year would let 2027 be created and only then fail on 2028,
				// leaving half a contract behind and depending on the plugin's transaction to undo
				// it. Nothing is created until every year is known to be viable.
				var seasonByYearIndex = new Dictionary<int, Entity>();
				var inventoryByYearIndex = new Dictionary<int, Dictionary<Guid, EntityReference>>();

				for (int i = 2; i <= totalYears; i++)
				{
					int year = startYear + (i - 1);

					Entity season = FindSeasonForYear(year, seasonSuffix, service);
					if (season == null)
					{
						throw new InvalidPluginExecutionException(
							$"There is no '{seasonSuffix}' season record for {year}. " +
							"Future-year deals cannot be generated until that season exists. Nothing has been created.");
					}

					seasonByYearIndex[i] = season;
					inventoryByYearIndex[i] = MapInventoryToTargetSeason(baseLines, season.Id, year, service, tracingService);
				}

				tracingService.Trace($"Validation passed for {seasonByYearIndex.Count} future year(s). Cloning now.");

				// Stamping the source deal happens after validation, not before, so a run that stops
				// on a missing season or missing inventory leaves the source deal exactly as it was.
				tracingService.Trace($"Marking base Deal with sequence=1 and totalYears={totalYears}");
				Entity baseDealUpdate = new Entity("new_deals", baseDeal.Id);
				baseDealUpdate["new_contractyearsequence"] = 1;
				baseDealUpdate["new_totalcontractyears"] = totalYears;
				service.Update(baseDealUpdate);
				tracingService.Trace("Base Deal updated with multi-year tracking fields.");

				// --- CLONING LOOP ---
				for (int i = 2; i <= totalYears; i++)
				{
					int targetYear = startYear + (i - 1);
					currentMultiplier *= multiplier;

					Entity targetSeason = seasonByYearIndex[i];
					Dictionary<Guid, EntityReference> inventoryForTargetSeason = inventoryByYearIndex[i];

					// B. Clone Opportunity (Management)
					Entity newOpp = new Entity("opportunity");
					newOpp["new_opportunitytype"] = new OptionSetValue(100000006); // Always Current (100000006)
					newOpp["new_basketballseason"] = targetSeason.ToEntityReference();

					if (opp.Contains("name"))
						newOpp["name"] = opp.GetAttributeValue<string>("name").Replace(startYear.ToString(), targetYear.ToString());

					string[] oppFieldsToCopy = {
						"parentaccountid", "parentcontactid", "campaignid", "new_leadsource",
						"budgetstatus", "new_pitchtype", "new_confidencelevel", "estimatedvalue"
					};
					foreach (string of in oppFieldsToCopy)
					{
						if (opp.Contains(of)) newOpp[of] = opp[of];
					}

					// Force cloned opportunities to be 1 Year long. 
					// This guarantees that if a user closes a cloned opp, the plugin aborts early and prevents an infinite loop.
					newOpp["new_pitchedcontractlength"] = new OptionSetValue(100000000);

					// Shift dates forward 
					if (opp.Contains("new_pitchdate"))
						newOpp["new_pitchdate"] = opp.GetAttributeValue<DateTime>("new_pitchdate").AddYears(i - 1);
					if (opp.Contains("estimatedclosedate"))
						newOpp["estimatedclosedate"] = opp.GetAttributeValue<DateTime>("estimatedclosedate").AddYears(i - 1);

					Guid newOppId = service.Create(newOpp);

					// Game counts of the target season, used to work out the incremental games
					// the cloned deal is entitled to.
					int targetSeasonHomeGames, targetSeasonAwayGames;
					GetSeasonGames(targetSeason.Id, service, tracingService, out targetSeasonHomeGames, out targetSeasonAwayGames);

					// C. Clone Deal
					Entity newDeal = new Entity("new_deals");
					if (baseDeal.Contains("new_name"))
						newDeal["new_name"] = baseDeal.GetAttributeValue<string>("new_name").Replace(startYear.ToString(), targetYear.ToString());

					newDeal["new_season"] = targetSeason.ToEntityReference();
					newDeal["new_originatingopportunity"] = opp.ToEntityReference();
					newDeal["new_contractyearsequence"] = i;
					newDeal["new_totalcontractyears"] = totalYears;
					newDeal["new_opportunity"] = new EntityReference("opportunity", newOppId);

					// Copy Deal Status, Type, and personnel.
					// The incremental games clause travels with the contract, so it is carried
					// forward too: it is entered once and inherited by every year of the deal.
					// Only the clause terms are copied — the amounts are NOT, because each season
					// has its own game counts and its own incremental revenue.
					string[] dealFieldsToCopy = { "new_accountid", "new_dealtype",
							  "new_partnershipassigneeemail", "new_salesperson",
							  "new_serviceperson",
							  "new_incrementalgamesclause", "new_awaygamebenefits",
							  "new_contractedhomegames", "new_contractedawaygames",
							  "new_investmentperhomegame", "new_investmentperawaygame",
							  "new_incrementalpricingmethod", "new_incrementalgamesnotes" };
					foreach (string df in dealFieldsToCopy)
					{
						if (baseDeal.Contains(df)) newDeal[df] = baseDeal[df];
					}

					// Incremental Contract Value is calculated, never copied. The rates travel with the
					// contract but the game counts belong to the target season, so the amount has to be
					// worked out here. Without this the cloned deal arrives with a clause, rates and
					// game counts but a blank contract value, which leaves Unallocated Variance at zero
					// and hides the fact that there is anything left to allocate.
					//
					// It is calculated rather than left to the form script because a clone never opens
					// a form. The field stays editable afterwards.
					SetIncrementalContractValue(newDeal, baseDeal, targetSeasonHomeGames, targetSeasonAwayGames, tracingService);

					Guid? openStatusId = GetDealStatusIdByCode("DS-1001", service, tracingService);
					if (openStatusId.HasValue)
					{
						newDeal["new_dealstatus"] = new EntityReference("new_dealstatus", openStatusId.Value);
						tracingService.Trace($"Cloned Deal for year {targetYear} will start in Open (DS-1001).");
					}
					else
					{
						tracingService.Trace($"WARNING: Could not find Deal Status DS-1001. Cloned Deal will have NULL status.");
					}

					// Evaluate Risk
					newDeal["new_revenuecertainty"] = new OptionSetValue(100000000); // Default to Guaranteed

					Guid newDealId = service.Create(newDeal);

					// D. Clone Deal Lines
					foreach (Entity line in baseLines.Entities)
					{
						Entity newLine = new Entity("new_deallines");
						newLine["new_dealid"] = new EntityReference("new_deals", newDealId);

						if (line.Contains("new_name"))
							newLine["new_name"] = line.GetAttributeValue<string>("new_name").Replace(startYear.ToString(), targetYear.ToString());

						// new_linetotaloverride MUST be copied. When a line is priced through the
						// override, leaving it behind silently drops the clone to Quantity x escalated
						// Rate, which is lower than what was contracted.
						//
						// new_incrementalrateperhomegame / new_incrementalrateperawaygame are the contract's
						// per-game rates, so they belong to the contract and travel forward.
						//
						// Deliberately NOT copied:
						//   new_incrementalrevenue  -> belongs to the season it was earned in. The next
						//                              year starts at zero until that season's game counts
						//                              are known and the amounts are entered or distributed.
						//   new_deliveredquantity   -> filled by the Deal Lines plugin on create.
						//
						// new_inventory is copied here and then re-pointed just below to the matching
						// record in the target season.
						string[] lineFieldsToCopy = { "new_inventory", "new_quantity", "new_discount", "new_notes",
													  "new_ratecard", "new_linetotaloverride",
													  "new_includeinautoproration",
													  "new_incrementalrateperhomegame", "new_incrementalrateperawaygame" };
						foreach (string lf in lineFieldsToCopy)
						{
							if (line.Contains(lf)) newLine[lf] = line[lf];
						}

						// Re-point the line at the equivalent inventory record in the target season.
						EntityReference sourceInv = line.GetAttributeValue<EntityReference>("new_inventory");
						if (sourceInv != null && inventoryForTargetSeason.ContainsKey(sourceInv.Id))
						{
							newLine["new_inventory"] = inventoryForTargetSeason[sourceInv.Id];
						}

						if (line.Contains("new_seasonid"))
						{
							newLine["new_seasonid"] = targetSeason.ToEntityReference();
						}

						// Delivered Quantity is deliberately left unset. The pre-operation handler in
						// Pl.DealLines.InventoryManagement fills it on create with the rule that applies
						// everywhere: the season's home games when the Inventory is a game-based asset,
						// the quantity otherwise. Setting it here would bypass that check and hand a
						// season's worth of games to assets that are not delivered per game.

						// Escalate the input Rate (Rate Charged)
						if (line.Contains("new_rate"))
						{
							decimal baseValue = ((Money)line["new_rate"]).Value;

							// Rounding to the nearest whole dollar amount (0 decimal places).
							// MidpointRounding.AwayFromZero ensures standard commercial rounding (e.g., .5 rounds up to the next dollar).
							decimal escalatedValue = Math.Round(baseValue * currentMultiplier, 0, MidpointRounding.AwayFromZero);

							newLine["new_rate"] = new Money(escalatedValue);
						}

						service.Create(newLine);
					}
				}
				tracingService.Trace("Multi-Year Cloning (with Opportunities) Completed!");
			}
			catch (Exception ex)
			{
				tracingService.Trace($"EXCEPTION: {ex.Message}");
				// Si lanzamos la excepcion original de arriba, queremos que el usuario la lea limpia, sin el "Error generating..." extra.
				if (ex is InvalidPluginExecutionException)
				{
					throw;
				}
				throw new InvalidPluginExecutionException($"Error generating future deals: {ex.Message}");
			}
		}

		/// <summary>
		/// Finds the season record for a given year and season family (the suffix taken from the
		/// source season's name, e.g. "Storm"). Returns null when it does not exist.
		/// </summary>
		private Entity FindSeasonForYear(int year, string seasonSuffix, IOrganizationService service)
		{
			QueryExpression query = new QueryExpression("new_season") { ColumnSet = new ColumnSet("new_seasonid") };
			query.Criteria.AddCondition("new_seasonyear", ConditionOperator.Equal, year);
			query.Criteria.AddCondition("new_name", ConditionOperator.Like, $"%{seasonSuffix}%");
			query.TopCount = 1;

			return service.RetrieveMultiple(query).Entities.FirstOrDefault();
		}

		/// <summary>
		/// Builds a map from each source line's inventory record to the equivalent record in the
		/// target season, matched on the product the inventory represents.
		///
		/// Inventory is season-scoped: a 2027 deal line has to consume 2027 availability. Copying the
		/// source reference across would have every year of a multi-year deal drawing down the same
		/// 2026 stock, so the numbers would look fine on each deal and be wrong in aggregate.
		///
		/// Throws when the target season is missing inventory for an asset the contract carries. That
		/// is deliberate: creating the deal anyway would silently produce the double counting this
		/// method exists to prevent, and the fix (load next season's inventory) belongs upstream.
		/// </summary>
		private Dictionary<Guid, EntityReference> MapInventoryToTargetSeason(EntityCollection baseLines,
			Guid targetSeasonId, int targetYear, IOrganizationService service, ITracingService tracing)
		{
			var map = new Dictionary<Guid, EntityReference>();

			var sourceInventoryIds = baseLines.Entities
				.Select(l => l.GetAttributeValue<EntityReference>("new_inventory"))
				.Where(r => r != null)
				.Select(r => r.Id)
				.Distinct()
				.ToList();

			if (sourceInventoryIds.Count == 0) return map;

			// Product, division AND collection identify an inventory row within a season. Product
			// alone is not enough: the same product is sold under more than one collection at
			// different rates (for example "Banner Ad" under Digital - App and under
			// Digital - Website), and keying on product alone would point both deal lines at the
			// same target row, so both would draw down the same availability.
			QueryExpression sourceQuery = new QueryExpression("new_inventory")
			{
				ColumnSet = new ColumnSet("new_productid", "new_division", "new_collection", "new_name")
			};
			sourceQuery.Criteria.AddCondition("new_inventoryid", ConditionOperator.In, sourceInventoryIds.Cast<object>().ToArray());
			List<Entity> sourceInventory = service.RetrieveMultiple(sourceQuery).Entities.ToList();

			var productIds = sourceInventory
				.Select(inv => inv.GetAttributeValue<EntityReference>("new_productid"))
				.Where(r => r != null)
				.Select(r => r.Id)
				.Distinct()
				.ToList();

			// The same product/collection combinations, in the target season.
			var targetInventoryByKey = new Dictionary<string, EntityReference>();
			if (productIds.Count > 0)
			{
				QueryExpression targetQuery = new QueryExpression("new_inventory")
				{
					ColumnSet = new ColumnSet("new_productid", "new_division", "new_collection")
				};
				targetQuery.Criteria.AddCondition("new_seasonid", ConditionOperator.Equal, targetSeasonId);
				targetQuery.Criteria.AddCondition("new_productid", ConditionOperator.In, productIds.Cast<object>().ToArray());

				foreach (Entity inv in service.RetrieveMultiple(targetQuery).Entities)
				{
					string key = InventoryKey(inv);
					if (!targetInventoryByKey.ContainsKey(key))
						targetInventoryByKey[key] = inv.ToEntityReference();
				}
			}

			var missing = new List<string>();

			foreach (Entity inv in sourceInventory)
			{
				EntityReference product = inv.GetAttributeValue<EntityReference>("new_productid");
				EntityReference division = inv.GetAttributeValue<EntityReference>("new_division");
				EntityReference collection = inv.GetAttributeValue<EntityReference>("new_collection");
				string label = inv.GetAttributeValue<string>("new_name") ?? "(unnamed inventory item)";

				string category = string.Join(" / ", new[] { division?.Name, collection?.Name }.Where(x => !string.IsNullOrEmpty(x)));
				if (!string.IsNullOrEmpty(category))
					label += $" ({category})";

				if (product == null)
				{
					missing.Add(label + " - no product on the inventory record");
					continue;
				}

				string key = InventoryKey(inv);

				if (!targetInventoryByKey.ContainsKey(key))
				{
					missing.Add(label);
					continue;
				}

				map[inv.Id] = targetInventoryByKey[key];
			}

			if (missing.Count > 0)
			{
				string list = string.Join(", ", missing.Take(10));
				string more = missing.Count > 10 ? $" and {missing.Count - 10} more" : string.Empty;

				throw new InvalidPluginExecutionException(
					$"There is no {targetYear} inventory for {missing.Count} asset(s) on this deal: {list}{more}. " +
					$"A {targetYear} deal has to draw on {targetYear} inventory, otherwise the same stock would be " +
					"counted again in every year of the contract. Create the missing inventory for that season and " +
					"close the opportunity again. Nothing has been created.");
			}

			tracing.Trace($"Inventory mapped to the {targetYear} season for {map.Count} asset(s).");
			return map;
		}

		/// <summary>
		/// What identifies an inventory row within a season: its product, its division and its
		/// collection. Season is already the scope of the lookup, so it is not part of the key.
		///
		/// All three categories are meaningful. Two rows for the same product under different
		/// collections are different sellable items at different rates, and the same is true of
		/// division. Both sides of the match have to be built the same way, so it lives in one place.
		/// </summary>
		private string InventoryKey(Entity inventory)
		{
			return string.Join("|",
				LookupKeyPart(inventory, "new_productid"),
				LookupKeyPart(inventory, "new_division"),
				LookupKeyPart(inventory, "new_collection"));
		}

		private string LookupKeyPart(Entity record, string attribute)
		{
			EntityReference reference = record.GetAttributeValue<EntityReference>(attribute);
			return reference != null ? reference.Id.ToString() : "none";
		}

		/// <summary>
		/// Reads Home Games and Away Games from a Season record. Missing values come back as 0,
		/// which makes the incremental game count zero rather than negative.
		/// </summary>
		private void GetSeasonGames(Guid seasonId, IOrganizationService service, ITracingService tracing,
			out int homeGames, out int awayGames)
		{
			homeGames = 0;
			awayGames = 0;

			try
			{
				Entity season = service.Retrieve("new_season", seasonId, new ColumnSet("new_homegames", "new_awaygames"));
				homeGames = season.GetAttributeValue<int>("new_homegames");
				awayGames = season.GetAttributeValue<int>("new_awaygames");
			}
			catch (Exception ex)
			{
				tracing.Trace($"GetSeasonGames skipped: {ex.Message}");
			}
		}

		/// <summary>
		/// Sets Incremental Contract Value on the cloned deal, by pricing method:
		///
		///  - Per Game Rate    calculated for the target season:
		///                       (Investment per Home Game x Max(0, season home - contracted home))
		///                     + (Investment per Away Game x Max(0, season away - contracted away))
		///  - Flat Amount      copied from the source deal. A flat figure is a term of the contract,
		///                     not something derived from how long the season turned out to be, so it
		///                     travels forward unchanged.
		///  - Itemized by Line left blank. The amount is the sum of what the lines carry, and the
		///                     lines bring their own rates across.
		///
		/// Does nothing unless the clause is In or Opt-In.
		/// </summary>
		private void SetIncrementalContractValue(Entity newDeal, Entity baseDeal,
			int seasonHomeGames, int seasonAwayGames, ITracingService tracing)
		{
			const int CLAUSE_IN = 100000000;
			const int CLAUSE_OPT_IN = 100000001;
			const int METHOD_PER_GAME_RATE = 100000000;
			const int METHOD_FLAT_AMOUNT = 100000002;

			OptionSetValue clause = baseDeal.GetAttributeValue<OptionSetValue>("new_incrementalgamesclause");
			if (clause == null || (clause.Value != CLAUSE_IN && clause.Value != CLAUSE_OPT_IN))
				return;

			OptionSetValue method = baseDeal.GetAttributeValue<OptionSetValue>("new_incrementalpricingmethod");
			if (method == null)
			{
				tracing.Trace("Incremental Contract Value left blank: no pricing method on the source deal.");
				return;
			}

			if (method.Value == METHOD_FLAT_AMOUNT)
			{
				Money flat = baseDeal.GetAttributeValue<Money>("new_incrementalcontractvalue");
				decimal flatValue = flat != null ? flat.Value : 0m;
				newDeal["new_incrementalcontractvalue"] = new Money(flatValue);
				tracing.Trace($"Incremental Contract Value carried forward as a flat amount: {flatValue}");
				return;
			}

			if (method.Value != METHOD_PER_GAME_RATE)
			{
				tracing.Trace("Incremental Contract Value left blank: the amount comes from the lines (Itemized by Line).");
				return;
			}

			int contractedHome = baseDeal.GetAttributeValue<int>("new_contractedhomegames");
			int contractedAway = baseDeal.GetAttributeValue<int>("new_contractedawaygames");
			bool awayBenefits = baseDeal.GetAttributeValue<bool>("new_awaygamebenefits");

			int incrementalHome = seasonHomeGames > contractedHome ? seasonHomeGames - contractedHome : 0;
			int incrementalAway = awayBenefits && seasonAwayGames > contractedAway ? seasonAwayGames - contractedAway : 0;

			Money homeRateMoney = baseDeal.GetAttributeValue<Money>("new_investmentperhomegame");
			Money awayRateMoney = baseDeal.GetAttributeValue<Money>("new_investmentperawaygame");
			decimal homeRate = homeRateMoney != null ? homeRateMoney.Value : 0m;
			decimal awayRate = awayRateMoney != null ? awayRateMoney.Value : 0m;

			decimal contractValue = (homeRate * incrementalHome) + (awayRate * incrementalAway);
			newDeal["new_incrementalcontractvalue"] = new Money(contractValue);

			tracing.Trace($"Incremental Contract Value -> home {homeRate} x {incrementalHome} + " +
						  $"away {awayRate} x {incrementalAway} = {contractValue}");
		}

		private Guid? GetDealStatusIdByCode(string code, IOrganizationService service, ITracingService tracing)
		{
			QueryExpression q = new QueryExpression("new_dealstatus") { ColumnSet = new ColumnSet("new_dealstatusid") };
			q.Criteria.AddCondition("new_code", ConditionOperator.Equal, code);
			q.TopCount = 1;
			var results = service.RetrieveMultiple(q);
			if (results.Entities.Count == 0)
			{
				tracing.Trace($"No Deal Status found with code '{code}'.");
				return null;
			}
			return results.Entities[0].Id;
		}
	}
}