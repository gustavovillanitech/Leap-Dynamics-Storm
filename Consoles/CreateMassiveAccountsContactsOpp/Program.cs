// ============================================================
//  Storm Into College – Dynamics 365 Bulk Import Tool
//  Target environment : https://stormbasketball.crm.dynamics.com/
//  SDK                : Microsoft.PowerPlatform.Dataverse.Client
//  .NET version       : 6 or higher
// ============================================================

using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Rest;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace StormIntoCollegeImport
{
	// ----------------------------------------------------------------
	// Simple DTO to hold one parsed CSV row
	// ----------------------------------------------------------------
	internal sealed class CsvRow
	{
		public string FirstName { get; set; } = string.Empty;
		public string LastName { get; set; } = string.Empty;
		public string JobTitle { get; set; } = string.Empty;
		public string Email { get; set; } = string.Empty;
		public string AccountName { get; set; } = string.Empty;
		public string City { get; set; } = string.Empty;
		public string State { get; set; } = string.Empty;
		public string Rep { get; set; } = string.Empty;
	}

	// ----------------------------------------------------------------
	// Logger: writes to console AND to a .txt file simultaneously
	// ----------------------------------------------------------------
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
			WriteLine(new string('=', 70));
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
		public void Updated(string msg) => WriteLine($"[UPDATED] {msg}", ConsoleColor.Yellow);
		public void Created(string msg) => WriteLine($"[CREATED] {msg}", ConsoleColor.Blue);
		public void Skipped(string msg) => WriteLine($"[SKIPPED] {msg}", ConsoleColor.DarkYellow);
		public void Error(string msg) => WriteLine($"[ERROR]   {msg}", ConsoleColor.Red);

		public void Dispose()
		{
			WriteLine(new string('=', 70));
			WriteLine($"[LOG END] {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			_writer.Dispose();
		}
	}

	// ----------------------------------------------------------------
	// Main program
	// ----------------------------------------------------------------
	internal class Program
	{
		// ==============================================================
		//  CONFIGURATION – edit these values before running
		// ==============================================================
		private const string EnvUrl = "https://stormbasketball.crm.dynamics.com/";
		private const string CrmUsername = "FanInteractive@stormbasketball.com";
		private const string CrmPassword = "CsCXbm2E-WtQ3c4DCy2!";
		private const string AppId = "51f81489-12ee-4a9e-aaae-a2591f45987d";
		private const string RedirectUri = "app://58145B91-0C36-4500-8554-080854F2AC97";

		// Path to the CSV file – adjust if needed
		private const string CsvFilePath =
			@"C:\Users\Angela\Downloads\2026 Storm into College Lead list (AK).csv";

		// Existing Campaign to which every processed contact will be added
		private static readonly Guid CampaignId = new Guid("be95cf0b-a654-f111-bec7-000d3a3ac02f");

		// Opportunity type choice value: 100000001 = "Ticketing - Groups"
		private const int OppTypeTicketingGroups = 100000001;
		// ==============================================================

		static void Main(string[] args)
		{
			// ── Log file name includes timestamp to avoid overwriting ──
			string logFile = $"log_import_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
			Logger log = new Logger(logFile);

			// ── Warning / confirmation banner ─────────────────────────
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine();
			Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
			Console.WriteLine("║        STORM INTO COLLEGE – DYNAMICS 365 IMPORTER       ║");
			Console.WriteLine("╠══════════════════════════════════════════════════════════╣");
			Console.WriteLine($"║  Environment : {EnvUrl,-45}║");
			Console.WriteLine($"║  CSV File    : {Path.GetFileName(CsvFilePath),-45}║");
			Console.WriteLine($"║  Campaign ID : {CampaignId,-45}║");
			Console.WriteLine("╠══════════════════════════════════════════════════════════════════════════════════╣");
			Console.WriteLine("║  This tool will, FOR EACH CSV ROW:                                               ║");
			Console.WriteLine("║   1. Resolve the Rep → System User GUID                                          ║");
			Console.WriteLine("║   2. Find or Create the Account (school)										  ║");
			Console.WriteLine("║   3. Find, Update or Create the Contact                                          ║");
			Console.WriteLine("║   4. Create a new Opportunity linked to Contact+Account+Campaign=CMP-01185-D9Q3V ║");
			Console.WriteLine("╚══════════════════════════════════════════════════════════════════════════════════╝");
			Console.ResetColor();
			Console.WriteLine();
			Console.WriteLine("DO YOU WANT TO PROCEED? Press Y to confirm or any other key to cancel:");

			string input = Console.ReadLine() ?? string.Empty;
			if (input.Trim().ToUpper() != "Y")
			{
				log.Info("Operation cancelled by the user. No changes were made.");
				Console.WriteLine("\nPress Enter to exit...");
				Console.ReadLine();
				return;
			}

			// ── Read and validate CSV ─────────────────────────────────
			if (!File.Exists(CsvFilePath))
			{
				log.Error($"CSV file not found: {CsvFilePath}");
				Console.ReadLine();
				return;
			}

			List<CsvRow> rows = ParseCsv(CsvFilePath, log);
			if (rows.Count == 0)
			{
				log.Error("CSV is empty or could not be parsed. Aborting.");
				Console.ReadLine();
				return;
			}
			log.Info($"CSV loaded successfully. Total rows to process: {rows.Count}");

			// ── Connect to Dataverse ──────────────────────────────────
			string connectionString =
				$"AuthType=OAuth;" +
				$"Url={EnvUrl};" +
				$"Username={CrmUsername};" +
				$"Password={CrmPassword};" +
				$"AppId={AppId};" +
				$"RedirectUri={RedirectUri};" +
				$"LoginPrompt=Auto";

			log.Info("Connecting to Dynamics 365...");
			ServiceClient svc = new ServiceClient(connectionString);

			if (!svc.IsReady)
			{
				log.Error($"Connection failed: {svc.LastError}");
				Console.ReadLine();
				return;
			}
			log.Success("Connection established successfully.");

			// ── In-memory caches to avoid redundant queries ───────────
			Dictionary<string, Guid> userCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
			Dictionary<string, Guid> accountCache = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

			// ── Processing counters ───────────────────────────────────
			int totalRows = rows.Count;
			int successCount = 0;
			int errorCount = 0;
			int rowIndex = 0;

			foreach (CsvRow row in rows)
			{
				rowIndex++;
				string rowLabel = $"Row {rowIndex}/{totalRows} | {row.FirstName} {row.LastName} <{row.Email}>";

				log.WriteLine(new string('-', 70));
				log.Info($"Processing {rowLabel}");

				try
				{
					// =================================================
					// STEP 1 – Resolve the Rep (System User)
					// =================================================
					Guid ownerId = ResolveOwner(svc, row.Rep, userCache, log);
					if (ownerId == Guid.Empty)
					{
						log.Skipped($"{rowLabel} – Rep '{row.Rep}' not found. Row skipped.");
						errorCount++;
						continue;
					}

					// =================================================
					// STEP 2 – Find or Create Account
					// =================================================
					Guid accountId = FindOrCreateAccount(svc, row, accountCache, log);

					// =================================================
					// STEP 3 – Find, Update or Create Contact
					// =================================================
					Guid contactId = FindUpdateOrCreateContact(svc, row, accountId, ownerId, log);

					// =================================================
					// STEP 4 – Create Opportunity
					// =================================================
					Guid opportunityId = CreateOpportunity(svc, row, contactId, accountId, ownerId, log);

					// =================================================
					// STEP 5 – Add Contact to Campaign
					// =================================================
					//AddContactToCampaign(svc, contactId, log);

					log.Success($"{rowLabel} – Completed. OppId={opportunityId}");
					successCount++;
				}
				catch (Exception ex)
				{
					// Per-row catch: one failure does NOT stop the loop
					log.Error($"{rowLabel} – Unhandled exception: {ex.Message}");
					if (ex.InnerException != null)
						log.Error($"  Inner: {ex.InnerException.Message}");
					errorCount++;
				}
			}

			// ── Final summary ─────────────────────────────────────────
			log.WriteLine(new string('=', 70));
			log.Info($"PROCESS COMPLETE  |  Total: {totalRows}  |  Success: {successCount}  |  Errors: {errorCount}");
			log.Info($"Log file saved to: {Path.GetFullPath(logFile)}");

			Console.WriteLine("\nPress Enter to exit...");
			Console.ReadLine();
		}

		// ==============================================================
		//  CSV PARSER
		//  Uses a simple manual parser compatible with RFC-4180 quoting.
		//  No external NuGet dependency required.
		// ==============================================================
		private static List<CsvRow> ParseCsv(string filePath, Logger log)
		{
			var result = new List<CsvRow>();

			try
			{
				string[] lines = File.ReadAllLines(filePath, Encoding.UTF8);
				if (lines.Length < 2)
				{
					log.Error("CSV has fewer than 2 lines (header + data). Nothing to process.");
					return result;
				}

				// Map column names to indices using the header row
				string[] headers = SplitCsvLine(lines[0]);
				Dictionary<string, int> idx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < headers.Length; i++)
					idx[headers[i].Trim()] = i;

				// Helper to safely read a column value
				string Get(string[] cols, string name)
				{
					// The Email column has a trailing space in the original file
					if (idx.TryGetValue(name, out int i) && i < cols.Length)
						return cols[i].Trim();
					// Fallback: try with trailing space (e.g. "Email ")
					if (idx.TryGetValue(name + " ", out i) && i < cols.Length)
						return cols[i].Trim();
					return string.Empty;
				}

				for (int lineNo = 1; lineNo < lines.Length; lineNo++)
				{
					string line = lines[lineNo];
					if (string.IsNullOrWhiteSpace(line)) continue;

					string[] cols = SplitCsvLine(line);

					var row = new CsvRow
					{
						FirstName = Get(cols, "First Name"),
						LastName = Get(cols, "Last Name"),
						JobTitle = Get(cols, "Job Title"),
						Email = Get(cols, "Email"),          // also tries "Email "
						AccountName = Get(cols, "Account Name"),
						City = Get(cols, "Address 1: City"),
						State = Get(cols, "Address 1: State/Province"),
						Rep = Get(cols, "Rep"),
					};

					// Skip rows that are entirely empty
					if (string.IsNullOrWhiteSpace(row.FirstName) &&
						string.IsNullOrWhiteSpace(row.Email))
						continue;

					result.Add(row);
				}
			}
			catch (Exception ex)
			{
				log.Error($"CSV parse error: {ex.Message}");
			}

			return result;
		}

		/// <summary>
		/// Splits a single CSV line respecting RFC-4180 double-quoted fields.
		/// </summary>
		private static string[] SplitCsvLine(string line)
		{
			var fields = new List<string>();
			bool inQuotes = false;
			var current = new StringBuilder();

			for (int i = 0; i < line.Length; i++)
			{
				char c = line[i];
				if (inQuotes)
				{
					if (c == '"')
					{
						// escaped quote ""
						if (i + 1 < line.Length && line[i + 1] == '"')
						{
							current.Append('"');
							i++;
						}
						else
						{
							inQuotes = false;
						}
					}
					else
					{
						current.Append(c);
					}
				}
				else
				{
					if (c == '"')
					{
						inQuotes = true;
					}
					else if (c == ',')
					{
						fields.Add(current.ToString());
						current.Clear();
					}
					else
					{
						current.Append(c);
					}
				}
			}
			fields.Add(current.ToString());
			return fields.ToArray();
		}

		// ==============================================================
		//  STEP 1 – RESOLVE OWNER (System User)
		// ==============================================================
		private static Guid ResolveOwner(
			ServiceClient svc,
			string repName,
			Dictionary<string, Guid> cache,
			Logger log)
		{
			if (string.IsNullOrWhiteSpace(repName)) return Guid.Empty;

			if (cache.TryGetValue(repName, out Guid cached))
				return cached;

			// Split "First Last" into parts for a flexible match
			string[] parts = repName.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
			var qe = new QueryExpression("systemuser")
			{
				ColumnSet = new ColumnSet("systemuserid", "fullname"),
				TopCount = 1,
				NoLock = true
			};
			qe.Criteria.AddCondition("fullname", ConditionOperator.Equal, repName);
			qe.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);

			EntityCollection results = svc.RetrieveMultiple(qe);

			// If exact full-name match fails, try first+last name fields
			if (results.Entities.Count == 0 && parts.Length >= 2)
			{
				var qe2 = new QueryExpression("systemuser")
				{
					ColumnSet = new ColumnSet("systemuserid", "fullname"),
					TopCount = 1,
					NoLock = true
				};
				qe2.Criteria.AddCondition("firstname", ConditionOperator.Equal, parts[parts.Length]);
				qe2.Criteria.AddCondition("lastname", ConditionOperator.Equal, parts[parts.Length - 1]);
				qe2.Criteria.AddCondition("isdisabled", ConditionOperator.Equal, false);
				results = svc.RetrieveMultiple(qe2);
			}

			if (results.Entities.Count == 0)
			{
				log.Error($"System user not found for Rep='{repName}'");
				return Guid.Empty;
			}

			Guid userId = results.Entities[0].Id;
			cache[repName] = userId;
			log.Info($"  Rep resolved: '{repName}' → {userId}");
			return userId;
		}

		// ==============================================================
		//  STEP 2 – FIND OR CREATE ACCOUNT
		// ==============================================================
		private static Guid FindOrCreateAccount(
			ServiceClient svc,
			CsvRow row,
			Dictionary<string, Guid> cache,
			Logger log)
		{
			string accountName = row.AccountName;

			if (cache.TryGetValue(accountName, out Guid cached))
			{
				log.Info($"  Account (cached): '{accountName}' → {cached}");
				return cached;
			}

			// Search by exact name
			var qe = new QueryExpression("account")
			{
				ColumnSet = new ColumnSet("accountid", "name"),
				TopCount = 1,
				NoLock = true
			};
			qe.Criteria.AddCondition("name", ConditionOperator.Equal, accountName);
			qe.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // active

			EntityCollection results = svc.RetrieveMultiple(qe);

			if (results.Entities.Count > 0)
			{
				Guid existingId = results.Entities[0].Id;
				cache[accountName] = existingId;
				log.Info($"  Account found   : '{accountName}' → {existingId}");
				return existingId;
			}

			// Create new Account
			var account = new Entity("account");
			account["name"] = accountName;
			account["address1_city"] = row.City;
			account["address1_stateorprovince"] = row.State;

			Guid newId = svc.Create(account);
			cache[accountName] = newId;
			log.Created($"  Account created : '{accountName}' → {newId}");
			return newId;
		}

		// ==============================================================
		//  STEP 3 – FIND, UPDATE OR CREATE CONTACT
		// ==============================================================
		private static Guid FindUpdateOrCreateContact(
			ServiceClient svc,
			CsvRow row,
			Guid accountId,
			Guid ownerId,
			Logger log)
		{
			// Primary dedup key: email address
			Entity existing = null;

			if (!string.IsNullOrWhiteSpace(row.Email))
			{
				var qe = new QueryExpression("contact")
				{
					ColumnSet = new ColumnSet("contactid", "emailaddress1", "firstname", "lastname"),
					TopCount = 1,
					NoLock = true
				};
				qe.Criteria.AddCondition("emailaddress1", ConditionOperator.Equal, row.Email);
				qe.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

				EntityCollection res = svc.RetrieveMultiple(qe);
				if (res.Entities.Count > 0) existing = res.Entities[0];
			}

			// Secondary dedup: first + last name if no email match
			if (existing == null &&
				!string.IsNullOrWhiteSpace(row.FirstName) &&
				!string.IsNullOrWhiteSpace(row.LastName))
			{
				var qe2 = new QueryExpression("contact")
				{
					ColumnSet = new ColumnSet("contactid", "emailaddress1", "firstname", "lastname"),
					TopCount = 1,
					NoLock = true
				};
				qe2.Criteria.AddCondition("firstname", ConditionOperator.Equal, row.FirstName);
				qe2.Criteria.AddCondition("lastname", ConditionOperator.Equal, row.LastName);
				qe2.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

				EntityCollection res2 = svc.RetrieveMultiple(qe2);
				if (res2.Entities.Count > 0) existing = res2.Entities[0];
			}

			if (existing != null)
			{
				// ── UPDATE existing contact ───────────────────────────
				var update = new Entity("contact", existing.Id);
				update["jobtitle"] = row.JobTitle;
				update["address1_city"] = row.City;
				update["address1_stateorprovince"] = row.State;
				update["parentcustomerid"] = new EntityReference("account", accountId);
				update["ownerid"] = new EntityReference("systemuser", ownerId);

				if (!string.IsNullOrWhiteSpace(row.Email))
					update["emailaddress1"] = row.Email;

				svc.Update(update);
				log.Updated($"  Contact updated : {existing.Id} ({row.FirstName} {row.LastName})");
				return existing.Id;
			}

			// ── CREATE new contact ────────────────────────────────────
			var contact = new Entity("contact");
			contact["firstname"] = row.FirstName;
			contact["lastname"] = row.LastName;
			contact["jobtitle"] = row.JobTitle;
			contact["emailaddress1"] = row.Email;
			contact["address1_city"] = row.City;
			contact["address1_stateorprovince"] = row.State;
			contact["parentcustomerid"] = new EntityReference("account", accountId);
			contact["ownerid"] = new EntityReference("systemuser", ownerId);

			Guid newContactId = svc.Create(contact);
			log.Created($"  Contact created : {newContactId} ({row.FirstName} {row.LastName})");
			return newContactId;
		}

		// ==============================================================
		//  STEP 4 – CREATE OPPORTUNITY
		// ==============================================================
		private static Guid CreateOpportunity(
			ServiceClient svc,
			CsvRow row,
			Guid contactId,
			Guid accountId,
			Guid ownerId,
			Logger log)
		{
			var opp = new Entity("opportunity");
			opp["name"] = $"Group Opp - {row.FirstName} {row.LastName}";
			opp["parentcontactid"] = new EntityReference("contact", contactId);
			opp["parentaccountid"] = new EntityReference("account", accountId);
			opp["ownerid"] = new EntityReference("systemuser", ownerId);
			// Associate Opportunity to Campaign
			opp["campaignid"] = new EntityReference("campaign", CampaignId);
			// Custom choice field: Opportunity Type = Ticketing - Groups (100000001)
			opp["new_opportunitytype"] = new OptionSetValue(OppTypeTicketingGroups);

			Guid oppId = svc.Create(opp);
			log.Created($"  Opportunity created: {oppId} | '{opp["name"]}'");
			return oppId;
		}

		// ==============================================================
		//  STEP 5 – ADD CONTACT TO CAMPAIGN
		// ==============================================================
		private static void AddContactToCampaign(
			ServiceClient svc,
			Guid contactId,
			Logger log)
		{
			try
			{
				var request = new AddItemCampaignRequest
				{
					CampaignId = CampaignId,
					EntityName = "contact",
					EntityId = contactId
				};

				svc.Execute(request);
				log.Info($"  Contact {contactId} added to Campaign {CampaignId}");
			}
			catch (Exception ex)
			{
				log.Error($"Failed to add contact {contactId} to campaign:");
				log.Error("Message: " + ex.Message);

				if (ex.InnerException != null)
				{
					log.Error("Inner: " + ex.InnerException.Message);
				}

				log.Error("StackTrace: " + ex.StackTrace);
			}
		}
	}
}
