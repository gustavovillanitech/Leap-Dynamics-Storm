using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Linq;

namespace CreateIncrementalTestDeals
{
	/// <summary>
	/// Builds one sandbox practice deal per Storm tester, cloned from a reference deal.
	///
	/// Ray's ask (2026-09-12): the Storm team is hearing the incremental-games explanation but it
	/// is not landing until they do it themselves, so each of them gets their own deal to work on
	/// and nobody trips over anybody else's edits.
	///
	/// The clone deliberately arrives EMPTY of the incremental clause. Filling that in is the
	/// exercise - see section 4 of IncrementalGames_UserManual.docx. Deal lines are copied so the
	/// allocation step in section 6 has something real to spread money across.
	///
	/// Pick a source deal in a season whose Home Games EXCEEDS the contracted games the tester
	/// will type, or Incremental Contract Value lands on $0.00 and the exercise proves nothing.
	/// 2026 - Storm has 22 home games; 2027 - Storm has 25. That is why the default source is the
	/// 2027 deal.
	///
	/// Safe to run twice: a tester whose deal already exists is skipped, never duplicated.
	/// Start with DRY_RUN = true and read the report.
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
		private const bool DRY_RUN = true;

		// The deal the practice copies are made from. Must already exist in the environment above.
		private const string SOURCE_DEAL_NAME = "Coho Winery (sample) - 2027 - Storm";

		// The source deal's id, used by RENAME_ONLY instead of its name.
		// Looking the source up by name is unreliable exactly when rename mode is needed: the
		// clones carry the SAME name until they are renamed, so the lookup returns whichever row
		// the platform hands back first - which on the 2026-09-15 run was a clone, not the source.
		// Leave empty to fall back to the name lookup.
		private const string SOURCE_DEAL_ID = "4005ae4b-77a8-f111-b8de-6045bd019888";

		// One deal per tester. The name is what they will search for, so keep it recognisable.
		private static readonly string[] TESTERS =
		{
			"Nate Silverman",
			"Christine Cook",
			"Bridget Peterson",
			"Tiffany Tran",
			"Matthew Madrona",
			"Katie Berger"
		};

		// Target deal name = PREFIX + tester. "IG Test - Nate Silverman".
		private const string NAME_PREFIX = "IG Test - ";

		// RENAME-ONLY MODE. Set true to skip creation entirely and only fix the names of deals
		// created by an earlier run. See ALREADY_CREATED below and the note about Pl.Deal.SetName.
		private const bool RENAME_ONLY = false;

		// Deals from the 2026-09-15 run, which all came out named after the source deal because
		// Pl.Deal.SetName overwrites new_name on Create. Ids from that run's console output.
		private static readonly Dictionary<string, string> ALREADY_CREATED = new Dictionary<string, string>
		{
			{ "Nate Silverman",  "f4d43143-1ab1-f111-aaac-6045bd019888" },
			{ "Christine Cook",  "83707f47-1ab1-f111-aaac-6045bd019c50" },
			{ "Bridget Peterson","d60b3550-1ab1-f111-aaac-6045bd019ed9" },
			{ "Tiffany Tran",    "9c5fde53-1ab1-f111-aaac-6045bd019c50" },
			{ "Matthew Madrona", "0a3c625b-1ab1-f111-aaac-6045bd019888" },
			{ "Katie Berger",    "55582d5c-1ab1-f111-aaac-6045bd019ed9" }
		};

		// =====================================================================

		private const string DEAL_ENTITY = "new_deals";
		private const string DEAL_PK = "new_dealsid";
		private const string LINE_ENTITY = "new_deallines";
		private const string LINE_DEAL_LOOKUP = "new_dealid";

		// Cleared on the clone: the tester fills these in. That IS the exercise.
		private static readonly string[] CLEAR_ON_DEAL =
		{
			"new_incrementalgamesclause",
			"new_contractedhomegames",
			"new_contractedawaygames",
			"new_awaygamebenefits",
			"new_incrementalpricingmethod",
			"new_investmentperhomegame",
			"new_investmentperawaygame",
			"new_incrementalcontractvalue",
			"new_incrementalgamesnotes"
		};

		// Cleared on every copied line, for the same reason.
		private static readonly string[] CLEAR_ON_LINE =
		{
			"new_incrementalrateperhomegame",
			"new_incrementalrateperawaygame",
			"new_incrementalrevenue"
		};

