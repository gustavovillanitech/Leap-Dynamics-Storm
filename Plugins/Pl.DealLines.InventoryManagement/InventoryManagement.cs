using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;

namespace Pl.DealLines.InventoryManagement
{
	public class UnifiedDealInventoryPlugin : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			if (!context.InputParameters.Contains("Target"))
				return;
			Entity target = null;
			// 1. Check whether the target is an entity (Create / Update)
			if (context.InputParameters["Target"] is Entity)
			{
				target = (Entity)context.InputParameters["Target"];
			}
			// 2. Check whether the Target is an EntityReference (Delete)
			else if (context.InputParameters["Target"] is EntityReference)
			{
				EntityReference targetRef = (EntityReference)context.InputParameters["Target"];
				// We create a dummy entity with the ID so that the rest of the code doesn't fail
				target = new Entity(targetRef.LogicalName) { Id = targetRef.Id };
			}
			else
			{
				return; // If it is neither an Entity nor an EntityReference, we exit.
			}

			string entityName = context.PrimaryEntityName;
			string messageName = context.MessageName.ToLower();
			int stage = context.Stage;

			try
			{
				// ==============================================================================
				// ENTITY 1: DEAL LINE (new_deallines)
				// ==============================================================================
				if (entityName == "new_deallines")
				{
					Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : new Entity(entityName);

					// 1. PRE-OPERATION: Calculate Inline Math
					if (stage == 20 && (messageName == "create" || messageName == "update"))
					{
						CalculateDealLineMetrics(target, preImage, service, tracingService);
						// Mirror the descriptions from the selected Inventory onto the Deal Line and,
						// on create, apply the Inventory-driven defaults (Delivered Quantity and
						// Include in Auto-Proration).
						// Covers lines created outside the form (Deal Line Builder canvas app, multi-year clone).
						ApplyInventoryDefaultsToLine(target, preImage, messageName, service, tracingService);
					}

					// 2. POST-OPERATION: Rollup to Deal & Update Inventory Deltas
					if (stage == 40 && (messageName == "create" || messageName == "update" || messageName == "delete"))
					{
						Guid dealId = GetLookupId(target, preImage, "new_dealid");

						if (dealId != Guid.Empty)
						{
							RollupTotalsToParentDeal(dealId, service, tracingService);
							UpdateInventoryDeltaFromLine(target, preImage, messageName, dealId, service, tracingService);
						}
					}
				}
				// ==============================================================================
				// ENTITY 2: DEAL (new_deals)
				// ==============================================================================
				else if (entityName == "new_deals")
				{
					// 3. POST-OPERATION: Handle Status Changes (Moving Pitched <-> Sold)
					if (stage == 40 && messageName == "update")
					{
						Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : new Entity(entityName);
						HandleDealStatusChange(target, preImage, service, tracingService);
					}
				}
			}
			catch (Exception ex)
			{
				tracingService.Trace($"Plugin Exception: {ex.Message}");
				throw new InvalidPluginExecutionException($"Plugin Error: {ex.Message}", ex);
			}
		}

		#region 1. Deal Line Math (Pre-Operation)

		private void CalculateDealLineMetrics(Entity target, Entity preImage, IOrganizationService service, ITracingService tracingService)
		{
			tracingService.Trace("Calculating Deal Line Financial Metrics...");

			decimal quantity = GetDecimalValue(target, preImage, "new_quantity");
			decimal rateCharged = GetMoneyValue(target, preImage, "new_rate");
			decimal rateCard = GetMoneyValue(target, preImage, "new_ratecard");

			// Line Total Override: if the user entered a manual total, it wins over Quantity x Rate.
			// (Used for rounded amounts, e.g. a deal meant to total exactly $50,000 that qty x rate
			//  cannot produce with a 2-decimal rate.) Null override => normal Quantity x Rate.
			decimal? overrideTotal = GetMoneyNullable(target, preImage, "new_linetotaloverride");
			decimal baseTotal = overrideTotal.HasValue ? overrideTotal.Value : quantity * rateCharged;

			// Incremental Revenue: revenue attributed to the additional games of the season.
			// It is added on top of the base, never instead of it.
			//
			// Calculated here rather than on the form, because the line is edited inline in the
			// Deal Lines subgrid as often as it is opened, and an editable grid never runs form
			// scripts. Anything the grid can change has to be maintained server-side.
			//
			// IMPORTANT: new_deliveredquantity is deliberately NOT used in any calculation here.
			// new_quantity is what the contracted price covers and drives the money; delivered
			// quantity is what the partner receives and only feeds activation and inventory
			// availability. Using delivered quantity in this formula would charge the additional
			// games twice: once through the quantity and again through the incremental revenue.
			// Precedence, in this order:
			//  1. A rate was touched -> recalculate from the rates. The plugin wins over anything the
			//     form script may have put in the field, so the rate stays the single source of truth.
			//  2. Otherwise an amount was supplied -> take it as given. This is how the distribution
			//     Custom API (and a Flat Amount / Itemized by Line deal) states the figure outright.
			//  3. Otherwise -> leave whatever the line already had, so a partial update that touches
			//     neither does not wipe it.
			decimal incrementalRevenue;
			if (target.Contains("new_incrementalrateperhomegame") || target.Contains("new_incrementalrateperawaygame"))
			{
				incrementalRevenue = CalculateIncrementalRevenue(target, preImage, service, tracingService);
				target["new_incrementalrevenue"] = new Money(incrementalRevenue);
			}
			else
			{
				incrementalRevenue = GetMoneyValue(target, preImage, "new_incrementalrevenue");
			}

			decimal total = baseTotal + incrementalRevenue;
			decimal listRate = quantity * rateCard;
			decimal gainLoss = total - listRate;

			decimal yieldValue = 0m;
			if (listRate != 0)
			{
				yieldValue = total / listRate;
			}

			target["new_total"] = new Money(total);
			target["new_listrate"] = new Money(listRate);
			target["new_gainloss"] = new Money(gainLoss);
			target["new_yield"] = yieldValue;

			tracingService.Trace($"Metrics Calculated -> Base: {baseTotal}, Incremental: {incrementalRevenue}, Total: {total}, Yield: {yieldValue}");
		}

		/// <summary>
		/// Incremental Revenue for one deal line:
		///
		///   (Incremental Rate per Home Game x Deal.Incremental Home Games)
		/// + (Incremental Rate per Away Game x Deal.Incremental Away Games)
		///
		/// The game counts live on the parent Deal as formula columns, so they are read back from
		/// the Deal rather than stored on the line: the line always reflects the season the deal
		/// belongs to, and correcting Contracted Home Games on the Deal never leaves the lines
		/// disagreeing with it.
		///
		/// Returns 0 when neither rate is set, so the extra Retrieve only happens on the lines that
		/// actually carry an incremental charge. Also returns 0 when the Deal has no incremental
		/// clause, which keeps a stray rate on a line from inventing revenue.
		/// </summary>
		private decimal CalculateIncrementalRevenue(Entity target, Entity preImage, IOrganizationService service, ITracingService tracing)
		{
			const int CLAUSE_IN = 100000000;
			const int CLAUSE_OPT_IN = 100000001;

			decimal homeRate = GetMoneyValue(target, preImage, "new_incrementalrateperhomegame");
			decimal awayRate = GetMoneyValue(target, preImage, "new_incrementalrateperawaygame");

			if (homeRate == 0m && awayRate == 0m)
				return 0m;

			Guid dealId = GetLookupId(target, preImage, "new_dealid");
			if (dealId == Guid.Empty)
			{
				tracing.Trace("CalculateIncrementalRevenue: no deal on the line, returning 0.");
				return 0m;
			}

			try
			{
				Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet(
					"new_incrementalgamesclause",
					"new_awaygamebenefits",
					"new_incrementalhomegames",
					"new_incrementalawaygames"));

				OptionSetValue clause = deal.GetAttributeValue<OptionSetValue>("new_incrementalgamesclause");
				bool hasClause = clause != null && (clause.Value == CLAUSE_IN || clause.Value == CLAUSE_OPT_IN);
				if (!hasClause)
				{
					tracing.Trace("CalculateIncrementalRevenue: deal has no incremental clause, returning 0.");
					return 0m;
				}

				// Read through GetNumericValue, not GetAttributeValue<int>. Incremental Home/Away
				// Games are FORMULA COLUMNS, and Dataverse stores a numeric Power Fx formula as a
				// Decimal even when the values are whole games. GetAttributeValue<int> does not
				// convert: on a decimal attribute it silently returns 0, which multiplied by the
				// per-game rate gives an incremental revenue of exactly zero and no error anywhere.
				decimal homeGames = GetNumericValue(deal, "new_incrementalhomegames");
				decimal awayGames = deal.GetAttributeValue<bool>("new_awaygamebenefits")
					? GetNumericValue(deal, "new_incrementalawaygames")
					: 0m;

				decimal revenue = (homeRate * homeGames) + (awayRate * awayGames);

				tracing.Trace($"Incremental Revenue -> home {homeRate} x {homeGames} + away {awayRate} x {awayGames} = {revenue}");
				return revenue;
			}
			catch (Exception ex)
			{
				tracing.Trace($"CalculateIncrementalRevenue failed, returning 0: {ex.Message}");
				return 0m;
			}
		}

		/// <summary>
		/// Applies everything the selected Inventory drives on a Deal Line:
		///  - always: mirrors the external/internal descriptions
		///    (external -> new_description, internal -> new_internaldescription)
		///  - on create only: defaults Include in Auto-Proration from the Inventory's
		///    Default Auto-Proration, and Delivered Quantity from the season's home games
		///    when the Inventory is a game-based asset (otherwise from the quantity).
		/// Only runs when the Inventory lookup is present on the Target (set on create, changed on
		/// update, or set by the Deal Line Builder / multi-year clone) to avoid an unnecessary
		/// Retrieve on unrelated edits.
		/// </summary>
		private void ApplyInventoryDefaultsToLine(Entity target, Entity preImage, string messageName,
			IOrganizationService service, ITracingService tracing)
		{
			if (!(target.Contains("new_inventory") && target["new_inventory"] is EntityReference invRef))
				return;

			try
			{
				Entity inv = service.Retrieve("new_inventory", invRef.Id, new ColumnSet(
					"new_description",
					"new_internaldescription",
					"new_gamebasedasset",
					"new_defaultautoproration"));

				target["new_description"] = inv.GetAttributeValue<string>("new_description");
				target["new_internaldescription"] = inv.GetAttributeValue<string>("new_internaldescription");
				tracing.Trace("Descriptions mirrored from inventory onto the deal line.");

				// The two defaults below run on create only. On update the user (or the clone) has
				// already decided, and overwriting would fight them.
				//
				// They live here rather than in the form script because most deal lines are never
				// created on the form: the Deal Line Builder canvas app and CloneMultiYearDeals both
				// write straight to the table, and a form OnChange never fires for them.
				if (messageName != "create")
					return;

				if (!target.Contains("new_includeinautoproration"))
				{
					bool defaultProration = inv.GetAttributeValue<bool>("new_defaultautoproration");
					target["new_includeinautoproration"] = defaultProration;
					tracing.Trace($"Include in Auto-Proration defaulted from inventory: {defaultProration}");
				}

				if (!target.Contains("new_deliveredquantity"))
				{
					bool gameBased = inv.GetAttributeValue<bool>("new_gamebasedasset");

					if (gameBased)
					{
						// A game-based asset is delivered once per home game of the season, whatever
						// the contract says the quantity is. That gap between delivered and contracted
						// is the whole point of the incremental games design.
						Guid dealId = GetLookupId(target, preImage, "new_dealid");
						int seasonHomeGames = GetSeasonHomeGames(dealId, service, tracing);
						if (seasonHomeGames > 0)
						{
							target["new_deliveredquantity"] = seasonHomeGames;
							tracing.Trace($"Delivered Quantity defaulted from season home games: {seasonHomeGames}");
						}
					}
					else
					{
						decimal quantity = GetDecimalValue(target, preImage, "new_quantity");
						target["new_deliveredquantity"] = (int)quantity;
						tracing.Trace($"Delivered Quantity defaulted from quantity: {(int)quantity}");
					}
				}
			}
			catch (Exception ex)
			{
				tracing.Trace($"ApplyInventoryDefaultsToLine skipped: {ex.Message}");
			}
		}

		/// <summary>
		/// Home games of the season the deal belongs to. Returns 0 when the deal has no season,
		/// which leaves Delivered Quantity untouched rather than writing a misleading zero.
		/// </summary>
		private int GetSeasonHomeGames(Guid dealId, IOrganizationService service, ITracingService tracing)
		{
			if (dealId == Guid.Empty) return 0;

			try
			{
				Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet("new_season"));
				EntityReference seasonRef = deal.GetAttributeValue<EntityReference>("new_season");
				if (seasonRef == null) return 0;

				Entity season = service.Retrieve("new_season", seasonRef.Id, new ColumnSet("new_homegames"));
				return (int)GetNumericValue(season, "new_homegames");
			}
			catch (Exception ex)
			{
				tracing.Trace($"GetSeasonHomeGames skipped: {ex.Message}");
				return 0;
			}
		}

		#endregion

		#region 2. Rollup to Parent Deal (Post-Operation)

		private void RollupTotalsToParentDeal(Guid dealId, IOrganizationService service, ITracingService tracingService)
		{
			tracingService.Trace($"Rolling up Net Totals to Parent Deal: {dealId}");

			string fetchXml = $@"
                        <fetch aggregate='true'>
                            <entity name='new_deallines'>
                            <attribute name='new_total' alias='sum_total' aggregate='sum' />
                            <attribute name='new_incrementalrevenue' alias='sum_incremental' aggregate='sum' />
                            <filter>
                                <condition attribute='new_dealid' operator='eq' value='{dealId}' />
                            </filter>
                            </entity>
                        </fetch>";

			EntityCollection result = service.RetrieveMultiple(new FetchExpression(fetchXml));
			decimal netTotal = 0m;
			decimal incrementalAllocated = 0m;

			if (result.Entities.Count > 0)
			{
				netTotal = GetAggregatedMoney(result.Entities[0], "sum_total");
				incrementalAllocated = GetAggregatedMoney(result.Entities[0], "sum_incremental");
			}

			// Pass dealId so calc can read the frozen percent from the Deal first
			decimal maxActivationSpend = CalculateMaxActivationSpend(dealId, netTotal, service, tracingService);

			// Incremental game revenue is maintained here, synchronously, rather than through a
			// Dataverse rollup field. Rollups recalculate on a schedule (hourly by default), which
			// would leave the figure stale exactly while a seller is entering amounts line by line.
			// This also keeps it consistent with new_total, which is already maintained this way.
			decimal contractValue = GetDealIncrementalContractValue(dealId, service, tracingService);

			// With no contract value there is nothing to reconcile against, so the variance is zero
			// rather than the negative of whatever the lines carry. That is the Itemized by Line case:
			// the amount is whatever the lines add up to, and there is no separate agreed total.
			// A contract value that IS set and is smaller than the lines still reports negative, which
			// is the signal that the deal is over-allocated.
			decimal variance = contractValue == 0m ? 0m : contractValue - incrementalAllocated;

			Entity dealToUpdate = new Entity("new_deals", dealId);
			dealToUpdate["new_total"] = new Money(netTotal);
			dealToUpdate["new_maxactivationspend"] = new Money(maxActivationSpend);
			dealToUpdate["new_totalincrementalrevenue"] = new Money(incrementalAllocated);
			dealToUpdate["new_unallocatedvariance"] = new Money(variance);
			service.Update(dealToUpdate);

			tracingService.Trace($"Deal {dealId} updated -> Total: {netTotal}, MaxActivationSpend: {maxActivationSpend}, " +
								 $"IncrementalAllocated: {incrementalAllocated}, Variance: {variance}");
		}

		/// <summary>
		/// Reads a summed Money value out of a FetchXML aggregate result.
		/// Returns 0 when the alias is missing or the aggregate produced no rows.
		/// </summary>
		private decimal GetAggregatedMoney(Entity aggregateRow, string alias)
		{
			if (!aggregateRow.Contains(alias))
				return 0m;

			AliasedValue aliased = aggregateRow[alias] as AliasedValue;
			if (aliased == null || aliased.Value == null)
				return 0m;

			Money money = aliased.Value as Money;
			return money != null ? money.Value : 0m;
		}

		/// <summary>
		/// Reads Incremental Contract Value from the parent Deal so the unallocated
		/// variance can be written in the same pass as the allocated total.
		/// Returns 0 when the Deal cannot be read or the field is empty.
		/// </summary>
		private decimal GetDealIncrementalContractValue(Guid dealId, IOrganizationService service, ITracingService tracing)
		{
			try
			{
				Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet("new_incrementalcontractvalue"));
				Money contractValue = deal.GetAttributeValue<Money>("new_incrementalcontractvalue");
				return contractValue != null ? contractValue.Value : 0m;
			}
			catch (Exception ex)
			{
				tracing.Trace($"GetDealIncrementalContractValue skipped: {ex.Message}");
				return 0m;
			}
		}

		/// <summary>
		/// Calculates Max Activation Spend for a Deal.
		/// Priority:
		///   1. If the Deal has a frozen percent (new_maxactivationspendpercent), use it.
		///   2. Otherwise, fall back to the global percent in new_DealConfiguration.
		/// Returns 0 if no source is available or total is 0.
		/// </summary>
		private decimal CalculateMaxActivationSpend(Guid dealId, decimal dealTotal, IOrganizationService service, ITracingService tracingService)
		{
			if (dealTotal == 0m)
			{
				tracingService.Trace("Deal total is 0, MaxActivationSpend = 0.");
				return 0m;
			}

			// 1. Try frozen percent on the Deal first
			Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet("new_maxactivationspendpercent"));
			decimal? percent = null;

			if (deal.Contains("new_maxactivationspendpercent") && deal["new_maxactivationspendpercent"] != null)
			{
				percent = Convert.ToDecimal(deal["new_maxactivationspendpercent"]);
				tracingService.Trace($"Using FROZEN percent from Deal: {percent}%");
			}
			else
			{
				// 2. Fall back to global config
				QueryExpression configQuery = new QueryExpression("new_dealconfiguration")
				{
					ColumnSet = new ColumnSet("new_maxactivationspendpercent"),
					TopCount = 1
				};

				EntityCollection configs = service.RetrieveMultiple(configQuery);

				if (configs.Entities.Count == 0)
				{
					tracingService.Trace("WARNING: No Deal Configuration record found. MaxActivationSpend will not be calculated.");
					return 0m;
				}

				Entity config = configs.Entities[0];

				if (!config.Contains("new_maxactivationspendpercent") || config["new_maxactivationspendpercent"] == null)
				{
					tracingService.Trace("WARNING: Deal Configuration exists but new_maxactivationspendpercent is null.");
					return 0m;
				}

				percent = Convert.ToDecimal(config["new_maxactivationspendpercent"]);
				tracingService.Trace($"Using GLOBAL percent from DealConfiguration: {percent}%");
			}

			decimal calcResult = Math.Round(dealTotal * (percent.Value / 100m), 2, MidpointRounding.AwayFromZero);
			tracingService.Trace($"MaxActivationSpend calc -> Total: {dealTotal} × {percent}% = {calcResult}");
			return calcResult;
		}

		#endregion

		#region 3. Inventory Allocation Logic (Post-Operation)

		private void UpdateInventoryDeltaFromLine(Entity target, Entity preImage, string messageName, Guid dealId, IOrganizationService service, ITracingService tracingService)
		{
			tracingService.Trace("Calculating Inventory Delta from Line change...");

			Guid inventoryId = GetLookupId(target, preImage, "new_inventory");
			if (inventoryId == Guid.Empty) return;

			decimal oldQty = (messageName == "delete") ? GetDecimalValue(preImage, preImage, "new_quantity") : 0m;
			decimal newQty = (messageName == "delete") ? 0m : GetDecimalValue(target, preImage, "new_quantity");

			if (messageName == "update")
			{
				oldQty = GetDecimalValue(preImage, preImage, "new_quantity");
			}

			decimal deltaQty = newQty - oldQty;
			if (deltaQty == 0) return;

			Entity parentDeal = service.Retrieve("new_deals", dealId, new ColumnSet("new_dealstatus"));
			string dealCode = GetStatusCodeFromLookup(parentDeal, "new_dealstatus", service);

			decimal pitchedDelta = 0m;
			decimal soldDelta = 0m;

			if (dealCode == "DS-1008") // Closed Won
			{
				soldDelta = deltaQty;
			}
			else if (dealCode != "DS-1009") // Open (Not won, not lost)
			{
				pitchedDelta = deltaQty;
			}

			UpdateInventoryBuckets(inventoryId, pitchedDelta, soldDelta, service, tracingService);
		}

		private void HandleDealStatusChange(Entity target, Entity preImage, IOrganizationService service, ITracingService tracingService)
		{
			if (!target.Contains("new_dealstatus")) return;

			EntityReference oldStatusRef = preImage.Contains("new_dealstatus") ? preImage.GetAttributeValue<EntityReference>("new_dealstatus") : null;
			EntityReference newStatusRef = target.GetAttributeValue<EntityReference>("new_dealstatus");

			string oldCode = oldStatusRef != null ? GetStatusCodeFromLookup(oldStatusRef, service) : "";
			string newCode = newStatusRef != null ? GetStatusCodeFromLookup(newStatusRef, service) : "";

			if (oldCode == newCode) return;

			tracingService.Trace($"Deal Status Code changed from {oldCode} to {newCode}");

			QueryExpression query = new QueryExpression("new_deallines")
			{
				ColumnSet = new ColumnSet("new_inventory", "new_quantity"),
				Criteria = new FilterExpression
				{
					Conditions = { new ConditionExpression("new_dealid", ConditionOperator.Equal, target.Id) }
				}
			};

			tracingService.Trace("Retrieving Deal Lines...");
			EntityCollection dealLines = service.RetrieveMultiple(query);
			tracingService.Trace($"Deal Lines found: {dealLines.Entities.Count}");

			foreach (Entity line in dealLines.Entities)
			{
				if (!line.Contains("new_inventory") || !line.Contains("new_quantity")) continue;

				tracingService.Trace($"Processing Deal Line ID: {line.Id}");

				// Read Lookup
				tracingService.Trace("Getting Inventory ID...");
				Guid inventoryId = line.GetAttributeValue<EntityReference>("new_inventory").Id;

				// Safe reading of quantity
				tracingService.Trace("Getting Quantity from Deal Line...");
				decimal qty = GetDecimalValue(line, line, "new_quantity");

				decimal pitchedDelta = 0m;
				decimal soldDelta = 0m;

				if (newCode == "DS-1008" && oldCode != "DS-1009")
				{
					pitchedDelta = -qty;
					soldDelta = qty;
				}
				else if (newCode == "DS-1009" && oldCode != "DS-1008")
				{
					pitchedDelta = -qty;
				}
				else if (oldCode == "DS-1008" && newCode != "DS-1009")
				{
					soldDelta = -qty;
					pitchedDelta = qty;
				}
				else if (oldCode == "DS-1009" && newCode != "DS-1008")
				{
					pitchedDelta = qty;
				}

				tracingService.Trace($"Calling UpdateInventoryBuckets for inventory: {inventoryId}");
				UpdateInventoryBuckets(inventoryId, pitchedDelta, soldDelta, service, tracingService);
			}
		}

		private void UpdateInventoryBuckets(Guid inventoryId, decimal pitchedDelta, decimal soldDelta, IOrganizationService service, ITracingService tracingService)
		{
			if (pitchedDelta == 0 && soldDelta == 0) return;

			tracingService.Trace("Retrieving Inventory record...");
			Entity inventory = service.Retrieve("new_inventory", inventoryId, new ColumnSet("new_quantity", "new_pitched", "new_sold"));

			tracingService.Trace("Calculating new quantities...");
			decimal baseQty = GetDecimalValue(inventory, inventory, "new_quantity");
			decimal currentPitched = GetDecimalValue(inventory, inventory, "new_pitched");
			decimal currentSold = GetDecimalValue(inventory, inventory, "new_sold");

			decimal newPitched = currentPitched + pitchedDelta;
			decimal newSold = currentSold + soldDelta;
			decimal newAllocated = newPitched + newSold;
			decimal newUnsold = baseQty - newSold;

			Entity inventoryUpdate = new Entity("new_inventory", inventoryId);

			// Note: If fields in CRM were not deleted and recreated as Decimals, this service.Update will crash
			inventoryUpdate["new_pitched"] = newPitched;
			inventoryUpdate["new_sold"] = newSold;
			inventoryUpdate["new_allocated"] = newAllocated;
			inventoryUpdate["new_unsold"] = newUnsold;

			tracingService.Trace($"Updating inventory in Dynamics -> Pitched: {newPitched}, Sold: {newSold}");
			service.Update(inventoryUpdate);
			tracingService.Trace("Inventory updated successfully.");
		}

		#endregion

		#region Helper Methods

		private decimal GetDecimalValue(Entity target, Entity preImage, string attributeName)
		{
			object value = null;

			if (target.Contains(attributeName))
			{
				value = target[attributeName];
			}
			else if (preImage.Contains(attributeName))
			{
				value = preImage[attributeName];
			}

			if (value == null) return 0m;

			try
			{
				return Convert.ToDecimal(value);
			}
			catch
			{
				return 0m;
			}
		}

		private decimal GetMoneyValue(Entity target, Entity preImage, string attributeName)
		{
			if (target.Contains(attributeName))
				return target.GetAttributeValue<Money>(attributeName)?.Value ?? 0m;
			if (preImage.Contains(attributeName))
				return preImage.GetAttributeValue<Money>(attributeName)?.Value ?? 0m;
			return 0m;
		}

		// Returns the Money value, or null when the field is absent OR explicitly cleared.
		// Distinguishing null from 0 matters for the Line Total Override (null => no override).
		// If the Target contains the attribute we honor it (including an explicit clear to null);
		// otherwise we fall back to the PreImage so an existing override survives edits to other fields.
		private decimal? GetMoneyNullable(Entity target, Entity preImage, string attributeName)
		{
			if (target.Contains(attributeName))
				return target.GetAttributeValue<Money>(attributeName)?.Value;
			if (preImage.Contains(attributeName))
				return preImage.GetAttributeValue<Money>(attributeName)?.Value;
			return null;
		}

		/// <summary>
		/// Reads a numeric attribute whatever CLR type Dataverse happens to store it as.
		///
		/// This exists because of formula columns. A Power Fx formula that returns a number is
		/// stored as a Decimal even when every value it produces is a whole number, so
		/// GetAttributeValue&lt;int&gt; on one returns 0 rather than the value or an exception —
		/// a silent zero that propagates into whatever it multiplies. Anything read from a formula
		/// column has to come through here.
		/// </summary>
		private static decimal GetNumericValue(Entity entity, string attributeName)
		{
			if (entity == null || !entity.Contains(attributeName) || entity[attributeName] == null)
				return 0m;

			object value = entity[attributeName];

			if (value is int) return (int)value;
			if (value is decimal) return (decimal)value;
			if (value is double) return (decimal)(double)value;
			if (value is long) return (long)value;
			if (value is Money) return ((Money)value).Value;

			return 0m;
		}

		private Guid GetLookupId(Entity target, Entity preImage, string attributeName)
		{
			if (target.Contains(attributeName) && target[attributeName] != null)
				return target.GetAttributeValue<EntityReference>(attributeName).Id;
			if (preImage.Contains(attributeName) && preImage[attributeName] != null)
				return preImage.GetAttributeValue<EntityReference>(attributeName).Id;
			return Guid.Empty;
		}

		private string GetStatusCodeFromLookup(Entity entityWithLookup, string lookupLogicalName, IOrganizationService service)
		{
			if (!entityWithLookup.Contains(lookupLogicalName) || entityWithLookup[lookupLogicalName] == null) return "";

			EntityReference lookupRef = entityWithLookup.GetAttributeValue<EntityReference>(lookupLogicalName);
			return GetStatusCodeFromLookup(lookupRef, service);
		}

		private string GetStatusCodeFromLookup(EntityReference lookupRef, IOrganizationService service)
		{
			if (lookupRef == null) return "";

			Entity statusRecord = service.Retrieve(lookupRef.LogicalName, lookupRef.Id, new ColumnSet("new_code"));
			return statusRecord.Contains("new_code") ? statusRecord.GetAttributeValue<string>("new_code") : "";
		}

		#endregion
	}
}