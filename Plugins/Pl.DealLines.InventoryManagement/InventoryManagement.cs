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
						CalculateDealLineMetrics(target, preImage, tracingService);
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

		private void CalculateDealLineMetrics(Entity target, Entity preImage, ITracingService tracingService)
		{
			tracingService.Trace("Calculating Deal Line Financial Metrics...");

			decimal quantity = GetDecimalValue(target, preImage, "new_quantity");
			decimal rateCharged = GetMoneyValue(target, preImage, "new_rate");
			decimal rateCard = GetMoneyValue(target, preImage, "new_ratecard");

			decimal total = quantity * rateCharged;
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

			tracingService.Trace($"Metrics Calculated -> Total: {total}, Yield: {yieldValue}");
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
                            <filter>
                                <condition attribute='new_dealid' operator='eq' value='{dealId}' />
                            </filter>
                            </entity>
                        </fetch>";

			EntityCollection result = service.RetrieveMultiple(new FetchExpression(fetchXml));
			decimal netTotal = 0m;

			if (result.Entities.Count > 0 && result.Entities[0].Contains("sum_total"))
			{
				AliasedValue aliasedTotal = (AliasedValue)result.Entities[0]["sum_total"];

				if (aliasedTotal != null && aliasedTotal.Value != null)
				{
					netTotal = ((Money)aliasedTotal.Value).Value;
				}
			}

			// Calculate Max Activation Spend
			decimal maxActivationSpend = CalculateMaxActivationSpend(netTotal, service, tracingService);

			Entity dealToUpdate = new Entity("new_deals", dealId);
			dealToUpdate["new_total"] = new Money(netTotal);
			dealToUpdate["new_maxactivationspend"] = new Money(maxActivationSpend);
			service.Update(dealToUpdate);

			tracingService.Trace($"Deal {dealId} updated -> Total: {netTotal}, MaxActivationSpend: {maxActivationSpend}");
		}

		/// <summary>
		/// Reads the Max Activation Spend percentage from the Deal Configuration table
		/// and applies it to the deal total. Returns 0 if config is missing or total is 0.
		/// </summary>
		private decimal CalculateMaxActivationSpend(decimal dealTotal, IOrganizationService service, ITracingService tracingService)
		{
			if (dealTotal == 0m)
			{
				tracingService.Trace("Deal total is 0, MaxActivationSpend = 0.");
				return 0m;
			}

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

			if (!config.Contains("new_maxactivationspendpercent"))
			{
				tracingService.Trace("WARNING: Deal Configuration record exists but new_maxactivationspendpercent is null.");
				return 0m;
			}

			decimal percent = Convert.ToDecimal(config["new_maxactivationspendpercent"]);
			decimal result = Math.Round(dealTotal * (percent / 100m), 2, MidpointRounding.AwayFromZero);

			tracingService.Trace($"MaxActivationSpend calc -> Total: {dealTotal} × {percent}% = {result}");
			return result;
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