		// Never carried across regardless of what the metadata allows: identity and ownership.
		private static readonly HashSet<string> SKIP_ALWAYS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			"new_dealsid", "new_deallinesid", "new_name",
			"ownerid", "owningbusinessunit", "owninguser", "owningteam",
			"statecode", "statuscode", "versionnumber",
			"createdon", "createdby", "modifiedon", "modifiedby",
			"createdonbehalfby", "modifiedonbehalfby",
			"importsequencenumber", "overriddencreatedon",
			"timezoneruleversionnumber", "utcconversiontimezonecode"
		};

		static void Main(string[] args)
		{
			Console.WriteLine("=========================================================");
			Console.WriteLine("  INCREMENTAL GAMES - TESTER PRACTICE DEALS");
			Console.WriteLine("=========================================================");
			Console.WriteLine($"  Environment : {ENV_URL}");
			Console.WriteLine($"  Source deal : {SOURCE_DEAL_NAME}");
			Console.WriteLine($"  Testers     : {TESTERS.Length}");
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

			if (!ENV_URL.Contains("org00bff505"))
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\n  *** This is NOT the sandbox URL. These are throwaway test records. ***");
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

			int created = 0, skipped = 0, failed = 0, linesCreated = 0;

			if (RENAME_ONLY)
			{
				RenameExisting(service);
				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
				return;
			}

			try
			{
				Entity source = ResolveSourceDeal(service);
				if (source == null)
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine($"Source deal not found: {SOURCE_DEAL_NAME}");
					Console.ResetColor();
					ListCandidates(service);
					Console.ReadLine();
					return;
				}

				Console.WriteLine($"Source deal found: {source.Id}");
				WarnIfSeasonHasNoHeadroom(service, source);

				List<Entity> sourceLines = GetDealLines(service, source.Id);
				Console.WriteLine($"Source lines    : {sourceLines.Count}\n");

				if (sourceLines.Count == 0)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine("The source deal has no lines. There would be nothing to allocate revenue to.");
					Console.ResetColor();
				}

				HashSet<string> dealCreatable = CreatableAttributes(service, DEAL_ENTITY);
				HashSet<string> lineCreatable = CreatableAttributes(service, LINE_ENTITY);

				foreach (string tester in TESTERS)
				{
					string targetName = NAME_PREFIX + tester;
					Console.WriteLine($"--- {targetName}");

					if (FindDealByName(service, targetName) != null)
					{
						Console.WriteLine("    Already exists. Skipped.");
						skipped++;
						continue;
					}

					try
					{
						Entity clone = CopyFor(source, dealCreatable, CLEAR_ON_DEAL);
						clone["new_name"] = targetName;

						Guid newDealId = Guid.Empty;
						if (!DRY_RUN)
						{
							newDealId = service.Create(clone);

							// Pl.Deal.SetName runs pre-op on Create with shouldGenerate = true and
							// rebuilds new_name from Account + Season, discarding whatever we sent.
							// Every clone would come out named after the source deal. On Update the
							// same plugin regenerates ONLY when Account or Season is in the Target,
							// so a second call carrying just the name is respected.
							service.Update(new Entity(DEAL_ENTITY, newDealId) { ["new_name"] = targetName });

							Console.WriteLine($"    Deal created: {newDealId}");
						}
						else
						{
							Console.WriteLine($"    Deal WOULD be created ({clone.Attributes.Count} attributes)");
						}

						int n = 0;
						foreach (Entity line in sourceLines)
						{
							Entity lineClone = CopyFor(line, lineCreatable, CLEAR_ON_LINE);
							if (!DRY_RUN)
							{
								lineClone[LINE_DEAL_LOOKUP] = new EntityReference(DEAL_ENTITY, newDealId);
								service.Create(lineClone);
							}
							n++;
						}

						linesCreated += n;
						created++;
						Console.WriteLine(DRY_RUN
							? $"    {n} line(s) WOULD be copied"
							: $"    {n} line(s) copied");
					}
					catch (Exception ex)
					{
						failed++;
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine("    FAILED: " + ex.Message);
						Console.ResetColor();
					}
				}
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("\nUnhandled error: " + ex);
				Console.ResetColor();
			}

			Console.WriteLine("\n=========================================================");
			Console.WriteLine($"  Deals {(DRY_RUN ? "to create" : "created")} : {created}");
			Console.WriteLine($"  Lines {(DRY_RUN ? "to copy" : "copied")}   : {linesCreated}");
			Console.WriteLine($"  Skipped (exist)  : {skipped}");
			Console.WriteLine($"  Failed           : {failed}");
			Console.WriteLine("=========================================================");
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}

		/// <summary>
		/// Renames deals a previous run created, addressing them by id because their names are
		/// currently identical to the source deal's and cannot be searched for.
		///
		/// Each record is read first and checked: if it already carries the intended name the
		/// update is skipped, and if it is the source deal the id is refused outright. Renaming the
		/// wrong deal here would be silent and hard to notice.
		/// </summary>
		private static void RenameExisting(CrmServiceClient service)
		{
			Console.WriteLine("RENAME-ONLY MODE - no records will be created.\n");

			Guid sourceId;
			if (!Guid.TryParse(SOURCE_DEAL_ID, out sourceId))
			{
				Entity source = FindDealByName(service, SOURCE_DEAL_NAME);
				sourceId = source?.Id ?? Guid.Empty;

				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("SOURCE_DEAL_ID is not set, so the source was looked up by name.");
				Console.WriteLine("That name is not unique while clones are still unrenamed - check the");
				Console.WriteLine($"resolved id before trusting a refusal:  {sourceId}\n");
				Console.ResetColor();
			}

			int renamed = 0, already = 0, failed = 0;

			foreach (var kv in ALREADY_CREATED)
			{
				string targetName = NAME_PREFIX + kv.Key;
				Console.WriteLine($"--- {targetName}");

				Guid id;
				if (!Guid.TryParse(kv.Value, out id))
				{
					failed++;
					Console.WriteLine("    Not a valid id. Skipped.");
					continue;
				}

				if (id == sourceId)
				{
					failed++;
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("    REFUSED: that is the source deal's own id.");
					Console.ResetColor();
					continue;
				}

				try
				{
					Entity current = service.Retrieve(DEAL_ENTITY, id, new ColumnSet("new_name"));
					string currentName = current.GetAttributeValue<string>("new_name");

					if (currentName == targetName)
					{
						already++;
						Console.WriteLine("    Already named correctly. Skipped.");
						continue;
					}

					Console.WriteLine($"    '{currentName}'  ->  '{targetName}'");

					if (DRY_RUN)
					{
						Console.WriteLine("    DRY RUN - not written");
						continue;
					}

					service.Update(new Entity(DEAL_ENTITY, id) { ["new_name"] = targetName });
					renamed++;
					Console.WriteLine("    Renamed.");
				}
				catch (Exception ex)
				{
					failed++;
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("    FAILED: " + ex.Message);
					Console.ResetColor();
				}
			}

			Console.WriteLine("\n=========================================================");
			Console.WriteLine($"  Renamed          : {renamed}");
			Console.WriteLine($"  Already correct  : {already}");
			Console.WriteLine($"  Failed / refused : {failed}");
			Console.WriteLine("=========================================================");
		}

		/// <summary>
		/// Only attributes the platform will accept on Create. Calculated, rollup and read-only
		/// columns are refused outright, and there are enough of them on these two tables that
		/// hand-listing would go stale the next time someone adds a formula column.
		/// </summary>
		private static HashSet<string> CreatableAttributes(IOrganizationService service, string logicalName)
		{
			var req = new RetrieveEntityRequest
			{
				LogicalName = logicalName,
				EntityFilters = EntityFilters.Attributes,
				RetrieveAsIfPublished = true
			};

			var resp = (RetrieveEntityResponse)service.Execute(req);

			return new HashSet<string>(
				resp.EntityMetadata.Attributes
					.Where(a => a.IsValidForCreate == true && a.AttributeType != AttributeTypeCode.Virtual)
					.Select(a => a.LogicalName),
				StringComparer.OrdinalIgnoreCase);
		}

		private static Entity CopyFor(Entity source, HashSet<string> creatable, string[] clear)
		{
			var clone = new Entity(source.LogicalName);
			var clearSet = new HashSet<string>(clear, StringComparer.OrdinalIgnoreCase);

			foreach (var kv in source.Attributes)
			{
				if (SKIP_ALWAYS.Contains(kv.Key)) continue;
				if (clearSet.Contains(kv.Key)) continue;
				if (!creatable.Contains(kv.Key)) continue;
				if (kv.Value == null) continue;
				if (kv.Key.EndsWith("_base", StringComparison.OrdinalIgnoreCase)) continue; // currency shadow columns

				clone[kv.Key] = kv.Value;
			}

			return clone;
		}

		/// <summary>
		/// The source deal, by id when one is configured and by name otherwise.
		///
		/// The name lookup is only safe while the name is unique, and it stops being unique the
		/// moment this console runs: every clone carries the source's name until the follow-up
		/// rename lands. On the 2026-09-15 run the lookup returned a clone, and a live run would
		/// have cloned a clone. Prefer the id.
		/// </summary>
		private static Entity ResolveSourceDeal(IOrganizationService service)
		{
			Guid id;
			if (Guid.TryParse(SOURCE_DEAL_ID, out id))
			{
				try
				{
					Entity byId = service.Retrieve(DEAL_ENTITY, id, new ColumnSet(true));
					string name = byId.GetAttributeValue<string>("new_name");

					if (name != SOURCE_DEAL_NAME)
					{
						Console.ForegroundColor = ConsoleColor.Yellow;
						Console.WriteLine($"NOTE: SOURCE_DEAL_ID resolves to '{name}', not '{SOURCE_DEAL_NAME}'.");
						Console.WriteLine("The id wins. Fix SOURCE_DEAL_NAME if this is not what you meant.");
						Console.ResetColor();
					}

					return byId;
				}
				catch (Exception ex)
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("SOURCE_DEAL_ID could not be retrieved: " + ex.Message);
					Console.ResetColor();
					return null;
				}
			}

			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("SOURCE_DEAL_ID is not set - falling back to the name lookup, which is");
			Console.WriteLine("ambiguous if a previous run left unrenamed clones behind.");
			Console.ResetColor();

			return FindDealByName(service, SOURCE_DEAL_NAME);
		}

		private static Entity FindDealByName(IOrganizationService service, string name)
		{
			var q = new QueryExpression(DEAL_ENTITY)
			{
				ColumnSet = new ColumnSet(true),
				TopCount = 1
			};
			q.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);

			return service.RetrieveMultiple(q).Entities.FirstOrDefault();
		}

		private static List<Entity> GetDealLines(IOrganizationService service, Guid dealId)
		{
			var q = new QueryExpression(LINE_ENTITY) { ColumnSet = new ColumnSet(true) };
			q.Criteria.AddCondition(LINE_DEAL_LOOKUP, ConditionOperator.Equal, dealId);
			q.PageInfo = new PagingInfo { Count = 500, PageNumber = 1 };

			var all = new List<Entity>();
			while (true)
			{
				var page = service.RetrieveMultiple(q);
				all.AddRange(page.Entities);
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}

			return all;
		}

		/// <summary>
		/// The whole exercise turns on the season having MORE home games than the contract covers.
		/// If it does not, every tester types their numbers in and Incremental Contract Value comes
		/// back $0.00 - correct, but it looks broken and teaches them nothing.
		/// </summary>
		private static void WarnIfSeasonHasNoHeadroom(IOrganizationService service, Entity deal)
		{
			var seasonRef = deal.GetAttributeValue<EntityReference>("new_season")
						 ?? deal.GetAttributeValue<EntityReference>("new_basketballseason");

			if (seasonRef == null)
			{
				Console.WriteLine("Season          : (no season on the source deal - check this)");
				return;
			}

			try
			{
				var season = service.Retrieve(seasonRef.LogicalName, seasonRef.Id,
					new ColumnSet("new_name", "new_homegames", "new_awaygames"));

				int home = season.GetAttributeValue<int?>("new_homegames") ?? 0;
				Console.WriteLine($"Season          : {season.GetAttributeValue<string>("new_name")} - {home} home games");

				if (home <= 22)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine("  WARNING: the testers are told to enter 22 contracted home games. This season has");
					Console.WriteLine($"  {home}, so Incremental Contract Value will be $0.00 and the exercise proves nothing.");
					Console.WriteLine("  Use a source deal in a season with more home games, or change the instructions.");
					Console.ResetColor();
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine("Season          : could not be read - " + ex.Message);
			}
		}

		private static void ListCandidates(IOrganizationService service)
		{
			var q = new QueryExpression(DEAL_ENTITY)
			{
				ColumnSet = new ColumnSet("new_name"),
				TopCount = 25
			};
			q.Criteria.AddCondition("new_name", ConditionOperator.Like, "%Coho%");

			var rows = service.RetrieveMultiple(q).Entities;
			if (rows.Count == 0) return;

			Console.WriteLine("\nDeals matching 'Coho':");
			foreach (var r in rows)
				Console.WriteLine("  " + r.GetAttributeValue<string>("new_name"));
		}
	}
}
