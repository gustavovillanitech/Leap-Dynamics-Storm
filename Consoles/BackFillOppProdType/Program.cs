// ============================================================
//  Storm Basketball – Dynamics 365 Data Fix Tool
//  Purpose     : Backfill Product Type Detail = "Storm 360 Membership"
//                on Closed Won opportunities whose Product Type is
//                "Membership - New" or "Membership - Renewal".
//  Environment : https://stormbasketball.crm.dynamics.com/
//  SDK         : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  .NET        : Framework 4.6.2
//
//  Ray's request (EmailRequest.pdf, Jul 13 2026):
//    "Backfill all the opportunities that have Membership - New and
//     Membership - Renewal as the product type that are closed won with
//     the new Storm 360 Membership option for the product type detail."
//
//  NOTE ON CLOSED WON:
//    Setting new_ticketingstage = "Closed Won" triggers a flow that
//    closes the opportunity as Won natively (statecode = 1 / inactive).
//    Inactive opportunities are read-only, so for those we must:
//        reopen (Active) -> update detail -> re-close as Won -> restore
//        the original actualclosedate.
//    Records still Active are updated directly.
// ============================================================

using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BackFillOppProdType
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
		//  (Same environment/credentials pattern as the other Storm
		//   data-fix consoles. Point at SANDBOX first to test.)
		// ==============================================================
		private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
		//private const string EnvUrl = "https://org00bff505.crm.dynamics.com/";       // <-- SANDBOX first!
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// ==============================================================
		//  BUSINESS RULES – option set values (from field inventory)
		// ==============================================================
		private const string ProductTypeField = "new_producttype";
		private const string ProductTypeDetailField = "new_producttypedetail";
		private const string TicketingStageField = "new_ticketingstage";

		private const int ProductType_MembershipNew = 100000007;      // Membership - New
		private const int ProductType_MembershipRenewal = 100000002;  // Membership - Renewal
		private const int TicketingStage_ClosedWon = 100000005;       // Closed Won
		private const int Detail_Storm360Membership = 100000027;      // NEW option (confirmed by Gustavo)
		// ==============================================================

		static void Main(string[] args)
		{
			string logFile = $"log_backfill_producttypedetail_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
			using (Logger log = new Logger(logFile))
			{
				// ── Pre-execution warning / confirmation banner ──────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|      STORM BASKETBALL - PRODUCT TYPE DETAIL BACKFILL TOOL             |");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine($"|  Environment  : {EnvUrl,-52}|");
				Console.WriteLine($"|  User         : {CrmUsername,-52}|");
				Console.WriteLine($"|  Log file     : {logFile,-52}|");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|  WHAT THIS TOOL DOES (per Opportunity found):                        |");
				Console.WriteLine("|   Target = Ticketing Stage 'Closed Won' AND Product Type in          |");
				Console.WriteLine("|            (Membership - New, Membership - Renewal).                  |");
				Console.WriteLine("|   1. Set Product Type Detail = 'Storm 360 Membership'.               |");
				Console.WriteLine("|   2. If the Opp is Won (inactive): reopen -> update -> re-Win ->      |");
				Console.WriteLine("|      restore the original actualclosedate.                           |");
				Console.WriteLine("|   3. If the Opp is still Active: update the field directly.          |");
				Console.WriteLine("|   Records already set to 'Storm 360 Membership' are skipped.         |");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|  !! This operation MODIFIES LIVE DATA. Verify the environment above. |");
				Console.WriteLine("+======================================================================+");
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
				log.Success($"Connection established. Organization: {svc.ConnectedOrgUniqueName}");
				Console.WriteLine();

				// ── Query: Closed Won opps with the two Membership product types ──
				log.Info("Querying Closed Won opportunities with Membership - New / Renewal...");

				QueryExpression query = new QueryExpression("opportunity")
				{
					ColumnSet = new ColumnSet(
						"opportunityid",
						"name",
						ProductTypeField,
						ProductTypeDetailField,
						TicketingStageField,
						"actualvalue",       // preserved when re-winning
						"actualclosedate",   // preserved when re-winning
						"statecode",
						"statuscode"
					),
					NoLock = true
				};

				// Ticketing Stage = Closed Won  (Gustavo: this is the definition of "Closed Won")
				query.Criteria.AddCondition(TicketingStageField, ConditionOperator.Equal, TicketingStage_ClosedWon);
				// Product Type in (Membership - New, Membership - Renewal)
				query.Criteria.AddCondition(ProductTypeField, ConditionOperator.In,
					ProductType_MembershipNew, ProductType_MembershipRenewal);

				List<Entity> opportunities = RetrieveAllPages(svc, query, log);

				int total = opportunities.Count;
				int success = 0;
				int skipped = 0;
				int errors = 0;

				log.Info($"Total Opportunities matching the criteria: {total}");

				if (total == 0)
				{
					log.Success("Nothing to process.");
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

					int? currentDetail = opp.Contains(ProductTypeDetailField)
						? ((OptionSetValue)opp[ProductTypeDetailField]).Value
						: (int?)null;

					int stateCode = opp.Contains("statecode")
						? ((OptionSetValue)opp["statecode"]).Value
						: 0;

					Money actualRevenue = opp.Contains("actualvalue") ? (Money)opp["actualvalue"] : null;
					DateTime? originalCloseDate = opp.Contains("actualclosedate")
						? (DateTime?)opp["actualclosedate"]
						: null;

					log.WriteLine(new string('-', 72));
					log.Info($"[{i + 1}/{total}] Opportunity: {oppId} | '{oppName}' | statecode={stateCode} | currentDetail={(currentDetail?.ToString() ?? "NULL")}");

					// Skip if already correct (idempotent re-runs)
					if (currentDetail == Detail_Storm360Membership)
					{
						log.Step("Already set to 'Storm 360 Membership'. Skipping.");
						skipped++;
						continue;
					}

					try
					{
						if (stateCode == 1) // Won / inactive -> reopen, update, re-win, restore date
						{
							// STEP A: Reopen to Active
							log.Step("Step A: Reopening Opportunity to Active state...");
							svc.Execute(new SetStateRequest
							{
								EntityMoniker = new EntityReference("opportunity", oppId),
								State = new OptionSetValue(0),   // 0 = Active
								Status = new OptionSetValue(1)    // 1 = In Progress
							});

							// STEP B: Update Product Type Detail
							log.Step("Step B: Setting Product Type Detail = Storm 360 Membership...");
							Entity upd = new Entity("opportunity", oppId);
							upd[ProductTypeDetailField] = new OptionSetValue(Detail_Storm360Membership);
							svc.Update(upd);

							// STEP C: Re-close as Won, preserving revenue + close date
							log.Step("Step C: Re-closing Opportunity as Won...");
							Entity closeActivity = new Entity("opportunityclose");
							closeActivity["opportunityid"] = new EntityReference("opportunity", oppId);
							if (actualRevenue != null) closeActivity["actualrevenue"] = actualRevenue;
							if (originalCloseDate.HasValue) closeActivity["actualend"] = originalCloseDate.Value;

							Entity existingClose = FindOpportunityClose(svc, oppId, log);
							if (existingClose != null) closeActivity["activityid"] = existingClose.Id;

							svc.Execute(new WinOpportunityRequest
							{
								OpportunityClose = closeActivity,
								Status = new OptionSetValue(3)  // 3 = Won
							});

							// STEP D: Restore original actualclosedate (Win may overwrite it)
							if (originalCloseDate.HasValue)
							{
								try
								{
									Entity dateRestore = new Entity("opportunity", oppId);
									dateRestore["actualclosedate"] = originalCloseDate.Value;
									svc.Update(dateRestore);
									log.Step($"Step D: actualclosedate restored to {originalCloseDate.Value:yyyy-MM-dd}.");
								}
								catch (Exception exDate)
								{
									log.Warning($"Step D: Could not restore actualclosedate for {oppId}. Detail: {exDate.Message}");
								}
							}
						}
						else // Active -> direct update
						{
							log.Step("Opportunity is Active. Updating Product Type Detail directly...");
							Entity upd = new Entity("opportunity", oppId);
							upd[ProductTypeDetailField] = new OptionSetValue(Detail_Storm360Membership);
							svc.Update(upd);
						}

						log.Success($"[{i + 1}/{total}] FIXED -> Opportunity {oppId} | Detail set to Storm 360 Membership.");
						success++;
					}
					catch (Exception ex)
					{
						log.Error($"[{i + 1}/{total}] FAILED -> Opportunity {oppId} | '{oppName}'");
						log.Error($"  Message    : {ex.Message}");
						if (ex.InnerException != null)
							log.Error($"  Inner      : {ex.InnerException.Message}");
						errors++;

						// Safety net: if we reopened it and then failed, don't leave it Active
						TryReWinOnError(svc, oppId, actualRevenue, originalCloseDate, log);
					}
				}

				// ── Summary ──────────────────────────────────────────────
				Console.WriteLine();
				log.WriteLine(new string('=', 72));
				log.Info("EXECUTION COMPLETE");
				log.Info($"  Total records found   : {total}");
				log.Success($"  Successfully fixed    : {success}");
				log.Info($"  Skipped (already set) : {skipped}");
				if (errors > 0) log.Error($"  Errors                : {errors}");
				else log.Info($"  Errors                : {errors}");
				log.Info($"  Log saved to          : {logFile}");
				log.WriteLine(new string('=', 72));

				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
			}
		}

		// ==============================================================
		//  Helper: retrieve ALL pages from a QueryExpression
		// ==============================================================
		private static List<Entity> RetrieveAllPages(CrmServiceClient svc, QueryExpression query, Logger log)
		{
			List<Entity> all = new List<Entity>();
			query.PageInfo = new PagingInfo { Count = 5000, PageNumber = 1, PagingCookie = null };

			while (true)
			{
				EntityCollection results = svc.RetrieveMultiple(query);
				all.AddRange(results.Entities);
				log.Info($"  Page {query.PageInfo.PageNumber}: {results.Entities.Count} records (running total: {all.Count}).");

				if (!results.MoreRecords) break;

				query.PageInfo.PageNumber++;
				query.PageInfo.PagingCookie = results.PagingCookie;
			}

			return all;
		}

		// ==============================================================
		//  Helper: find an existing opportunityclose record for an Opp
		// ==============================================================
		private static Entity FindOpportunityClose(CrmServiceClient svc, Guid opportunityId, Logger log)
		{
			try
			{
				QueryExpression qe = new QueryExpression("opportunityclose")
				{
					ColumnSet = new ColumnSet("activityid", "actualrevenue", "actualend", "subject"),
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
		//  Safety net: if an error occurs after reopening, re-close as Won
		//  so the record is not left in an unintended Active state.
		// ==============================================================
		private static void TryReWinOnError(CrmServiceClient svc, Guid opportunityId, Money revenue, DateTime? originalCloseDate, Logger log)
		{
			try
			{
				Entity current = svc.Retrieve("opportunity", opportunityId, new ColumnSet("statecode"));
				OptionSetValue state = current.Contains("statecode") ? (OptionSetValue)current["statecode"] : null;

				if (state != null && state.Value == 0) // Active
				{
					log.Warning($"  Safety net: Opportunity {opportunityId} is Active after error. Re-closing as Won...");

					Entity safeClose = new Entity("opportunityclose");
					safeClose["opportunityid"] = new EntityReference("opportunity", opportunityId);
					if (revenue != null) safeClose["actualrevenue"] = revenue;
					if (originalCloseDate.HasValue) safeClose["actualend"] = originalCloseDate.Value;

					svc.Execute(new WinOpportunityRequest
					{
						OpportunityClose = safeClose,
						Status = new OptionSetValue(3)
					});
					log.Warning($"  Safety net: Opportunity {opportunityId} re-closed as Won.");
				}
			}
			catch (Exception exSafe)
			{
				log.Error($"  Safety net FAILED for {opportunityId}: {exSafe.Message}");
				log.Error($"  !! This record may be left Active. Manual intervention required.");
			}
		}
	}
}
