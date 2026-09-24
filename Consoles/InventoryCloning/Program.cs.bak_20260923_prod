using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace InventoryCloning
{
	/// <summary>
	/// Rolls a season's inventory forward into future seasons.
	///
	/// Future-year deals have to draw on their own season's inventory. CloneMultiYearDeals now
	/// refuses to generate a future-year deal when that season has no inventory for an asset the
	/// contract carries, so this is the tool that prepares the ground for it.
	///
	/// Safe to run more than once: an asset that already exists in the target season is skipped,
	/// never duplicated. Start with DRY_RUN = true and read the report before writing anything.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// Sandbox. Change deliberately, never by accident.
		private const string ENV_URL = "https://org00bff505.crm.dynamics.com/";

		// true  = report what would happen and write nothing.
		// false = actually create the records.
		private const bool DRY_RUN = false;

		// The season whose inventory is the template.
		private const int SOURCE_YEAR = 2026;

		// The seasons to create the inventory for.
		private static readonly int[] TARGET_YEARS = { 2030 };

		// Only seasons whose name contains one of these is processed. Keeps the run away from
		// season records that belong to something else.
		private static readonly string[] SEASON_NAME_FILTERS = { "Storm", "Practice Facility" };

		// =====================================================================

		private const string SEASON_ENTITY = "new_season";
		private const string INVENTORY_ENTITY = "new_inventory";
		private const string INV_SEASON_LOOKUP = "new_seasonid";
		private const string INV_PRODUCT_LOOKUP = "new_productid";
		private const string INV_COLLECTION_LOOKUP = "new_collection";
		private const string INV_DIVISION_LOOKUP = "new_division";

		// Never carried across: system columns, the Trak id (each season's row gets its own), and
		// the availability buckets, which are reset rather than copied.
		private static readonly HashSet<string> SKIP_ATTRIBUTES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"new_inventoryid", "createdon", "createdby", "modifiedon", "modifiedby",
			"createdonbehalfby", "modifiedonbehalfby",
			"ownerid", "owningbusinessunit", "owninguser", "owningteam",
			"statecode", "statuscode", "versionnumber", "timezoneruleversionnumber",
			"utcconversiontimezonecode", "importsequencenumber", "overriddencreatedon",
			"new_trakinventoryid",
			"new_sold", "new_pitched", "new_allocated", "new_unsold"
		};

		static void Main(string[] args)
		{
			Console.WriteLine("=========================================================");
			Console.WriteLine("  INVENTORY ROLLOVER");
			Console.WriteLine("=========================================================");
			Console.WriteLine($"  Environment : {ENV_URL}");
			Console.WriteLine($"  Source year : {SOURCE_YEAR}");
			Console.WriteLine($"  Target years: {string.Join(", ", TARGET_YEARS)}");
			Console.WriteLine($"  Seasons     : {string.Join(", ", SEASON_NAME_FILTERS)}");
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

			Console.Write("\nType YES to continue: ");
			if ((Console.ReadLine() ?? string.Empty).Trim().ToUpperInvariant() != "YES")
			{
				Console.WriteLine("\nCancelled. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// Interactive sign-in. No credentials live in this file.
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

			int created = 0, skipped = 0, seasonsCreated = 0;

			try
			{
				List<Entity> sourceSeasons = GetSourceSeasons(service);

				if (sourceSeasons.Count == 0)
				{
					Console.WriteLine($"No {SOURCE_YEAR} season matched the filters. Nothing to do.");
					Console.ReadLine();
					return;
				}

				Console.WriteLine($"Found {sourceSeasons.Count} source season(s) for {SOURCE_YEAR}.\n");

				foreach (int targetYear in TARGET_YEARS)
				{
					Console.WriteLine($"===== {targetYear} =====");

					foreach (Entity sourceSeason in sourceSeasons)
					{
						string sourceName = sourceSeason.GetAttributeValue<string>("new_name");
						string targetName = sourceName.Replace(SOURCE_YEAR.ToString(), targetYear.ToString());

						Console.WriteLine($"\n  {sourceName}  ->  {targetName}");

						Entity targetSeason = FindSeason(service, targetYear, targetName);

						if (targetSeason == null)
						{
							Console.WriteLine($"    Season does not exist. It will be created.");

							if (!DRY_RUN)
							{
								Entity newSeason = new Entity(SEASON_ENTITY);
								newSeason["new_name"] = targetName;

								// new_seasonyear is not decoration: CloneMultiYearDeals looks the
								// target season up by year. A season created without it is invisible
								// to the cloning plugin.
								newSeason["new_seasonyear"] = targetYear;

								Guid newSeasonId = service.Create(newSeason);
								targetSeason = new Entity(SEASON_ENTITY, newSeasonId);
								seasonsCreated++;
								Console.WriteLine($"    Season created.");
							}
							else
							{
								// Nothing to compare against in a dry run, so report and move on.
								int wouldCreate = CountSourceInventory(service, sourceSeason.Id);
								Console.WriteLine($"    Would create the season and {wouldCreate} inventory record(s).");
								continue;
							}
						}
						else
						{
							Console.WriteLine($"    Season already exists, reusing it.");
							WarnIfGameCountsMissing(service, targetSeason.Id, targetName);
						}

						RolloverInventory(service, sourceSeason.Id, targetSeason.Id, ref created, ref skipped);
					}

					Console.WriteLine();
				}

				Console.WriteLine("=========================================================");
				Console.WriteLine(DRY_RUN ? "  DRY RUN COMPLETE - nothing was written" : "  ROLLOVER COMPLETE");
				Console.WriteLine($"  Seasons created   : {seasonsCreated}");
				Console.WriteLine($"  Inventory created : {created}");
				Console.WriteLine($"  Already existed   : {skipped}");
				Console.WriteLine("=========================================================");

				if (DRY_RUN)
				{
					Console.WriteLine("\nSet DRY_RUN = false to apply these changes.");
				}
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine($"\nERROR: {ex.Message}");
				Console.ResetColor();
				Console.WriteLine(ex.StackTrace);
			}

			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}

		/// <summary>
		/// The seasons to copy from. Matched on year, falling back to the name when the year field
		/// was never populated on the older records.
		/// </summary>
		private static List<Entity> GetSourceSeasons(IOrganizationService service)
		{
			QueryExpression byYear = new QueryExpression(SEASON_ENTITY)
			{
				ColumnSet = new ColumnSet("new_name", "new_seasonyear")
			};
			byYear.Criteria.AddCondition("new_seasonyear", ConditionOperator.Equal, SOURCE_YEAR);

			List<Entity> seasons = service.RetrieveMultiple(byYear).Entities.ToList();

			if (seasons.Count == 0)
			{
				QueryExpression byName = new QueryExpression(SEASON_ENTITY)
				{
					ColumnSet = new ColumnSet("new_name", "new_seasonyear")
				};
				byName.Criteria.AddCondition("new_name", ConditionOperator.Like, $"%{SOURCE_YEAR}%");
				seasons = service.RetrieveMultiple(byName).Entities.ToList();

				if (seasons.Count > 0)
					Console.WriteLine($"NOTE: no season had new_seasonyear = {SOURCE_YEAR}; matched by name instead.");
			}

			return seasons
				.Where(s =>
				{
					string name = s.GetAttributeValue<string>("new_name") ?? string.Empty;
					return SEASON_NAME_FILTERS.Any(f => name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
				})
				.ToList();
		}

		private static Entity FindSeason(IOrganizationService service, int year, string name)
		{
			QueryExpression query = new QueryExpression(SEASON_ENTITY)
			{
				ColumnSet = new ColumnSet("new_name", "new_seasonyear"),
				TopCount = 1
			};
			query.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);

			Entity found = service.RetrieveMultiple(query).Entities.FirstOrDefault();
			if (found == null) return null;

			// An existing season with no year is a trap: the cloning plugin will not find it.
			if (!found.Contains("new_seasonyear"))
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($"    WARNING: '{name}' has no Season Year. CloneMultiYearDeals looks seasons up by year and will not find it.");
				Console.ResetColor();

				if (!DRY_RUN)
				{
					Entity fix = new Entity(SEASON_ENTITY, found.Id);
					fix["new_seasonyear"] = year;
					service.Update(fix);
					Console.WriteLine($"    Season Year set to {year}.");
				}
			}

			return found;
		}

		/// <summary>
		/// Home and Away Games drive the incremental games calculation. They are not copied from the
		/// source season, because next season's schedule is exactly the thing nobody knows yet, and a
		/// wrong number here produces wrong money downstream without any error.
		/// </summary>
		private static void WarnIfGameCountsMissing(IOrganizationService service, Guid seasonId, string name)
		{
			Entity season = service.Retrieve(SEASON_ENTITY, seasonId, new ColumnSet("new_homegames", "new_awaygames"));

			bool missing = !season.Contains("new_homegames") || season.GetAttributeValue<int>("new_homegames") == 0;
			if (!missing) return;

			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine($"    REMINDER: '{name}' has no Home Games. Incremental games stay at zero until it is set.");
			Console.ResetColor();
		}

		private static int CountSourceInventory(IOrganizationService service, Guid sourceSeasonId)
		{
			QueryExpression query = new QueryExpression(INVENTORY_ENTITY)
			{
				ColumnSet = new ColumnSet(INV_PRODUCT_LOOKUP)
			};
			query.Criteria.AddCondition(INV_SEASON_LOOKUP, ConditionOperator.Equal, sourceSeasonId);
			return service.RetrieveMultiple(query).Entities.Count;
		}

		/// <summary>
		/// Copies every inventory record of the source season into the target season, skipping the
		/// products that are already there. Skipping by product is what makes the tool re-runnable:
		/// a partial run can be finished by running it again.
		/// </summary>
		private static void RolloverInventory(IOrganizationService service, Guid sourceSeasonId, Guid targetSeasonId,
			ref int created, ref int skipped)
		{
			QueryExpression sourceQuery = new QueryExpression(INVENTORY_ENTITY)
			{
				ColumnSet = new ColumnSet(true)
			};
			sourceQuery.Criteria.AddCondition(INV_SEASON_LOOKUP, ConditionOperator.Equal, sourceSeasonId);
			List<Entity> sourceInventory = service.RetrieveMultiple(sourceQuery).Entities.ToList();

			QueryExpression existingQuery = new QueryExpression(INVENTORY_ENTITY)
			{
				ColumnSet = new ColumnSet(INV_PRODUCT_LOOKUP, INV_DIVISION_LOOKUP, INV_COLLECTION_LOOKUP)
			};
			existingQuery.Criteria.AddCondition(INV_SEASON_LOOKUP, ConditionOperator.Equal, targetSeasonId);

			HashSet<string> alreadyThere = new HashSet<string>(
				service.RetrieveMultiple(existingQuery).Entities
					.Where(e => e.GetAttributeValue<EntityReference>(INV_PRODUCT_LOOKUP) != null)
					.Select(InventoryKey));

			Console.WriteLine($"    {sourceInventory.Count} source record(s), {alreadyThere.Count} already in the target season.");

			// Two different reasons to skip, counted apart. Lumping them together hides the second
			// one, which is the interesting one: it means the source season itself has more than one
			// inventory row for the same product.
			int localCreated = 0;
			int skippedExisting = 0;
			int noProduct = 0;

			HashSet<string> seenInSource = new HashSet<string>();
			List<string> duplicatesInSource = new List<string>();

			foreach (Entity source in sourceInventory)
			{
				EntityReference product = source.GetAttributeValue<EntityReference>(INV_PRODUCT_LOOKUP);
				string label = source.GetAttributeValue<string>("new_name") ?? "(unnamed)";

				if (product == null)
				{
					// Without a product there is no way to tell it apart from anything else, and
					// CloneMultiYearDeals matches on product too, so it would not be usable anyway.
					noProduct++;
					continue;
				}

				string key = InventoryKey(source);

				if (alreadyThere.Contains(key))
				{
					skippedExisting++;
					continue;
				}

				if (!seenInSource.Add(key))
				{
					duplicatesInSource.Add($"{label} (product: {product.Name ?? product.Id.ToString()})");
					continue;
				}

				if (!DRY_RUN)
				{
					service.Create(BuildTargetInventory(source, targetSeasonId));
				}

				localCreated++;
			}

			created += localCreated;
			skipped += skippedExisting;

			Console.WriteLine(DRY_RUN
				? $"    Would create {localCreated}, skip {skippedExisting} already in the target season."
				: $"    Created {localCreated}, skipped {skippedExisting} already in the target season.");

			if (duplicatesInSource.Count > 0)
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($"    {duplicatesInSource.Count} source record(s) share a product with another row and were NOT copied.");
				Console.WriteLine("      CloneMultiYearDeals matches inventory by product, so two rows for the same");
				Console.WriteLine("      product in one season are ambiguous. Review these before going live.");
				Console.ResetColor();

				ReportDuplicateGroups(sourceInventory);
			}

			if (noProduct > 0)
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($"    {noProduct} source record(s) have no Product and were ignored.");
				Console.ResetColor();
			}
		}

		/// <summary>
		/// Prints every row involved in a duplicated product, side by side, so the difference between
		/// them (or the absence of one) is visible without opening the records one at a time.
		///
		/// Two rows that differ only by id are duplicate data and should be merged. Two rows that
		/// differ by collection, division or rate are legitimate variants, and matching inventory by
		/// product alone is then too coarse for the cloning plugin.
		/// </summary>
		private static void ReportDuplicateGroups(List<Entity> sourceInventory)
		{
			var groups = sourceInventory
				.Where(e => e.GetAttributeValue<EntityReference>(INV_PRODUCT_LOOKUP) != null)
				.GroupBy(InventoryKey)
				.Where(g => g.Count() > 1);

			foreach (var group in groups)
			{
				EntityReference product = group.First().GetAttributeValue<EntityReference>(INV_PRODUCT_LOOKUP);
				Console.WriteLine();
				Console.WriteLine($"      Product: {product.Name ?? product.Id.ToString()}  ({group.Count()} rows with the same division and collection)");

				foreach (Entity row in group)
				{
					Console.WriteLine($"        id        : {row.Id}");
					Console.WriteLine($"        name      : {row.GetAttributeValue<string>("new_name")}");
					Console.WriteLine($"        collection: {Describe(row, "new_collection")}");
					Console.WriteLine($"        division  : {Describe(row, "new_division")}");
					Console.WriteLine($"        rate card : {FormatMoney(row, "new_rate")}");
					Console.WriteLine($"        quantity  : {FormatNumber(row, "new_quantity")}");
					Console.WriteLine($"        is package: {row.GetAttributeValue<bool>("new_ispackage")}");
					Console.WriteLine();
				}
			}
		}

		private static string Describe(Entity record, string attribute)
		{
			EntityReference reference = record.GetAttributeValue<EntityReference>(attribute);
			if (reference != null) return reference.Name ?? reference.Id.ToString();

			OptionSetValue option = record.GetAttributeValue<OptionSetValue>(attribute);
			if (option != null) return option.Value.ToString();

			object raw = record.Contains(attribute) ? record[attribute] : null;
			return raw != null ? raw.ToString() : "(blank)";
		}

		private static string FormatMoney(Entity record, string attribute)
		{
			Money value = record.GetAttributeValue<Money>(attribute);
			return value != null ? value.Value.ToString("N2") : "(blank)";
		}

		private static string FormatNumber(Entity record, string attribute)
		{
			if (!record.Contains(attribute) || record[attribute] == null) return "(blank)";
			return Convert.ToDecimal(record[attribute]).ToString("N2");
		}

		/// <summary>
		/// What makes an inventory row unique within a season: product, division and collection.
		/// Season is the scope of the comparison, so it is not part of the key.
		///
		/// Product alone is not enough. The 2026 data has "Banner Ad" twice, once under
		/// Digital - App and once under Digital - Website, at different rates and quantities. They
		/// are different things sold separately, and matching on product alone would collapse them
		/// into one, so both deal lines would draw down the same availability. Division is included
		/// for the same reason: it is a category of the item, not a detail of it.
		///
		/// This has to stay identical to InventoryKey in CloneMultiYearDeals. If one of them changes,
		/// the other has to change with it, or the cloning plugin will look for rows this tool never
		/// created.
		/// </summary>
		private static string InventoryKey(Entity inventory)
		{
			return string.Join("|",
				LookupKeyPart(inventory, INV_PRODUCT_LOOKUP),
				LookupKeyPart(inventory, INV_DIVISION_LOOKUP),
				LookupKeyPart(inventory, INV_COLLECTION_LOOKUP));
		}

		private static string LookupKeyPart(Entity record, string attribute)
		{
			EntityReference reference = record.GetAttributeValue<EntityReference>(attribute);
			return reference != null ? reference.Id.ToString() : "none";
		}

		private static Entity BuildTargetInventory(Entity source, Guid targetSeasonId)
		{
			Entity target = new Entity(INVENTORY_ENTITY);

			foreach (var attribute in source.Attributes)
			{
				if (SKIP_ATTRIBUTES.Contains(attribute.Key)) continue;
				target[attribute.Key] = attribute.Value;
			}

			target[INV_SEASON_LOOKUP] = new EntityReference(SEASON_ENTITY, targetSeasonId);

			// A new season starts with nothing sold or pitched, and everything available.
			decimal quantity = source.Contains("new_quantity") ? Convert.ToDecimal(source["new_quantity"]) : 0m;
			target["new_sold"] = 0m;
			target["new_pitched"] = 0m;
			target["new_allocated"] = 0m;
			target["new_unsold"] = quantity;

			return target;
		}
	}
}
