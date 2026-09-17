using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CloseStuckTicketingOpps
{
	/// <summary>
	/// Closes the ticketing opportunities that are sitting in a closing stage but are still
	/// Open in Dataverse.
	///
	/// Bryan, via Ray: lost opportunities are not showing as closed.
	///
	/// ROOT CAUSE. The background flow "Close Ticketing Opportunity" owns this transition, and
	/// its trigger carries two conditions, not one:
	///
	///     new_ticketingstage IN (100000005, 100000006, 100000022, 100000029, 100000030)
	///     AND new_previousphonecallguid == null
	///
	/// An opportunity that was worked from a logged phone call has a value in
	/// new_previousphonecallguid, so the flow silently skips it: no run, no failure, no trace.
	/// The stage says Closed Lost, statecode stays 0 (Open). That is what this console cleans up.
	///
	/// It does NOT fix the flow. Whether that second condition can be dropped is a business
	/// question for Ray and Bryan - the same field drives a required-field rule on the form
	/// (new_opportunity_form.js, RULE 6), so it was put there on purpose by somebody.
	///
	/// WHAT IT DOES, per record, mirroring the flow's Switch exactly:
	///   100000005 Closed Won              -> WinOpportunity,  statuscode 3, actualrevenue = estimatedvalue
	///   100000022 9 - Experience Complete -> WinOpportunity,  statuscode 3, actualrevenue = estimatedvalue
	///   100000029 11 - Closed Auto Renewed-> WinOpportunity,  statuscode 3, actualrevenue = estimatedvalue
	///   100000006 Closed Lost             -> LoseOpportunity, statuscode 4, actualrevenue = 0
	///   100000030 12 - Closed Opted Out    -> LoseOpportunity, statuscode 4, actualrevenue = 0
	///
	/// On the two losing stages, if new_lostreason is empty it is stamped with Unknown
	/// (100000024) BEFORE the close, because a closed opportunity can no longer be updated.
	///
	/// The close runs as the connected user, not as the record's modifiedby. Audit history will
	/// say so.
	///
	/// A CSV of every record's state BEFORE the run is written first; the run aborts if it
	/// cannot be written. A second CSV with the per-record outcome is written at the end.
	///
	/// Start with DRY_RUN = true and read the report.
	/// </summary>
	internal class Program
	{
		// =====================================================================
		// CONFIGURATION - review every line before running
		// =====================================================================

		// PRODUCTION.
		private const string ENV_URL = "https://stormbasketball.crm.dynamics.com/";

		// true  = report what would happen and change nothing.
		// false = actually close the opportunities.
		private const bool DRY_RUN = true;

		// Stamp Lost Reason = Unknown when the field is empty on a losing stage.
		private const bool SET_UNKNOWN_LOST_REASON = true;
		private const int LOST_REASON_UNKNOWN = 100000024;

		// Where the CSVs go. The "before" CSV is written before the first change.
		private const string BACKUP_FOLDER = @"C:\Customer Docs\Storm\Ticketing";

		// =====================================================================

		private const string STAGE_FIELD = "new_ticketingstage";
		private const string PREV_CALL_FIELD = "new_previousphonecallguid";
		private const string LOST_REASON_FIELD = "new_lostreason";

		// stage -> true = win, false = lose. Same set the flow's trigger listens to.
		private static readonly Dictionary<int, bool> ClosingStages = new Dictionary<int, bool>
		{
			{ 100000005, true  }, // Closed Won
			{ 100000022, true  }, // 9 - Experience Complete
			{ 100000029, true  }, // 11 - Closed - Auto Renewed
			{ 100000006, false }, // Closed Lost
			{ 100000030, false }  // 12 - Closed - Opted Out
		};

		private class Row
		{
			public Guid Id;
			public string Name;
			public int Stage;
			public string StageLabel;
			public bool IsWin;
			public decimal EstimatedValue;
			public string PreviousCall;
			public int? LostReason;
			public string Owner;
			public string ModifiedBy;
			public DateTime Modified;

			// filled by the run
			public bool LostReasonStamped;
			public string Outcome = "";
			public string Error = "";
		}

		static void Main(string[] args)
		{
			Console.WriteLine("=========================================================");
			Console.WriteLine("  CLOSE STUCK TICKETING OPPORTUNITIES");
			Console.WriteLine("=========================================================");
			Console.WriteLine($"  Environment : {ENV_URL}");
			Console.WriteLine($"  Lost Reason : {(SET_UNKNOWN_LOST_REASON ? "stamp Unknown (" + LOST_REASON_UNKNOWN + ") when empty" : "leave as is")}");
			Console.Write("  Mode        : ");

			if (DRY_RUN)
			{
				Console.ForegroundColor = ConsoleColor.Cyan;
				Console.WriteLine("DRY RUN - nothing will be changed");
			}
			else
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("LIVE - opportunities will be CLOSED");
			}
			Console.ResetColor();

			if (ENV_URL.Contains("stormbasketball"))
			{
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine("\n  *** This is PRODUCTION. ***");
				Console.ResetColor();
			}

			Console.WriteLine("=========================================================");
			Console.Write(DRY_RUN ? "\nType YES to continue: " : "\nType CLOSE to continue: ");

			string expected = DRY_RUN ? "YES" : "CLOSE";
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
				List<Row> rows = FindStuckOpportunities(service);

				if (rows.Count == 0)
				{
					Console.WriteLine("No stuck opportunities found. Nothing to do.");
					Done();
					return;
				}

				Report(rows);

				// Written before anything changes. If it cannot be written, nothing runs.
				string backup = WriteCsv(rows, "BEFORE");
				if (backup == null)
				{
					Console.ForegroundColor = ConsoleColor.Red;
					Console.WriteLine("ABORT: the backup could not be written, so nothing was changed.");
					Console.ResetColor();
					Done();
					return;
				}
				Console.WriteLine($"\nBefore-state written: {backup}");

				if (DRY_RUN)
				{
					int wouldWin = rows.Count(r => r.IsWin);
					int wouldLose = rows.Count(r => !r.IsWin);
					int wouldStamp = rows.Count(r => !r.IsWin && !r.LostReason.HasValue);

					Console.WriteLine($"\nDRY RUN - would close {wouldWin} as Won and {wouldLose} as Lost.");
					if (SET_UNKNOWN_LOST_REASON)
						Console.WriteLine($"          would stamp Lost Reason = Unknown on {wouldStamp} record(s).");
					Done();
					return;
				}

				Console.WriteLine();

				int closed = 0, failed = 0, stamped = 0;

				foreach (Row r in rows)
				{
					try
					{
						// The stamp has to happen while the record is still Open.
						if (SET_UNKNOWN_LOST_REASON && !r.IsWin && !r.LostReason.HasValue)
						{
							var patch = new Entity("opportunity", r.Id);
							patch[LOST_REASON_FIELD] = new OptionSetValue(LOST_REASON_UNKNOWN);
							service.Update(patch);
							r.LostReasonStamped = true;
							stamped++;
						}

						Close(service, r);

						// Trust the record, not the response.
						Entity after = service.Retrieve("opportunity", r.Id, new ColumnSet("statecode", "statuscode"));
						int state = after.GetAttributeValue<OptionSetValue>("statecode")?.Value ?? -1;

						if (state == 0)
						{
							failed++;
							r.Outcome = "STILL OPEN";
							r.Error = "the close request returned without error but statecode is still 0";
							Console.ForegroundColor = ConsoleColor.Red;
							Console.WriteLine($"  STILL OPEN  {Trim(r.Name, 50)}");
							Console.ResetColor();
						}
						else
						{
							closed++;
							r.Outcome = r.IsWin ? "Closed Won" : "Closed Lost";
							Console.WriteLine($"  {(r.IsWin ? "won " : "lost"),-5}       {Trim(r.Name, 50)}{(r.LostReasonStamped ? "   (Lost Reason = Unknown)" : "")}");
						}
					}
					catch (Exception ex)
					{
						failed++;
						r.Outcome = "FAILED";
						r.Error = ex.Message;
						Console.ForegroundColor = ConsoleColor.Red;
						Console.WriteLine($"  FAILED      {Trim(r.Name, 50)}: {ex.Message}");
						Console.ResetColor();
					}
				}

				string results = WriteCsv(rows, "RESULT");

				Console.WriteLine("\n=========================================================");
				Console.WriteLine($"  Closed                  : {closed}");
				Console.WriteLine($"  Lost Reason stamped     : {stamped}");
				Console.WriteLine($"  Failed / still open     : {failed}");
				if (results != null) Console.WriteLine($"  Result CSV              : {results}");
				Console.WriteLine("=========================================================");

				if (failed > 0)
				{
					Console.ForegroundColor = ConsoleColor.Yellow;
					Console.WriteLine("\nRecords that did not close are usually held by a plugin or a required field on");
					Console.WriteLine("the close form. The message next to each one in the CSV is the server's own.");
					Console.ResetColor();
				}
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
		/// Open opportunities whose ticketing stage is one the flow should have closed.
		/// </summary>
		private static List<Row> FindStuckOpportunities(IOrganizationService service)
		{
			var q = new QueryExpression("opportunity")
			{
				ColumnSet = new ColumnSet("name", STAGE_FIELD, PREV_CALL_FIELD, LOST_REASON_FIELD,
										  "estimatedvalue", "ownerid", "modifiedby", "modifiedon",
										  "statecode")
			};
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // Open
			q.Criteria.AddCondition(STAGE_FIELD, ConditionOperator.In,
									ClosingStages.Keys.Cast<object>().ToArray());
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
				int stage = e.GetAttributeValue<OptionSetValue>(STAGE_FIELD)?.Value ?? 0;
				if (!ClosingStages.ContainsKey(stage)) continue;

				rows.Add(new Row
				{
					Id = e.Id,
					Name = e.GetAttributeValue<string>("name") ?? "(unnamed)",
					Stage = stage,
					StageLabel = e.FormattedValues.Contains(STAGE_FIELD) ? e.FormattedValues[STAGE_FIELD] : stage.ToString(),
					IsWin = ClosingStages[stage],
					EstimatedValue = e.GetAttributeValue<Money>("estimatedvalue")?.Value ?? 0m,
					PreviousCall = Raw(e, PREV_CALL_FIELD),
					LostReason = e.GetAttributeValue<OptionSetValue>(LOST_REASON_FIELD)?.Value,
					Owner = e.GetAttributeValue<EntityReference>("ownerid")?.Name ?? "",
					ModifiedBy = e.GetAttributeValue<EntityReference>("modifiedby")?.Name ?? "",
					Modified = e.GetAttributeValue<DateTime>("modifiedon").ToLocalTime()
				});
			}

			return rows.OrderBy(r => r.IsWin).ThenBy(r => r.Name).ToList();
		}

		/// <summary>
		/// The same two requests the flow performs, with the same statuscodes and revenue.
		/// </summary>
		private static void Close(IOrganizationService service, Row r)
		{
			var close = new Entity("opportunityclose");
			close["opportunityid"] = new EntityReference("opportunity", r.Id);
			close["actualend"] = DateTime.UtcNow;
			close["subject"] = "Closed by CloseStuckTicketingOpps";

			if (r.IsWin)
			{
				close["actualrevenue"] = new Money(r.EstimatedValue);
				service.Execute(new WinOpportunityRequest
				{
					OpportunityClose = close,
					Status = new OptionSetValue(3)
				});
			}
			else
			{
				close["actualrevenue"] = new Money(0m);
				service.Execute(new LoseOpportunityRequest
				{
					OpportunityClose = close,
					Status = new OptionSetValue(4)
				});
			}
		}

		private static void Report(List<Row> rows)
		{
			int withCall = rows.Count(r => !string.IsNullOrEmpty(r.PreviousCall));

			Console.WriteLine($"Open opportunities sitting in a closing stage: {rows.Count}");
			Console.WriteLine($"  of those, carrying a previous phone call GUID: {withCall}  <- the flow skips these\n");
			Console.WriteLine($"{"Opportunity",-52}{"Stage",-26}{"Action",-8}{"LostReason",-12}{"PrevCall",-10}Owner");

			foreach (Row r in rows)
			{
				if (!string.IsNullOrEmpty(r.PreviousCall)) Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine(
					$"{Trim(r.Name, 50),-52}" +
					$"{Trim(r.StageLabel, 24),-26}" +
					$"{(r.IsWin ? "Win" : "Lose"),-8}" +
					$"{(r.LostReason.HasValue ? r.LostReason.Value.ToString() : (r.IsWin ? "-" : "EMPTY")),-12}" +
					$"{(string.IsNullOrEmpty(r.PreviousCall) ? "no" : "yes"),-10}" +
					$"{Trim(r.Owner, 22)}");
				Console.ResetColor();
			}
		}

		private static string WriteCsv(List<Row> rows, string tag)
		{
			string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
			string path = Path.Combine(BACKUP_FOLDER, $"StuckTicketingOpps_{tag}_{stamp}.csv");

			try
			{
				var sb = new StringBuilder();
				sb.AppendLine("OpportunityId,Name,Stage,StageLabel,Action,EstimatedValue,LostReasonBefore,LostReasonStamped,PreviousPhoneCallGuid,Owner,ModifiedBy,ModifiedOn,Outcome,Error");

				foreach (Row r in rows)
				{
					sb.AppendLine(string.Join(",",
						Csv(r.Id.ToString()),
						Csv(r.Name),
						r.Stage.ToString(),
						Csv(r.StageLabel),
						r.IsWin ? "Win" : "Lose",
						r.EstimatedValue.ToString(CultureInfo.InvariantCulture),
						r.LostReason.HasValue ? r.LostReason.Value.ToString() : "",
						r.LostReasonStamped ? "Unknown" : "",
						Csv(r.PreviousCall),
						Csv(r.Owner),
						Csv(r.ModifiedBy),
						Csv(r.Modified.ToString("yyyy-MM-dd HH:mm")),
						Csv(r.Outcome),
						Csv(r.Error)));
				}

				File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
				return path;
			}
			catch (Exception ex)
			{
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine("CSV failed: " + ex.Message);
				Console.ResetColor();
				return null;
			}
		}

		/// <summary>
		/// new_previousphonecallguid is a text column today, but it is read defensively: the point
		/// is only whether it carries a value, and a type change should not break the cleanup.
		/// </summary>
		private static string Raw(Entity e, string field)
		{
			if (!e.Contains(field)) return "";
			object v = e[field];
			if (v == null) return "";
			if (v is EntityReference) return ((EntityReference)v).Id.ToString();
			return v.ToString();
		}

		private static string Csv(string s)
		{
			if (string.IsNullOrEmpty(s)) return "";
			return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
		}

		private static string Trim(string s, int n)
		{
			return string.IsNullOrEmpty(s) || s.Length <= n ? s : s.Substring(0, n - 1) + "...";
		}

		private static void Done()
		{
			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}
	}
}
