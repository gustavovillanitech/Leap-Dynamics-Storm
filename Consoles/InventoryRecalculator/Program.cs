using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;

namespace InventoryRecalculator
{
	class Program
	{
		static void Main(string[] args)
		{
			// 1. Connection setup
			string envUrl = "https://stormbasketball.crm.dynamics.com/";

			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("=========================================================");
			Console.WriteLine($"WARNING: YOU ARE ABOUT TO CONNECT TO:");
			Console.WriteLine(envUrl);
			Console.WriteLine("=========================================================");
			Console.ResetColor();

			Console.WriteLine("\nDO YOU WANT TO PERFORM THE RECALCULATION OF INVENTORIES AND DEALS?");
			Console.WriteLine("PRESS 'Y' FOR YES OR 'N' FOR NO:");

			string userInput = Console.ReadLine();
			if (userInput?.Trim().ToUpper() != "Y")
			{
				Console.WriteLine("\nOperation cancelled by the user. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// Connection string (Update with your credentials or use Interactive Login)
			string connectionString = $@"AuthType=OAuth;Url={envUrl};Username=FanInteractive@stormbasketball.com;Password=CsCXbm2E-WtQ3c4DCy2!;AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

			Console.WriteLine("\nConnecting to Dynamics 365...");
			CrmServiceClient service = new CrmServiceClient(connectionString);

			if (!service.IsReady)
			{
				Console.WriteLine("Connection error: " + service.LastCrmError);
				Console.ReadLine();
				return;
			}
			Console.WriteLine("Connection successful!\n");

			using (service)
			{
				// ==============================================================================
				// PHASE 1: RECALCULATE INVENTORIES (Sold, Pitched, Unsold)
				// ==============================================================================
				Console.WriteLine("--- PHASE 1: RECALCULATING INVENTORIES ---");

				// Note: Add PagingCookie logic here if you have more than 5,000 inventory records
				QueryExpression inventoryQuery = new QueryExpression("new_inventory")
				{
					ColumnSet = new ColumnSet("new_quantity", "new_sold", "new_unsold", "new_pitched")
				};

				EntityCollection inventories = service.RetrieveMultiple(inventoryQuery);
				Console.WriteLine($"Found {inventories.Entities.Count} Inventory records.");

				foreach (Entity inventory in inventories.Entities)
				{
					try
					{
						Guid inventoryId = inventory.Id;
						decimal baseQuantity = GetSafeDecimal(inventory, "new_quantity");

						// LinkEntity to fetch the Deal Status without triggering N+1 queries
						QueryExpression linesQuery = new QueryExpression("new_deallines")
						{
							ColumnSet = new ColumnSet("new_quantity"),
							Criteria = new FilterExpression
							{
								Conditions = { new ConditionExpression("new_inventory", ConditionOperator.Equal, inventoryId) }
							}
						};

						LinkEntity linkDeal = linesQuery.AddLink("new_deals", "new_dealid", "new_dealsid", JoinOperator.Inner);
						linkDeal.Columns = new ColumnSet("new_dealstatus");
						linkDeal.EntityAlias = "deal";

						EntityCollection dealLines = service.RetrieveMultiple(linesQuery);

						decimal totalSold = 0m;
						decimal totalPitched = 0m;

						// Evaluate status in memory
						foreach (Entity line in dealLines.Entities)
						{
							decimal lineQty = GetSafeDecimal(line, "new_quantity");
							string statusCode = GetStatusCodeFromAliasedValue(line, "deal.new_dealstatus", service);

							if (statusCode == "DS-1008") // Closed Won
							{
								totalSold += lineQty;
							}
							else if (statusCode == "DS-1001") // Pitched / Open (Update with actual code)
							{
								totalPitched += lineQty;
							}
						}

						decimal totalUnsold = baseQuantity - totalSold - totalPitched;

						// Update the Inventory record
						Entity updateInventory = new Entity("new_inventory", inventoryId);
						updateInventory["new_sold"] = totalSold;
						updateInventory["new_pitched"] = totalPitched;
						updateInventory["new_unsold"] = totalUnsold;

						service.Update(updateInventory);
						Console.WriteLine($"Inventory {inventoryId} -> OK: Sold={totalSold}, Pitched={totalPitched}, Unsold={totalUnsold}");
					}
					catch (Exception ex)
					{
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine($"ERROR processing Inventory {inventory.Id}: {ex.Message}");
						Console.ResetColor();
					}
				}

				// ==============================================================================
				// PHASE 2: RECALCULATE DEALS (Total & Net Total)
				// ==============================================================================
				Console.WriteLine("\n--- PHASE 2: RECALCULATING DEAL TOTALS ---");

				QueryExpression dealQuery = new QueryExpression("new_deals")
				{
					ColumnSet = new ColumnSet("new_total") // Fetch existing to compare if needed
				};

				EntityCollection deals = service.RetrieveMultiple(dealQuery);
				Console.WriteLine($"Found {deals.Entities.Count} Deal records.");

				foreach (Entity deal in deals.Entities)
				{
					try
					{
						Guid dealId = deal.Id;

						// Fetch all Deal Lines related to this specific Deal
						QueryExpression dealLinesQuery = new QueryExpression("new_deallines")
						{
							ColumnSet = new ColumnSet("new_total"), // Assuming these are the fields on the deal line
							Criteria = new FilterExpression
							{
								Conditions = { new ConditionExpression("new_dealid", ConditionOperator.Equal, dealId) }
							}
						};

						EntityCollection linesForDeal = service.RetrieveMultiple(dealLinesQuery);

						decimal dealTotal = 0m;
						decimal dealNetTotal = 0m;

						// Sum the totals from the child Deal Lines
						foreach (Entity line in linesForDeal.Entities)
						{
							dealTotal += GetSafeDecimal(line, "new_total");
						}

						// Update the Parent Deal record
						Entity updateDeal = new Entity("new_deals", dealId);
						updateDeal["new_total"] = dealTotal;

						service.Update(updateDeal);
						Console.WriteLine($"Deal {dealId} -> OK: Total={dealTotal}, Net Total={dealNetTotal}");
					}
					catch (Exception ex)
					{
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine($"ERROR processing Deal {deal.Id}: {ex.Message}");
						Console.ResetColor();
					}
				}

				Console.WriteLine("\nData fix completed successfully! Press Enter to exit.");
				Console.ReadLine();
			}
		}

		// ==========================================================
		// HELPER METHODS
		// ==========================================================

		/// <summary>
		/// Safely converts an Entity attribute to a decimal. Returns 0 if null or missing.
		/// </summary>
		static decimal GetSafeDecimal(Entity entity, string attributeName)
		{
			if (entity.Contains(attributeName) && entity[attributeName] != null)
			{
				// Is money?
				if (entity[attributeName] is Money moneyField)
				{
					return moneyField.Value;
				}

				// Is decimal or double?
				try
				{
					return Convert.ToDecimal(entity[attributeName]);
				}
				catch
				{
					return 0m;
				}
			}
			return 0m;
		}

		/// <summary>
		/// Retrieves the status code from a linked entity (AliasedValue).
		/// </summary>
		static string GetStatusCodeFromAliasedValue(Entity line, string aliasedAttributeName, IOrganizationService service)
		{
			if (!line.Contains(aliasedAttributeName)) return "";

			var aliasedValue = line.GetAttributeValue<AliasedValue>(aliasedAttributeName);
			if (aliasedValue == null || aliasedValue.Value == null) return "";

			EntityReference lookupRef = (EntityReference)aliasedValue.Value;

			// Retrieve the actual code from the status configuration table
			Entity statusRecord = service.Retrieve(lookupRef.LogicalName, lookupRef.Id, new ColumnSet("new_code"));
			return statusRecord.Contains("new_code") ? statusRecord.GetAttributeValue<string>("new_code") : "";
		}
	}
}