using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Tooling.Connector;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CreateMassiveOppFromLeads
{
	internal class Program
	{
		static void Main(string[] args)
		{
			// 1. DATAVERSE CONNECTION
			string envUrl = "https://stormbasketball.crm.dynamics.com/";

			// WARNING PROMPT
			Console.ForegroundColor = ConsoleColor.Yellow;
			Console.WriteLine("=========================================================");
			Console.WriteLine($"WARNING: YOU ARE ABOUT TO CONNECT TO:");
			Console.WriteLine(envUrl);
			Console.WriteLine("=========================================================");
			Console.ResetColor();

			Console.WriteLine("\nDO YOU WANT TO PERFORM THE MASSIVE QUALIFICATION OF LEADS, CREATING AN CONTACT AND OPPORTUNITY?");
			Console.WriteLine("PRESS 'Y' FOR YES OR 'N' FOR NO:");

			string userInput = Console.ReadLine();
			if (userInput?.Trim().ToUpper() != "Y")
			{
				Console.WriteLine("\nOperation cancelled by the user. Press Enter to exit...");
				Console.ReadLine();
				return;
			}

			// 1. DATAVERSE CONNECTION STRING
			string connectionString = $@"AuthType=OAuth;Url={envUrl};Username=FanInteractive@stormbasketball.com;Password=CsCXbm2E-WtQ3c4DCy2!;AppId=51f81489-12ee-4a9e-aaae-a2591f45987d;RedirectUri=app://58145B91-0C36-4500-8554-080854F2AC97;LoginPrompt=Auto";

			Console.WriteLine("\nConnecting to Dynamics 365...");
			CrmServiceClient service = new CrmServiceClient(connectionString);

			if (!service.IsReady)
			{
				Console.WriteLine("Connection error: " + service.LastCrmError);
				Console.ReadLine();
				return;
			}
			Console.WriteLine("Connection successful!\n");

			// Path to your CSV file containing the GUIDs
			string csvFilePath = @"C:\Users\Angela\Downloads\LeadsToConvert-Storm-HayleyBrown.csv";

			Console.WriteLine("Connecting to Dynamics 365 Client...");

			using (ServiceClient serviceClient = new ServiceClient(connectionString))
			{
				if (!serviceClient.IsReady)
				{
					Console.WriteLine($"Connection error: {serviceClient.LastError}");
					return;
				}

				Console.WriteLine("Connection successful!");

				// 2. Read GUIDs from the file
				string[] leadIds = File.ReadAllLines(csvFilePath);
				int successCount = 0;
				int errorCount = 0;

				Console.WriteLine($"Found {leadIds.Length} records to process.");

				// 3. Process each Lead
				foreach (string idString in leadIds)
				{
					// Clean the string in case the CSV includes quotation marks or spaces
					string cleanId = idString.Replace("\"", "").Trim();

					if (Guid.TryParse(cleanId, out Guid leadId))
					{
						try
						{
							// Configure the qualification request
							var qualifyRequest = new QualifyLeadRequest
							{
								CreateAccount = false,      // As agreed, do not create an account
								CreateContact = true,       // Create contact
								CreateOpportunity = true,   // Create opportunity
								LeadId = new EntityReference("lead", leadId),
								Status = new OptionSetValue(3) // 3 = Status Reason for "Qualified"
							};

							// Execute the request
							var qualifyResponse = (QualifyLeadResponse)serviceClient.Execute(qualifyRequest);

							Console.WriteLine($"[SUCCESS] Lead {leadId} qualified. Opportunity created.");
							successCount++;
						}
						catch (Exception ex)
						{
							Console.WriteLine($"[ERROR] Lead {leadId} failed: {ex.Message}");
							errorCount++;
						}
					}
					else
					{
						Console.WriteLine($"[SKIPPED] '{cleanId}' is not a valid GUID.");
					}
				}

				Console.WriteLine("-----------------------------------");
				Console.WriteLine($"Process completed. Successful: {successCount} | Errors: {errorCount}");
				Console.ReadLine();
			}
		}
	}
}