// ============================================================
//  Storm Basketball – Dynamics 365 Cleanup Tool
//  Purpose     : Delete the duplicate opportunities that the
//                "AutomatedOpportunityCreationRenewal" flow created
//                when the backfill re-closed 72 Won opportunities on
//                2026-07-13 (burst 18:32-18:37 UTC).
//  Environment : https://stormbasketball.crm.dynamics.com/
//  SDK         : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  .NET        : Framework 4.6.2
//
//  INPUT       : created_opp_ids_0713.txt  (one opportunity GUID per line,
//                extracted from the flow run-history CSV).
//
//  SAFETY:
//    - HARD DELETE (permanent), as requested.
//    - Two phases in one run: (1) DRY RUN that retrieves and validates
//      every ID, then (2) real deletion only after typing DELETE.
//    - GUARDRAIL: a record is deleted ONLY if its createdon falls inside
//      the 2026-07-13 backfill window. Anything outside is SKIPPED, so a
//      stray/legit ID can never be removed by mistake.
// ============================================================

using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CleanupDuplicateOpps
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
			_writer = new StreamWriter(filePath, append: false, encoding: Encoding.UTF8) { AutoFlush = true };
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

	internal class Program
	{
		// ==============================================================
		//  CONFIGURATION – verify before running
		//  (Point at SANDBOX first to test.)
		// ==============================================================
		private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
		//private const string EnvUrl = "https://org00bff505.crm.dynamics.com/";   // <-- SANDBOX
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// ==============================================================
		//  GUARDRAIL – only delete records created inside this window.
		//  Flow burst was 2026-07-13 18:32-18:37 UTC; padded a little.
		//  createdon in Dataverse is stored/returned in UTC.
		// ==============================================================
		private const string IdsFileName = "created_opp_ids_0713.txt";
		private static readonly DateTime WindowStartUtc = new DateTime(2026, 7, 13, 18, 25, 0, DateTimeKind.Utc);
		private static readonly DateTime WindowEndUtc = new DateTime(2026, 7, 13, 18, 45, 0, DateTimeKind.Utc);
		// ==============================================================

		static void Main(string[] args)
		{
			string logFile = $"log_cleanup_duplicateopps_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
			using (Logger log = new Logger(logFile))
			{
				// ── Locate the IDs file ──────────────────────────────────
				string idsPath = ResolveIdsFile(args);
				if (idsPath == null)
				{
					log.Error($"Could not find {IdsFileName}. Place it next to the .exe or pass its path as the first argument.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				List<Guid> ids = LoadIds(idsPath, log);
				if (ids.Count == 0)
				{
					log.Error("No valid GUIDs found in the IDs file. Nothing to do.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				// ── Banner ───────────────────────────────────────────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|      STORM BASKETBALL - DUPLICATE OPPORTUNITY CLEANUP TOOL            |");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine($"|  Environment  : {EnvUrl,-52}|");
				Console.WriteLine($"|  User         : {CrmUsername,-52}|");
				Console.WriteLine($"|  IDs file     : {Path.GetFileName(idsPath),-52}|");
				Console.WriteLine($"|  IDs loaded   : {ids.Count,-52}|");
				Console.WriteLine($"|  Log file     : {logFile,-52}|");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|  WHAT THIS TOOL DOES:                                                |");
				Console.WriteLine("|   1. DRY RUN: retrieve + validate every opportunity in the list.     |");
				Console.WriteLine("|   2. Delete ONLY records created 2026-07-13 in the flow window.      |");
				Console.WriteLine("|   3. HARD DELETE (permanent) after you confirm the dry-run.          |");
				Console.WriteLine("|   Records outside the window / not found are SKIPPED.               |");
				Console.WriteLine("+======================================================================+");
				Console.WriteLine("|  !! HARD DELETE is PERMANENT and MODIFIES LIVE DATA. Verify above.   |");
				Console.WriteLine("+======================================================================+");
				Console.ResetColor();
				Console.WriteLine();

				// ── Connect ──────────────────────────────────────────────
				string connectionString =
					$"AuthType=OAuth;Url={EnvUrl};Username={CrmUsername};Password={CrmPassword};" +
					$"AppId={AppId};RedirectUri={RedirectUri};LoginPrompt=Auto";

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

				// ── PHASE 1: DRY RUN (validate) ──────────────────────────
				log.Info("========== PHASE 1: DRY RUN (no changes) ==========");
				var toDelete = new List<Guid>();
				int notFound = 0, outOfWindow = 0;

				for (int i = 0; i < ids.Count; i++)
				{
					Guid id = ids[i];
					Entity opp = SafeRetrieve(svc, id, log);

					if (opp == null)
					{
						log.Warning($"[{i + 1}/{ids.Count}] {id} -> NOT FOUND (already deleted?). SKIP.");
						notFound++;
						continue;
					}

					string name = opp.Contains("name") ? (string)opp["name"] : "(no name)";
					DateTime? createdOn = opp.Contains("createdon") ? (DateTime?)((DateTime)opp["createdon"]).ToUniversalTime() : null;
					int stateCode = opp.Contains("statecode") ? ((OptionSetValue)opp["statecode"]).Value : -1;
					string oppType = opp.FormattedValues.Contains("new_opportunitytype") ? opp.FormattedValues["new_opportunitytype"] : "?";
					string owner = opp.Contains("ownerid") ? ((EntityReference)opp["ownerid"]).Name : "?";

					bool inWindow = createdOn.HasValue && createdOn.Value >= WindowStartUtc && createdOn.Value <= WindowEndUtc;

					string line = $"[{i + 1}/{ids.Count}] {id} | '{name}' | type={oppType} | owner={owner} | createdon(UTC)={createdOn:yyyy-MM-dd HH:mm:ss} | state={stateCode}";

					if (inWindow)
					{
						log.WriteLine("  WILL DELETE  " + line, ConsoleColor.Green);
						toDelete.Add(id);
					}
					else
					{
						log.Warning("  SKIP (outside 07/13 window) " + line);
						outOfWindow++;
					}
				}

				Console.WriteLine();
				log.Info($"DRY RUN SUMMARY: to delete = {toDelete.Count} | skipped (not found) = {notFound} | skipped (outside window) = {outOfWindow} | total in file = {ids.Count}");
				log.WriteLine(new string('=', 72));

				if (toDelete.Count == 0)
				{
					log.Success("No records qualify for deletion. Exiting without changes.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				// ── Confirmation gate ────────────────────────────────────
				Console.ForegroundColor = ConsoleColor.Red;
				Console.WriteLine();
				Console.WriteLine($"About to PERMANENTLY DELETE {toDelete.Count} opportunities from {EnvUrl}.");
				Console.ResetColor();
				Console.Write("Type DELETE (all caps) and press Enter to proceed, or anything else to cancel: ");

				string confirm = Console.ReadLine() ?? string.Empty;
				if (!confirm.Trim().Equals("DELETE", StringComparison.Ordinal))
				{
					log.Warning("Deletion cancelled by the user. No records were deleted (dry run only).");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}

				// ── PHASE 2: DELETE ──────────────────────────────────────
				Console.WriteLine();
				log.Info("========== PHASE 2: HARD DELETE ==========");
				int deleted = 0, errors = 0;

				for (int i = 0; i < toDelete.Count; i++)
				{
					Guid id = toDelete[i];
					try
					{
						svc.Delete("opportunity", id);
						log.Success($"[{i + 1}/{toDelete.Count}] DELETED {id}");
						deleted++;
					}
					catch (Exception ex)
					{
						log.Error($"[{i + 1}/{toDelete.Count}] FAILED to delete {id}: {ex.Message}");
						if (ex.InnerException != null) log.Error($"  Inner: {ex.InnerException.Message}");
						errors++;
					}
				}

				// ── Summary ──────────────────────────────────────────────
				Console.WriteLine();
				log.WriteLine(new string('=', 72));
				log.Info("EXECUTION COMPLETE");
				log.Success($"  Deleted            : {deleted}");
				if (errors > 0) log.Error($"  Errors             : {errors}");
				else log.Info($"  Errors             : {errors}");
				log.Info($"  Skipped (not found): {notFound}");
				log.Info($"  Skipped (window)   : {outOfWindow}");
				log.Info($"  Log saved to       : {logFile}");
				log.WriteLine(new string('=', 72));

				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
			}
		}

		// ==============================================================
		//  Retrieve one opportunity; return null if it does not exist.
		// ==============================================================
		private static Entity SafeRetrieve(CrmServiceClient svc, Guid id, Logger log)
		{
			try
			{
				return svc.Retrieve("opportunity", id,
					new ColumnSet("name", "createdon", "statecode", "new_opportunitytype", "ownerid"));
			}
			catch (Exception ex)
			{
				// A missing record throws; treat as not found.
				if (ex.Message.IndexOf("Does Not Exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
					ex.Message.IndexOf("was not found", StringComparison.OrdinalIgnoreCase) >= 0)
					return null;

				log.Warning($"  Retrieve error for {id}: {ex.Message}");
				return null;
			}
		}

		// ==============================================================
		//  Find the IDs file: arg[0], exe dir, or walk up a few folders.
		// ==============================================================
		private static string ResolveIdsFile(string[] args)
		{
			if (args != null && args.Length > 0 && File.Exists(args[0])) return args[0];

			string baseDir = AppDomain.CurrentDomain.BaseDirectory;
			string dir = baseDir;
			for (int i = 0; i < 5 && dir != null; i++)
			{
				string candidate = Path.Combine(dir, IdsFileName);
				if (File.Exists(candidate)) return candidate;
				DirectoryInfo parent = Directory.GetParent(dir);
				dir = parent?.FullName;
			}
			return null;
		}

		// ==============================================================
		//  Load GUIDs from the file (one per line, ignore blanks/dupes).
		// ==============================================================
		private static List<Guid> LoadIds(string path, Logger log)
		{
			var set = new HashSet<Guid>();
			var list = new List<Guid>();
			int bad = 0;
			foreach (string raw in File.ReadAllLines(path))
			{
				string s = raw.Trim();
				if (s.Length == 0) continue;
				if (Guid.TryParse(s, out Guid g))
				{
					if (set.Add(g)) list.Add(g);
				}
				else bad++;
			}
			log.Info($"Loaded {list.Count} unique GUID(s) from {Path.GetFileName(path)}" + (bad > 0 ? $" ({bad} unparsable line(s) ignored)." : "."));
			return list;
		}
	}
}
