// ============================================================
//  Storm Basketball – Dynamics 365 Data Fix Tool
//  Purpose     : Fix Won Opportunities where actualrevenue is NULL
//                by copying the value from estimatedrevenue.
//  Environment : https://stormbasketball.crm.dynamics.com/
//  SDK         : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  .NET        : Framework 4.6.2
// ============================================================

using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Principal;
using System.Text;

namespace StormBasketball.DataFix
{
	// ================================================================
	//  Logger: writes simultaneously to console (with colors) + .txt
	// ================================================================
	internal sealed class Logger : IDisposable
	{
		private readonly StreamWriter _writer;
		private readonly object _lock = new object();

		public Logger(string filePath)
		{
			_writer = new StreamWriter(filePath, append: false, encoding: Encoding.UTF8)
			{
				AutoFlush = true
			};
			WriteLine($"[LOG START] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			WriteLine(new string('=', 72));
		}

		public void WriteLine(string message, ConsoleColor color = ConsoleColor.Gray)
		{
			lock (_lock)
			{
				Console.ForegroundColor = color;
				Console.WriteLine(message);
				Console.ResetColor();
				_writer.WriteLine(message);
			}
		}

		public void Info(string msg) => WriteLine($"[INFO]    {msg}", ConsoleColor.Cyan);
		public void Success(string msg) => WriteLine($"[SUCCESS] {msg}", ConsoleColor.Green);
		public void Warning(string msg) => WriteLine($"[WARNING] {msg}", ConsoleColor.Yellow);
		public void Error(string msg) => WriteLine($"[ERROR]   {msg}", ConsoleColor.Red);
		public void Step(string msg) => WriteLine($"  >> {msg}", ConsoleColor.DarkCyan);

		public void Dispose()
		{
			WriteLine(new string('=', 72));
			WriteLine($"[LOG END]  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			_writer.Dispose();
		}
	}

	// ================================================================
	//  Main program
	// ================================================================
	internal class Program
	{
		// ==============================================================
		//  CONFIGURATION – verify before running
		// ==============================================================
		private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";
		// ==============================================================

		static void Main(string[] args)
		{
			// ── Log file (timestamped so previous runs are never overwritten) ──
			string logFile = $"log_fix_actualrevenue_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
			using (Logger log = new Logger(logFile))
			{
				// ── Pre-execution warning / confirmation banner ──────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
				Console.WriteLine("║         STORM BASKETBALL – DYNAMICS 365  DATA FIX TOOL              ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine($"║  Environment  : {EnvUrl,-53}║");
				Console.WriteLine($"║  User         : {CrmUsername,-53}║");
				Console.WriteLine($"║  Log file     : {logFile,-53}║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  WHAT THIS TOOL DOES (per Opportunity found):                        ║");
				Console.WriteLine("║   1. Query all Won Opps where actualrevenue IS NULL                  ║");
				Console.WriteLine("║      and estimatedrevenue IS NOT NULL.                               ║");
				Console.WriteLine("║   2. Reopen the Opportunity → Active (statecode=0).                  ║");
				Console.WriteLine("║   3. Write estimatedrevenue value into actualrevenue.                 ║");
				Console.WriteLine("║   4. Update the linked opportunityclose record if it exists.          ║");
				Console.WriteLine("║   5. Re-close the Opportunity as Won (WinOpportunityRequest).         ║");
				Console.WriteLine("║   6. Restore the original actualclosedate so history is preserved.    ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  ⚠  This operation MODIFIES LIVE DATA. Verify the environment above. ║");
				Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
				Console.ResetColor();
				Console.WriteLine();
				Console.Write("DO YOU WANT TO PROCEED? Type YES and press Enter to confirm, or anything else to cancel: ");

				string input = Console.ReadLine() ?? string.Empty;
				if (!input.Trim().Equals("YES", StringComparison.OrdinalIgnoreCase))
				{
					log.Warning("Operation cancelled by the user. No changes were made.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				Console.WriteLine();
				log.Info("User confirmed execution. Starting...");

				// ── Connect to Dynamics 365 ──────────────────────────────
				string connectionString =
					$"AuthType=OAuth;" +
					$"Url={EnvUrl};" +
					$"Username={CrmUsername};" +
					$"Password={CrmPassword};" +
					$"AppId={AppId};" +
					$"RedirectUri={RedirectUri};" +
					$"LoginPrompt=Auto";

				log.Info("Connecting to Dynamics 365 via CrmServiceClient...");
				CrmServiceClient svc = new CrmServiceClient(connectionString);

				if (!svc.IsReady)
				{
					log.Error($"Connection failed: {svc.LastCrmError}");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}
				log.Success($"Connection established successfully. Organization: {svc.ConnectedOrgUniqueName}");
				Console.WriteLine();

				// ── Query: Won Opps with estimatedrevenue set but actualrevenue NULL ──
				log.Info("Querying Won Opportunities with NULL actualrevenue...");

				QueryExpression query = new QueryExpression("opportunity")
				{
					ColumnSet = new ColumnSet(
						"opportunityid",
						"name",
						"estimatedvalue",   // estimatedrevenue maps to estimatedvalue in the schema
						"actualvalue",      // actualrevenue maps to actualvalue in the schema
						"actualclosedate",
						"statecode",
						"statuscode"
					),
					NoLock = true
				};

				// statecode = 1 → Won
				query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 1);
				// estimatedrevenue (estimatedvalue) is NOT NULL
				query.Criteria.AddCondition("estimatedvalue", ConditionOperator.NotNull);
				// actualrevenue (actualvalue) is NULL
				query.Criteria.AddCondition("actualvalue", ConditionOperator.Null);

				// Retrieve all pages
				List<Entity> opportunities = RetrieveAllPages(svc, query, log);

				int total = opportunities.Count;
				int success = 0;
				int errors = 0;

				log.Info($"Total Opportunities found to fix: {total}");

				if (total == 0)
				{
					log.Success("Nothing to process. All Won Opportunities already have actualrevenue populated.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				Console.WriteLine();

				// ── Processing loop ──────────────────────────────────────
				for (int i = 0; i < opportunities.Count; i++)
				{
					Entity opp = opportunities[i];
					Guid oppId = opp.Id;
					string oppName = opp.Contains("name") ? (string)opp["name"] : "(no name)";

					// Read the revenue amount to copy
					Money revenueAmount = opp.Contains("estimatedvalue")
						? (Money)opp["estimatedvalue"]
						: null;

					// Read the original close date so we can restore it afterward
					DateTime? originalCloseDate = opp.Contains("actualclosedate")
						? (DateTime?)opp["actualclosedate"]
						: null;

					log.WriteLine(new string('-', 72));
					log.Info($"[{i + 1}/{total}] Processing Opportunity: {oppId} | '{oppName}'");
					log.Step($"estimatedrevenue = {revenueAmount?.Value.ToString("C") ?? "N/A"} | originalCloseDate = {originalCloseDate?.ToString("yyyy-MM-dd") ?? "N/A"}");

					if (revenueAmount == null)
					{
						log.Warning($"  estimatedrevenue is null at runtime for {oppId}. Skipping.");
						errors++;
						continue;
					}

					try
					{
						// ── STEP A: Reopen the Opportunity → Active ──────
						log.Step("Step A: Reopening Opportunity to Active state...");
						SetStateRequest reopenRequest = new SetStateRequest
						{
							EntityMoniker = new EntityReference("opportunity", oppId),
							State = new OptionSetValue(0),   // 0 = Active
							Status = new OptionSetValue(1)    // 1 = In Progress
						};
						svc.Execute(reopenRequest);
						log.Step("Step A: Opportunity set to Active.");

						// ── STEP B: Update actualrevenue on the Opportunity ──
						log.Step("Step B: Writing actualrevenue on the Opportunity record...");
						Entity oppUpdate = new Entity("opportunity", oppId);
						oppUpdate["actualvalue"] = revenueAmount;  // actualrevenue = actualvalue
						svc.Update(oppUpdate);
						log.Step($"Step B: actualrevenue set to {revenueAmount.Value:C}.");

						// ── STEP C: Find and update the opportunityclose record ──
						log.Step("Step C: Looking for associated opportunityclose record...");
						Entity oppCloseRecord = FindOpportunityClose(svc, oppId, log);

						if (oppCloseRecord != null)
						{
							Entity closeUpdate = new Entity("opportunityclose", oppCloseRecord.Id);
							closeUpdate["actualrevenue"] = revenueAmount;
							// Preserve the original close date on the activity too
							if (originalCloseDate.HasValue)
								closeUpdate["actualend"] = originalCloseDate.Value;

							svc.Update(closeUpdate);
							log.Step($"Step C: opportunityclose {oppCloseRecord.Id} actualrevenue updated.");
						}
						else
						{
							log.Step("Step C: No opportunityclose record found. A new one will be created by WinOpportunityRequest.");
						}

						// ── STEP D: Re-close the Opportunity as Won ──────
						// Build the opportunityclose entity for WinOpportunityRequest.
						// If a close record already existed, pass its ID so D365 reuses it;
						// otherwise let the platform create a fresh one.
						log.Step("Step D: Closing Opportunity as Won (WinOpportunityRequest)...");

						Entity closeActivity = new Entity("opportunityclose");
						closeActivity["opportunityid"] = new EntityReference("opportunity", oppId);
						closeActivity["actualrevenue"] = revenueAmount;

						// Restore the original close date so history is preserved
						if (originalCloseDate.HasValue)
							closeActivity["actualend"] = originalCloseDate.Value;

						// If we found an existing opportunityclose, attach its ID
						// so the request targets that record rather than creating a duplicate.
						if (oppCloseRecord != null)
							closeActivity["activityid"] = oppCloseRecord.Id;

						WinOpportunityRequest winRequest = new WinOpportunityRequest
						{
							OpportunityClose = closeActivity,
							Status = new OptionSetValue(3)  // 3 = Won
						};
						svc.Execute(winRequest);
						log.Step("Step D: WinOpportunityRequest executed successfully.");

						// ── STEP E: Restore the original actualclosedate ─
						// WinOpportunityRequest may overwrite actualclosedate with today's date.
						// We patch it back to preserve the historical close date.
						if (originalCloseDate.HasValue)
						{
							log.Step("Step E: Restoring original actualclosedate...");

							// To patch a closed opportunity we need to reopen briefly again,
							// update the date, then re-close. However, D365 allows updating
							// actualclosedate directly after a Win via a direct Update call
							// because the field is not locked in the same way. We try the
							// direct patch first; if it fails, we log a warning.
							try
							{
								Entity dateRestore = new Entity("opportunity", oppId);
								dateRestore["actualclosedate"] = originalCloseDate.Value;
								svc.Update(dateRestore);
								log.Step($"Step E: actualclosedate restored to {originalCloseDate.Value:yyyy-MM-dd}.");
							}
							catch (Exception exDate)
							{
								// Non-fatal: the revenue is already fixed; only the date patch failed.
								log.Warning($"Step E: Could not restore actualclosedate for {oppId}. " +
											$"Manual review may be needed. Detail: {exDate.Message}");
							}
						}

						log.Success($"[{i + 1}/{total}] FIXED → Opportunity {oppId} | Revenue: {revenueAmount.Value:C}");
						success++;
					}
					catch (Exception ex)
					{
						// Log the full error and continue to the next record
						log.Error($"[{i + 1}/{total}] FAILED → Opportunity {oppId} | '{oppName}'");
						log.Error($"  Message    : {ex.Message}");
						if (ex.InnerException != null)
							log.Error($"  Inner      : {ex.InnerException.Message}");
						log.Error($"  StackTrace : {ex.StackTrace}");
						errors++;

						// Safety: attempt to re-close the opportunity if it was left Open
						// to avoid leaving records in an unintended Active state.
						TryReWinOnError(svc, oppId, revenueAmount, originalCloseDate, log);
					}
				}

				// ── Summary ──────────────────────────────────────────────
				Console.WriteLine();
				log.WriteLine(new string('=', 72));
				log.Info($"EXECUTION COMPLETE");
				log.Info($"  Total records found   : {total}");
				log.Success($"  Successfully fixed    : {success}");
				if (errors > 0)
					log.Error($"  Errors / skipped      : {errors}");
				else
					log.Info($"  Errors / skipped      : {errors}");
				log.Info($"  Log saved to          : {logFile}");
				log.WriteLine(new string('=', 72));

				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
			}
		}

		// ==============================================================
		//  Helper: retrieve ALL pages from a QueryExpression
		// ==============================================================
		private static List<Entity> RetrieveAllPages(
			CrmServiceClient svc,
			QueryExpression query,
			Logger log)
		{
			List<Entity> all = new List<Entity>();
			query.PageInfo = new PagingInfo
			{
				Count = 5000,
				PageNumber = 1,
				PagingCookie = null
			};

			while (true)
			{
				EntityCollection results = svc.RetrieveMultiple(query);
				all.AddRange(results.Entities);
				log.Info($"  Page {query.PageInfo.PageNumber}: {results.Entities.Count} records retrieved (running total: {all.Count}).");

				if (!results.MoreRecords)
					break;

				query.PageInfo.PageNumber++;
				query.PageInfo.PagingCookie = results.PagingCookie;
			}

			return all;
		}

		// ==============================================================
		//  Helper: find an existing opportunityclose record for an Opp
		// ==============================================================
		private static Entity FindOpportunityClose(
			CrmServiceClient svc,
			Guid opportunityId,
			Logger log)
		{
			try
			{
				QueryExpression qe = new QueryExpression("opportunityclose")
				{
					ColumnSet = new ColumnSet(
						"activityid",
						"actualrevenue",
						"actualend",
						"subject"
					),
					TopCount = 1,
					NoLock = true
				};
				qe.Criteria.AddCondition("opportunityid", ConditionOperator.Equal, opportunityId);

				EntityCollection results = svc.RetrieveMultiple(qe);

				if (results.Entities.Count > 0)
				{
					log.Step($"  opportunityclose found: {results.Entities[0].Id}");
					return results.Entities[0];
				}

				return null;
			}
			catch (Exception ex)
			{
				log.Warning($"  Could not query opportunityclose for Opp {opportunityId}: {ex.Message}");
				return null;
			}
		}

		// ==============================================================
		//  Safety net: if an error occurs mid-loop and the Opportunity
		//  was reopened (Active), try to re-close it as Won so it is
		//  not left in an Active state accidentally.
		// ==============================================================
		private static void TryReWinOnError(
			CrmServiceClient svc,
			Guid opportunityId,
			Money revenue,
			DateTime? originalCloseDate,
			Logger log)
		{
			try
			{
				// Check current state
				Entity current = svc.Retrieve(
					"opportunity",
					opportunityId,
					new ColumnSet("statecode"));

				OptionSetValue state = current.Contains("statecode")
					? (OptionSetValue)current["statecode"]
					: null;

				// Only act if the record is currently Active (statecode = 0)
				if (state != null && state.Value == 0)
				{
					log.Warning($"  Safety net: Opportunity {opportunityId} is still Active after error. Attempting to re-close as Won...");

					Entity safeClose = new Entity("opportunityclose");
					safeClose["opportunityid"] = new EntityReference("opportunity", opportunityId);

					if (revenue != null)
						safeClose["actualrevenue"] = revenue;

					if (originalCloseDate.HasValue)
						safeClose["actualend"] = originalCloseDate.Value;

					WinOpportunityRequest safeWin = new WinOpportunityRequest
					{
						OpportunityClose = safeClose,
						Status = new OptionSetValue(3)
					};
					svc.Execute(safeWin);
					log.Warning($"  Safety net: Opportunity {opportunityId} successfully re-closed as Won.");
				}
			}
			catch (Exception exSafe)
			{
				log.Error($"  Safety net FAILED for {opportunityId}: {exSafe.Message}");
				log.Error($"  ⚠ This record may be left in Active state. Manual intervention required.");
			}
		}
	}
}
