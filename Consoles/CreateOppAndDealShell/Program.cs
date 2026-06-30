// ============================================================
//  Storm Basketball – Dynamics 365 Shell Creation Tool
//  Purpose     : Create Opportunity + Deal shells (CP-Prospect,
//                Closed Won, 1-year contract) for Zack to fill in
//                Deal Lines manually.
//  Environment : https://stormbasketball.crm.dynamics.com/
//  SDK         : Microsoft.Xrm.Tooling.Connector (CrmServiceClient)
//  .NET        : Framework 4.6.2
// ============================================================

using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CreateOppAndDealShell
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
	//  Data model for each shell to be created
	// ================================================================
	internal class ShellSpec
	{
		public string CompanyLabel { get; set; }   // Label from Ray's list (e.g. "Amazon - Community")
		public Guid AccountId { get; set; }        // Hardcoded GUID from CRM
		public string AccountName { get; set; }    // For logging only
		public decimal EstimatedValue { get; set; } // Total from Ray's screenshot

		public ShellSpec(string label, string accountName, Guid accountId, decimal value)
		{
			CompanyLabel = label;
			AccountId = accountId;
			AccountName = accountName;
			EstimatedValue = value;
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
		//private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/"; // <-- sandbox URL first!
		private const string EnvUrl = "https://org00bff505.crm.dynamics.com/"; // <-- sandbox URL first!
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// Season name to look up at runtime
		private const string SeasonName = "2026 - Storm";

		// ==============================================================
		//  OptionSet values
		// ==============================================================
		// Opportunity
		private const int OPPTYPE_CP_PROSPECT = 100000003;
		private const int LEADSOURCE_INBOUND = 100000002;
		private const int CONTRACTLENGTH_1YR = 100000000;
		private const int CONFIDENCE_100_CLOSED_WON = 100000005;
		private const int SALESSTAGE_INITIAL_PROSPECT = 100000000;   // 01 - Prospect (easiest stage to start)
		private const int SALESSTAGE_CLOSED_WON = 100000003;         // 10 - Closed (Prospect Won)
		private const int PITCHTYPE_NEW = 100000000;                 // "New" pitch type
		private const int BUDGETSTATUS_CONFIRMED = 0;                // Standard CRM value for "Can Buy"

		// Deal
		private const int OPTOUTTYPE_NO_OPTION = 100000000;
		private const int PLAYOFFOPTIONSTATUS_OUT = 100000003;
		// ==============================================================

		// ==============================================================
		//  SHELL DEFINITIONS – Account GUIDs resolved from CRM via SQL query
		//
		//  NOTE: 2 entries from Ray's original list of 13 are NOT included:
		//    - "Climate Pledge Arena/ OVG" ($456,666): no matching Account.
		//    - "One Work Place - Affiliate" ($25,000): no matching Account.
		//  These need to be created/clarified by Ray before we can shell them.
		// ==============================================================
		private static readonly List<ShellSpec> Shells = new List<ShellSpec>
		{
            // Label                          AccountName (CRM)              AccountId GUID                                                  Value
            new ShellSpec("Amazon - Community",        "Amazon Community",   new Guid("f6e512c6-58f9-f011-8406-000d3a36642b"),               131250m),
			new ShellSpec("BDO - Affiliate",           "BDO",                new Guid("5e21e5c2-d0fb-f011-8406-6045bd0066ee"),               10000m),
			new ShellSpec("Carter",                    "Carter Subaru",      new Guid("88caef6f-4703-e611-80e4-f0921c194348"),               65000m),
			new ShellSpec("Prime Electric - Affiliate","Prime Electric",     new Guid("977c1ac0-58f9-f011-8406-000d3a36642b"),               22898m),
			new ShellSpec("Seattle Children's",        "Seattle Children's", new Guid("feccef6f-4703-e611-80e4-f0921c194348"),               76700m),
			new ShellSpec("Supergraphics - Affiliate", "Storm Sponsor / Supergraphics", new Guid("ac4c8c6d-ce18-f111-8341-000d3a3ac02f"),       11449m),
			new ShellSpec("ZenTech - Affiliate",       "ZenTechWorks",       new Guid("6221e5c2-d0fb-f011-8406-6045bd0066ee"),               10000m),
			new ShellSpec("Progressive",               "Progressive",        new Guid("e4c8ef6f-4703-e611-80e4-f0921c194348"),               50000m),
			new ShellSpec("PEMCO",                     "Pemco",              new Guid("9cccef6f-4703-e611-80e4-f0921c194348"),               250000m),
			new ShellSpec("The Advocates",             "The Advocates",      new Guid("707c84a5-a728-f111-8341-6045bd081d2b"),               210000m),
			new ShellSpec("Amazon Alexa",              "Alexa",              new Guid("fa6f7259-490c-f111-8406-6045bd081d2b"),               300000m),
		};

		static void Main(string[] args)
		{
			string logFile = $"log_create_shells_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
			using (Logger log = new Logger(logFile))
			{
				// ── Pre-execution banner ─────────────────────────────────
				Console.ForegroundColor = ConsoleColor.Yellow;
				Console.WriteLine();
				Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
				Console.WriteLine("║       STORM BASKETBALL – DYNAMICS 365  OPP+DEAL SHELL CREATOR        ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine($"║  Environment  : {EnvUrl,-53}║");
				Console.WriteLine($"║  User         : {CrmUsername,-53}║");
				Console.WriteLine($"║  Season       : {SeasonName,-53}║");
				Console.WriteLine($"║  Shells to create : {Shells.Count,-49}║");
				Console.WriteLine($"║  Log file     : {logFile,-53}║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  WHAT THIS TOOL DOES (per shell):                                    ║");
				Console.WriteLine("║   1. Create Opportunity (CP-Prospect, Stage=01 Prospect, 1yr).       ║");
				Console.WriteLine("║   2. Create associated Deal (Option Status=No Option, Playoff=Out). ║");
				Console.WriteLine("║   3. Flip Opp Sales Stage to '10 - Closed' (triggers Won cascade).   ║");
				Console.WriteLine("║      → CloseCorpPartnership closes Opp Won + Deal cascaded to Won.   ║");
				Console.WriteLine("║      → DealOptionAutomation freezes Max Activation Spend %.          ║");
				Console.WriteLine("║      → CloneMultiYearDeals does NOT fire (contract length = 1).      ║");
				Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
				Console.WriteLine("║  ⚠  This operation MODIFIES LIVE DATA in PRODUCTION.                 ║");
				Console.WriteLine("║  ⚠  Verify the environment + shell list above before continuing.     ║");
				Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
				Console.ResetColor();
				Console.WriteLine();

				// Print the list of shells for visual verification
				log.Info("Shells to be created:");
				foreach (ShellSpec s in Shells)
				{
					log.Step($"  • {s.CompanyLabel,-30} → Account: {s.AccountName,-22} | Value: {s.EstimatedValue,12:C}");
				}
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

				// ── Resolve Season once ──────────────────────────────────
				log.Info($"Resolving Season '{SeasonName}'...");
				EntityReference seasonRef = ResolveSeason(svc, SeasonName, log);
				if (seasonRef == null)
				{
					log.Error($"Season '{SeasonName}' not found. Aborting.");
					Console.WriteLine("\nPress Enter to exit...");
					Console.ReadLine();
					return;
				}
				log.Success($"Season resolved: {seasonRef.Id}");
				Console.WriteLine();

				// ── Process each shell ───────────────────────────────────
				int total = Shells.Count;
				int success = 0;
				int errors = 0;

				for (int i = 0; i < total; i++)
				{
					ShellSpec spec = Shells[i];
					log.WriteLine(new string('-', 72));
					log.Info($"[{i + 1}/{total}] Processing: '{spec.CompanyLabel}' → Account '{spec.AccountName}' ({spec.AccountId}) | Value: {spec.EstimatedValue:C}");

					Guid? createdOppId = null;
					Guid? createdDealId = null;

					try
					{
						EntityReference accountRef = new EntityReference("account", spec.AccountId)
						{
							Name = spec.AccountName
						};

						// STEP A: Create Opportunity (Open, Stage=01 Prospect)
						log.Step("Step A: Creating Opportunity (Open, Stage=01 Prospect)...");
						createdOppId = CreateOpportunity(svc, spec, accountRef, seasonRef);
						log.Step($"  Opportunity created: {createdOppId}");

						// STEP B: Create Deal (Open, Option Status=No Option)
						log.Step("Step B: Creating Deal (Option Status=No Option, Playoff=Out)...");
						createdDealId = CreateDeal(svc, createdOppId.Value, accountRef, seasonRef);
						log.Step($"  Deal created: {createdDealId}");

						// STEP C: Flip Opp Sales Stage to Closed → triggers Won cascade
						log.Step("Step C: Flipping Opp Sales Stage to '10 - Closed' (Won)...");
						Entity oppUpdate = new Entity("opportunity", createdOppId.Value);
						oppUpdate["new_salesstage"] = new OptionSetValue(SALESSTAGE_CLOSED_WON);
						svc.Update(oppUpdate);
						log.Step("  Sales Stage flipped. Cascade (CloseCorpPartnership + Fix 2) should have fired.");

						log.Success($"[{i + 1}/{total}] DONE → Opp: {createdOppId} | Deal: {createdDealId} | Account: '{spec.AccountName}'");
						success++;
					}
					catch (Exception ex)
					{
						log.Error($"[{i + 1}/{total}] FAILED → '{spec.CompanyLabel}'");
						log.Error($"  Message    : {ex.Message}");
						if (ex.InnerException != null)
							log.Error($"  Inner      : {ex.InnerException.Message}");
						log.Error($"  StackTrace : {ex.StackTrace}");

						// Log what was created so far (for manual cleanup if needed)
						if (createdOppId.HasValue)
							log.Warning($"  → Partial state: Opp {createdOppId} was created but may need manual cleanup.");
						if (createdDealId.HasValue)
							log.Warning($"  → Partial state: Deal {createdDealId} was created but may need manual cleanup.");

						errors++;
					}
				}

				// ── Summary ──────────────────────────────────────────────
				Console.WriteLine();
				log.WriteLine(new string('=', 72));
				log.Info($"EXECUTION COMPLETE");
				log.Info($"  Total shells in batch    : {total}");
				log.Success($"  Successfully created     : {success}");
				if (errors > 0)
					log.Error($"  Errors                   : {errors}");
				else
					log.Info($"  Errors                   : {errors}");
				log.Info($"  Log saved to             : {logFile}");
				log.WriteLine(new string('=', 72));

				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
			}
		}

		// ==============================================================
		//  Resolve Season by name (returns EntityReference or null)
		// ==============================================================
		private static EntityReference ResolveSeason(CrmServiceClient svc, string seasonName, Logger log)
		{
			QueryExpression q = new QueryExpression("new_season")
			{
				ColumnSet = new ColumnSet("new_seasonid", "new_name"),
				TopCount = 1,
				NoLock = true
			};
			q.Criteria.AddCondition("new_name", ConditionOperator.Equal, seasonName);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

			EntityCollection results = svc.RetrieveMultiple(q);
			if (results.Entities.Count == 0) return null;

			Entity e = results.Entities[0];
			return new EntityReference("new_season", e.Id) { Name = (string)e["new_name"] };
		}

		// ==============================================================
		//  Create Opportunity (Open, Stage=01 Prospect)
		//  All required fields for stage 10 are pre-filled now so the
		//  Won transition (step C) does not trip on any required check.
		// ==============================================================
		private static Guid CreateOpportunity(
			CrmServiceClient svc,
			ShellSpec spec,
			EntityReference accountRef,
			EntityReference seasonRef)
		{
			Entity opp = new Entity("opportunity");
			opp["name"] = $"{accountRef.Name} - 2026 - Storm";
			opp["parentaccountid"] = accountRef;
			opp["new_opportunitytype"] = new OptionSetValue(OPPTYPE_CP_PROSPECT);
			opp["new_leadsource"] = new OptionSetValue(LEADSOURCE_INBOUND);
			opp["new_pitchdate"] = DateTime.Today;
			opp["new_pitchedcontractlength"] = new OptionSetValue(CONTRACTLENGTH_1YR);
			opp["new_confidencelevel"] = new OptionSetValue(CONFIDENCE_100_CLOSED_WON);
			opp["new_pitchtype"] = new OptionSetValue(PITCHTYPE_NEW);
			//opp["budgetstatus"] = new OptionSetValue(BUDGETSTATUS_CONFIRMED);
			opp["estimatedclosedate"] = DateTime.Today;
			opp["estimatedvalue"] = new Money(spec.EstimatedValue);
			opp["new_salesstage"] = new OptionSetValue(SALESSTAGE_INITIAL_PROSPECT);
			opp["new_basketballseason"] = seasonRef;

			return svc.Create(opp);
		}

		// ==============================================================
		//  Create Deal (Open, Option Status=No Option, Playoff=Out)
		// ==============================================================
		private static Guid CreateDeal(
			CrmServiceClient svc,
			Guid oppId,
			EntityReference accountRef,
			EntityReference seasonRef)
		{
			Entity deal = new Entity("new_deals");
			// new_name will be auto-set by SetName plugin (Pre-Op Create) if Fix 3 is deployed
			// Setting a fallback here as defense in depth in case the plugin doesn't run
			deal["new_name"] = $"{accountRef.Name} - 2026 - Storm";
			deal["new_opportunity"] = new EntityReference("opportunity", oppId);
			deal["new_accountid"] = accountRef;
			deal["new_season"] = seasonRef;
			// Pre-fill Option Status fields so Fix 4 validator doesn't block Won cascade closure
			deal["new_optouttype"] = new OptionSetValue(OPTOUTTYPE_NO_OPTION);
			deal["new_playoffoptionstatus"] = new OptionSetValue(PLAYOFFOPTIONSTATUS_OUT);

			return svc.Create(deal);
		}
	}
}
