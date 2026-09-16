using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace DeletePlayoffDeals
{
	/// <summary>
	/// Removes the playoff deals that FLUJO 3 created automatically for a season that never
	/// qualified.
	///
	/// Christine, via Ray: the 2026 playoff deals should not exist. The team did not make the
	/// playoffs, and the deals were created at contract time by the old behaviour of
	/// DealOptionAutomation - see §20.6.
	///
	/// PREREQUISITE, NOT OPTIONAL: the "Playoffs Qualified" guard must already be live in this
	/// environment. Without it, the next status change on any parent deal recreates exactly what
	/// this console deletes. The console checks for the field and refuses to run if it is missing.
	///
	/// DELETE IS NOT REVERSIBLE. There is no RestoreFromBackup here and there cannot be: a deleted
	/// record cannot be updated back into existence. The CSV this writes is the only trace left, so
	/// it is written BEFORE anything is deleted and the run aborts if it cannot be written.
	///
	/// A playoff deal carrying deal lines is REFUSED, always, in every mode. Lines mean somebody
	/// put inventory on it, and that is a conversation, not a cleanup.
	///
	/// Start with DRY_RUN = true and read the report.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// PRODUCTION. This console deletes. Read this line out loud before running it live.
		private const string ENV_URL = "https://stormbasketball.crm.dynamics.com/";

		// true  = report what would happen and delete nothing.
		// false = actually delete.
		private const bool DRY_RUN = true;

		// Only playoff deals whose parent deal sits in this season are considered.
		private const string SEASON_NAME = "2026 - Storm";

		// Where the backup CSV goes. Written before the first delete; the run aborts if it fails.
		private const string BACKUP_FOLDER = @"C:\Customer Docs\Storm\Deal Options And PlayOff Automation";

		// =====================================================================

		private const string DEAL_ENTITY = "new_deals";
		private const string LINE_ENTITY = "new_deallines";
		private const string LINE_DEAL_LOOKUP = "new_dealid";
		private const string PLAYOFF_PARENT = "new_regularseasondeal";
		private const string QUALIFIED_FIELD = "new_playoffsqualified";

		private class Row
		{
			public Guid Id;
			public string Name;
			public string ParentName;
			public string Season;
			public string Status;
			public decimal Total;
			public int Lines;
			public DateTime Created;
			public string CreatedBy;
		}

		static void Main(string[] args)
		{
			Console.WriteLine("=========================================================");
			Console.WriteLine("  PLAYOFF DEAL CLEANUP");
			Console.WriteLine("=========================================================");
			Console.WriteLine($"  Environment : {ENV_URL}");
			Console.WriteLine($"  Season      : {SEASON_NAME}");
			Console.Write("  Mode        : ");

			if (DRY_RUN)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("DRY RUN - nothing will be deleted");
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("LIVE - records will be PERMANENTLY DELETED");
			}
			Console.ResetColor();

			if (ENV_URL.Contains("stormbasketball"))
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("\n  *** This is PRODUCTION. ***");
				Console.ResetColor();
			}

			Console.WriteLine("=========================================================");
			Console.Write(DRY_RUN ? "\nType YES to continue: " : "\nType DELETE to continue: ");

			string expected = DRY_RUN ? "YES" : "DELETE";
			if ((Console.ReadLine() ?? string.Empty).Trim().ToUpperInvariant() != expected)
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
				if (!GuardIsLive(service))
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("ABORT: the 'Playoffs Qualified' guard is not live in this environment.");
					Console.WriteLine("Deleting now would be undone by the next status change on any parent deal.");
					Console.WriteLine("Ship the field and the assembly first (§20.6), then run this.");
					Console.ResetColor();
					Done();
					return;
				}

				List<Row> rows = FindPlayoffDeals(service);

				if (rows.Count == 0)
				{
					Console.WriteLine($"No playoff deals found for {SEASON_NAME}. Nothing to do.");
					Done();
					return;
				}

				Report(rows);

				List<Row> deletable = rows.Where(r => r.Lines == 0).ToList();
				List<Row> refused = rows.Where(r => r.Lines > 0).ToList();

				if (refused.Count > 0)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine($"\n{refused.Count} playoff deal(s) carry deal lines and will NOT be deleted:");
					foreach (Row r in refused)
						Console.WriteLine($"  {r.Name}  ({r.Lines} line(s), {r.Total:C})");
					Console.WriteLine("Someone put inventory on these. Take them to Storm before removing anything.");
					Console.ResetColor();
				}

				if (DRY_RUN)
				{
					Console.WriteLine($"\nDRY RUN - would delete {deletable.Count}, refuse {refused.Count}.");
					WriteBackup(rows, preview: true);
					Done();
					return;
				}

				// The CSV is the only record that survives a delete, so it is written first and a
				// failure here stops the run rather than being reported afterwards.
				string backup = WriteBackup(rows, preview: false);
				if (backup == null)
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("ABORT: the backup could not be written, so nothing was deleted.");
					Console.ResetColor();
					Done();
					return;
				}

				Console.WriteLine($"Backup written: {backup}\n");

				int deleted = 0, failed = 0;

				foreach (Row r in deletable)
				{
					try
					{
						service.Delete(DEAL_ENTITY, r.Id);
						deleted++;
						Console.WriteLine($"  deleted  {r.Name}");
					}
					catch (Exception ex)
					{
						failed++;
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine($"  FAILED   {r.Name}: {ex.Message}");
						Console.ResetColor();
					}
				}

				Console.WriteLine("\n=========================================================");
				Console.WriteLine($"  Deleted           : {deleted}");
				Console.WriteLine($"  Refused (lines)   : {refused.Count}");
				Console.WriteLine($"  Failed            : {failed}");
				Console.WriteLine("=========================================================");
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
		/// Refuses to run unless the guard field exists. Deleting these deals while FLUJO 3 still
		/// creates them on any status change is not a cleanup, it is a loop.
		/// </summary>
		private static bool GuardIsLive(IOrganizationService service)
		{
			try
			{
				var q = new QueryExpression("new_season")
				{
					ColumnSet = new ColumnSet(QUALIFIED_FIELD),
					TopCount = 1
				};
				service.RetrieveMultiple(q);
				return true;
			}
			catch (Exception ex)
			{
				Console.WriteLine("Guard check failed: " + ex.Message);
				return false;
			}
		}

		private static List<Row> FindPlayoffDeals(IOrganizationService service)
		{
			// A playoff deal is one that points back at a regular-season deal. Matching on the
			// " - Playoffs" name suffix would also work but names can be edited; the lookup cannot.
			var q = new QueryExpression(DEAL_ENTITY)
			{
				ColumnSet = new ColumnSet("new_name", PLAYOFF_PARENT, "new_season", "new_dealstatus",
										  "new_total", "createdon", "createdby")
			};
			q.Criteria.AddCondition(PLAYOFF_PARENT, ConditionOperator.NotNull);
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

			var rows = new List<Row>();

			foreach (Entity e in all)
			{
				var season = e.GetAttributeValue<EntityReference>("new_season");
				string seasonName = season?.Name ?? "";

				if (!string.Equals(seasonName, SEASON_NAME, StringComparison.OrdinalIgnoreCase))
					continue;

				rows.Add(new Row
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>("new_name") ?? "(unnamed)",
					ParentName = e.GetAttributeValue<EntityReference>(PLAYOFF_PARENT)?.Name ?? "",
					Season = seasonName,
					Status = e.GetAttributeValue<EntityReference>("new_dealstatus")?.Name ?? "",
					Total = e.GetAttributeValue<Money>("new_total")?.Value ?? 0m,
					Created = e.GetAttributeValue<DateTime>("createdon").ToLocalTime(),
					CreatedBy = e.GetAttributeValue<EntityReference>("createdby")?.Name ?? "",
					Lines = CountLines(service, e.Id)
				});
			}

			return rows.OrderBy(r => r.Name).ToList();
		}

		private static int CountLines(IOrganizationService service, Guid dealId)
		{
			var q = new QueryExpression(LINE_ENTITY) { ColumnSet = new ColumnSet(false) };
			q.Criteria.AddCondition(LINE_DEAL_LOOKUP, ConditionOperator.Equal, dealId);
			q.PageInfo = new PagingInfo { Count = 500, PageNumber = 1 };

			int n = 0;
			while (true)
			{
				var page = service.RetrieveMultiple(q);
				n += page.Entities.Count;
				if (!page.MoreRecords) break;
				q.PageInfo.PageNumber++;
				q.PageInfo.PagingCookie = page.PagingCookie;
			}

			return n;
		}

		private static void Report(List<Row> rows)
		{
			Console.WriteLine($"Playoff deals in {SEASON_NAME}: {rows.Count}\n");
			Console.WriteLine($"{"Deal",-52}{"Lines",6}{"Total",14}  {"Status",-16}Created");

			foreach (Row r in rows)
			{
				if (r.Lines > 0) Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine($"{Trim(r.Name, 50),-52}{r.Lines,6}{r.Total,14:C}  {Trim(r.Status, 14),-16}{r.Created:yyyy-MM-dd} {r.CreatedBy}");
				Console.ResetColor();
			}
		}

		private static string WriteBackup(List<Row> rows, bool preview)
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string path = Path.Combine(BACKUP_FOLDER, $"PlayoffDeals_BACKUP_{stamp}.csv");

			try
			{
				var sb = new StringBuilder();
				sb.AppendLine("DealId,Name,ParentDeal,Season,Status,Total,DealLines,CreatedOn,CreatedBy");

				foreach (Row r in rows)
				{
					sb.AppendLine(string.Join(",",
						Csv(r.Id.ToString()), Csv(r.Name), Csv(r.ParentName), Csv(r.Season),
						Csv(r.Status), r.Total.ToString(CultureInfo.InvariantCulture),
						r.Lines.ToString(), Csv(r.Created.ToString("yyyy-MM-dd HH:mm")), Csv(r.CreatedBy)));
				}

				File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

				if (preview) Console.WriteLine($"\nBackup (dry run) written: {path}");
				return path;
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("Backup failed: " + ex.Message);
				Console.ResetColor();
				return null;
			}
		}

		private static string Csv(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
		}

		private static string Trim(string s, int n)
		{
			return string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "…";
		}

		private static void Done()
		{
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}
	}
}
