using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;

namespace InventoryCloning
{
	internal class Program
	{
		static void Main(string[] args)
		{
			// 1. DATAVERSE CONNECTION
			string envUrl = "https://stormbasketball.crm.dynamics.com/";

			// WARNING PROMPT
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("=========================================================");
			Console.WriteLine($"WARNING: YOU ARE ABOUT TO CONNECT TO:");
			Console.WriteLine(envUrl);
			Console.WriteLine("=========================================================");
			Console.ResetColor();

			Console.WriteLine("\nDO YOU WANT TO PERFORM THE CLONING OF INVENTORIES FOR SEASONS 2027-2035?");
			Console.WriteLine("PRESS 'Y' FOR YES OR 'N' FOR NO:");

			string userInput = Console.ReadLine();
			if (userInput?.Trim().ToUpper() != "Y")
			{
				Console.WriteLine("\nOperation cancelled by the user. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// 1. DATAVERSE CONNECTION
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

			// =========================================================================
			// LOGICAL NAMES CONFIGURATION
			// =========================================================================
			string seasonEntityName = "new_season";
			string seasonNameField = "new_name";

			string invEntityName = "new_inventory";
			string invSeasonLookup = "new_seasonid";

			//Trak Inventory ID
			string trakInventoryIdField = "new_trakinventoryid";
			// =========================================================================

			try
			{
				// 2. RETRIEVE ALL 2026 SEASONS
				QueryExpression seasonQuery = new QueryExpression(seasonEntityName)
				{
					ColumnSet = new ColumnSet(seasonNameField),
					Criteria = new FilterExpression
					{
						Conditions = {
							new ConditionExpression(seasonNameField, ConditionOperator.Like, "%2026%")
						}
					}
				};

				EntityCollection sourceSeasons = service.RetrieveMultiple(seasonQuery);

				// 3. LOOP THROUGH YEARS 2027 TO 2035
				for (int year = 2027; year <= 2035; year++)
				{
					Console.WriteLine($"\n--- PROCESSING YEAR {year} ---");

					foreach (Entity sourceSeason in sourceSeasons.Entities)
					{
						string sourceSeasonName = sourceSeason.GetAttributeValue<string>(seasonNameField);

						// Filter ONLY "Storm" and "Practice Facility"
						if (!sourceSeasonName.Contains("Storm") && !sourceSeasonName.Contains("Practice Facility"))
						{
							continue;
						}

						string newSeasonName = sourceSeasonName.Replace("2026", year.ToString());

						// 4. CREATE THE NEW SEASON
						Entity newSeason = new Entity(seasonEntityName);
						newSeason[seasonNameField] = newSeasonName;
						Guid newSeasonId = service.Create(newSeason);
						Console.WriteLine($"Season created: {newSeasonName}");

						// 5. RETRIEVE INVENTORY RECORDS FOR THIS SPECIFIC 2026 SEASON
						QueryExpression invQuery = new QueryExpression(invEntityName)
						{
							ColumnSet = new ColumnSet(true), // Esto trae Product ID, Division, Collection, Rate, etc.
							Criteria = new FilterExpression
							{
								Conditions = {
									new ConditionExpression(invSeasonLookup, ConditionOperator.Equal, sourceSeason.Id)
								}
							}
						};

						EntityCollection sourceInventories = service.RetrieveMultiple(invQuery);
						Console.WriteLine($" -> Cloning {sourceInventories.Entities.Count} inventory records...");

						// 6. CLONE EACH INVENTORY RECORD
						foreach (Entity oldInv in sourceInventories.Entities)
						{
							Entity newInv = new Entity(invEntityName);

							foreach (var attr in oldInv.Attributes)
							{
								string key = attr.Key;

								// Ignore System fields AND the Trak Inventory ID (so it stays blank)
								if (key == invEntityName + "id" || key == "createdon" || key == "createdby" ||
									key == "modifiedon" || key == "modifiedby" || key == "ownerid" ||
									key == "owningbusinessunit" || key == "owninguser" || key == "owningteam" ||
									key == "statecode" || key == "statuscode" ||
									key == trakInventoryIdField)
									continue;

								newInv[key] = attr.Value;
							}

							// 7. APPLY BUSINESS RULES FOR THE NEW YEAR

							// A. Point to the new Season
							newInv[invSeasonLookup] = new EntityReference(seasonEntityName, newSeasonId);

							// B. Reset Buckets to 0
							newInv["new_sold"] = 0m;
							newInv["new_pitched"] = 0m;
							newInv["new_allocated"] = 0m;

							// C. Set Unsold equal to Base Quantity
							decimal baseQty = oldInv.Contains("new_quantity") ? Convert.ToDecimal(oldInv["new_quantity"]) : 0m;
							newInv["new_unsold"] = baseQty;

							// Insert new record
							service.Create(newInv);
						}
					}
				}

				Console.WriteLine("\nCLONING PROCESS COMPLETED SUCCESSFULLY!");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"\nAN ERROR OCCURRED: {ex.Message}");
			}

			Console.WriteLine("Press Enter to exit...");
			Console.ReadLine();
		}
	}
}