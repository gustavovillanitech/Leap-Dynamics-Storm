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
			// 1. Conexión a tu entorno de Producción
			string envUrl = "https://stormbasketball.crm.dynamics.com/";

			// WARNING PROMPT
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("=========================================================");
			Console.WriteLine($"WARNING: YOU ARE ABOUT TO CONNECT TO:");
			Console.WriteLine(envUrl);
			Console.WriteLine("=========================================================");
			Console.ResetColor();

			Console.WriteLine("\nDO YOU WANT TO PERFORM THE RECALCULATION OF INVENTORIES (SOLD/PITCHED)?");
			Console.WriteLine("PRESS 'Y' FOR YES OR 'N' FOR NO:");

			string userInput = Console.ReadLine();
			if (userInput?.Trim().ToUpper() != "Y")
			{
				Console.WriteLine("\nOperation cancelled by the user. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// 1. DATAVERSE CONNECTION
			string connectionString = $@"AuthType=OAuth;Url={envUrl};Username=YOUR_EMAIL;Password=YOUR_PASSWORD;AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

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
				Console.WriteLine("Conexión exitosa. Iniciando recalculo...");

				// 2. Traer todos los registros de Inventario
				QueryExpression inventoryQuery = new QueryExpression("new_inventory")
				{
					ColumnSet = new ColumnSet("new_quantity", "new_sold", "new_unsold")
				};

				EntityCollection inventories = service.RetrieveMultiple(inventoryQuery);
				Console.WriteLine($"Se encontraron {inventories.Entities.Count} registros de inventario.");

				foreach (Entity inventory in inventories.Entities)
				{
					Guid inventoryId = inventory.Id;

					// USAMOS EL MÉTODO SEGURO AQUÍ TAMBIÉN
					decimal baseQuantity = GetSafeDecimal(inventory, "new_quantity");

					Console.WriteLine($"Procesando Inventario ID: {inventoryId} (Base Qty: {baseQuantity})");

					// 3. Buscar todas las Deal Lines asociadas a este inventario
					QueryExpression linesQuery = new QueryExpression("new_deallines")
					{
						ColumnSet = new ColumnSet("new_quantity", "new_dealid"),
						Criteria = new FilterExpression
						{
							Conditions = { new ConditionExpression("new_inventory", ConditionOperator.Equal, inventoryId) }
						}
					};

					EntityCollection dealLines = service.RetrieveMultiple(linesQuery);

					decimal totalSold = 0m;

					// 4. Evaluar el estado de cada Deal Line
					foreach (Entity line in dealLines.Entities)
					{
						// ✅ SOLUCIÓN: Usamos conversión segura, si viene un int, lo pasa a decimal sin explotar
						decimal lineQty = GetSafeDecimal(line, "new_quantity");

						if (line.Contains("new_dealid"))
						{
							Guid dealId = line.GetAttributeValue<EntityReference>("new_dealid").Id;

							Entity deal = service.Retrieve("new_deals", dealId, new ColumnSet("new_dealstatus"));
							string statusCode = GetStatusCodeFromLookup(deal, "new_dealstatus", service);

							if (statusCode == "DS-1008") // Closed Won
							{
								totalSold += lineQty;
							}
						}
					}

					// 5. Recalcular Unsold
					decimal totalUnsold = baseQuantity - totalSold;

					// 6. Actualizar el Inventario en Producción
					Entity updateInventory = new Entity("new_inventory", inventoryId);
					updateInventory["new_sold"] = totalSold;
					updateInventory["new_unsold"] = totalUnsold;

					service.Update(updateInventory);

					Console.WriteLine($" -> Actualizado: Sold = {totalSold}, Unsold = {totalUnsold}");
				}

				Console.WriteLine("¡Recálculo completado exitosamente! Presiona Enter para salir.");
				Console.ReadLine();
			}
		}

		// ==========================================================
		// MÉTODOS DE AYUDA (HELPER METHODS)
		// ==========================================================

		// Método blindado para leer cualquier número y pasarlo a Decimal
		static decimal GetSafeDecimal(Entity entity, string attributeName)
		{
			if (entity.Contains(attributeName) && entity[attributeName] != null)
			{
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

		// Método para sacar el código del status
		static string GetStatusCodeFromLookup(Entity entityWithLookup, string lookupLogicalName, IOrganizationService service)
		{
			if (!entityWithLookup.Contains(lookupLogicalName) || entityWithLookup[lookupLogicalName] == null) return "";

			EntityReference lookupRef = entityWithLookup.GetAttributeValue<EntityReference>(lookupLogicalName);
			Entity statusRecord = service.Retrieve(lookupRef.LogicalName, lookupRef.Id, new ColumnSet("new_code"));
			return statusRecord.Contains("new_code") ? statusRecord.GetAttributeValue<string>("new_code") : "";
		}
	}
}