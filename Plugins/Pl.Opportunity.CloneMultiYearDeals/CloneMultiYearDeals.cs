using System;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Opportunity.CloneMultiYearDeals
{
	public class CloneMultiYearDeals : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = factory.CreateOrganizationService(context.UserId);
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			tracingService.Trace($"--- PLUGIN STARTED: CloneMultiYearDeals (Full Architecture) ---");
			tracingService.Trace($"Current Depth: {context.Depth}, Message: {context.MessageName}");

			try
			{
				// Increased depth tolerance to 2 to allow synchronous workflows/Flows to trigger the plugin
				if (context.Depth > 2)
				{
					tracingService.Trace("ABORT: Context Depth is greater than 2. Exiting to prevent infinite loops.");
					return;
				}

				Guid oppId = Guid.Empty;

				if (context.MessageName.ToLower() == "win" && context.InputParameters.Contains("OpportunityClose") && context.InputParameters["OpportunityClose"] is Entity)
				{
					oppId = ((Entity)context.InputParameters["OpportunityClose"]).GetAttributeValue<EntityReference>("opportunityid").Id;
				}
				else if (context.MessageName.ToLower() == "update" && context.PrimaryEntityName == "opportunity")
				{
					Entity target = (Entity)context.InputParameters["Target"];
					if (!target.Contains("statecode") || target.GetAttributeValue<OptionSetValue>("statecode").Value != 1)
					{
						tracingService.Trace("ABORT: Update message but statecode is not Won (1).");
						return;
					}
					oppId = target.Id;
				}
				else
				{
					tracingService.Trace("ABORT: Message is neither Win nor a Won Update.");
					return;
				}

				if (oppId == Guid.Empty) return;

				// 1. Retrieve Master Opportunity 
				Entity opp = service.Retrieve("opportunity", oppId, new ColumnSet(
					"name", "parentaccountid", "parentcontactid", "new_pitchdate", "estimatedclosedate",
					"new_opportunitytype", "new_pitchedcontractlength", "new_escalator",
					"campaignid", "new_leadsource", "budgetstatus", "new_pitchtype", "new_confidencelevel", "estimatedvalue"
				));

				if (!opp.Contains("new_opportunitytype"))
				{
					tracingService.Trace("ABORT: Opportunity does not have new_opportunitytype.");
					return;
				}

				int oppType = opp.GetAttributeValue<OptionSetValue>("new_opportunitytype").Value;
				if (oppType != 100000003 && oppType != 100000006)
				{
					tracingService.Trace($"ABORT: OppType {oppType} is not Prospect or Current.");
					return;
				}

				if (!opp.Contains("new_pitchedcontractlength"))
				{
					tracingService.Trace("ABORT: Opportunity does not have Pitched Contract Length.");
					return;
				}

				int optionValue = opp.GetAttributeValue<OptionSetValue>("new_pitchedcontractlength").Value;
				int totalYears = (optionValue - 100000000) + 1;

				if (totalYears <= 1)
				{
					tracingService.Trace($"ABORT: Contract length is {totalYears} year(s). No cloning needed.");
					return;
				}

				// 2. Retrieve Base Deal
				QueryExpression dealQuery = new QueryExpression("new_deals")
				{
					ColumnSet = new ColumnSet(
					"new_name", "new_accountid", "new_dealstatus", "new_dealtype",
					"new_partnershipassigneeemail", "new_salesperson", "new_serviceperson", "new_season"
				)
				};
				dealQuery.Criteria.AddCondition("new_opportunity", ConditionOperator.Equal, opp.Id);
				Entity baseDeal = service.RetrieveMultiple(dealQuery).Entities.FirstOrDefault();

				// Validation rule to prevent closing without a Deal
				if (baseDeal == null)
				{
					// Throwing this exception stops the save process and shows a popup error to the user.
					throw new InvalidPluginExecutionException("Validation Error: You cannot close a Multi-Year Opportunity (Contract Length > 1) as Won without at least one associated Deal.");
				}

				if (!baseDeal.Contains("new_season"))
				{
					// Also preventing save if the Deal exists but lacks a season, since math depends on it.
					throw new InvalidPluginExecutionException("Validation Error: The associated Deal is missing a 'Season'. A Season is required to accurately clone future Multi-Year Deals.");
				}

				// 3. Season Math
				EntityReference baseSeasonRef = baseDeal.GetAttributeValue<EntityReference>("new_season");
				Entity baseSeason = service.Retrieve("new_season", baseSeasonRef.Id, new ColumnSet("new_name", "new_seasonyear"));

				int startYear = baseSeason.GetAttributeValue<int>("new_seasonyear");
				string seasonName = baseSeason.GetAttributeValue<string>("new_name");
				string seasonSuffix = seasonName.Replace(startYear.ToString(), "").Trim(' ', '-');

				// 4. Financial Math setup 
				decimal escalatorPercent = opp.Contains("new_escalator") ? opp.GetAttributeValue<decimal>("new_escalator") : 0m;
				decimal multiplier = 1m + (escalatorPercent / 100m);
				decimal currentMultiplier = 1m;

				// 5. Retrieve Base Deal Lines 
				QueryExpression lineQuery = new QueryExpression("new_deallines")
				{
					ColumnSet = new ColumnSet(
					"new_name", "new_inventory", "new_quantity", "new_discount", "new_rate", "new_notes",
					"new_seasonid", "new_ratecard"
				)
				};
				lineQuery.Criteria.AddCondition("new_dealid", ConditionOperator.Equal, baseDeal.Id);
				EntityCollection baseLines = service.RetrieveMultiple(lineQuery);

				// --- CLONING LOOP ---
				for (int i = 2; i <= totalYears; i++)
				{
					int targetYear = startYear + (i - 1);
					currentMultiplier *= multiplier;

					// A. Find Target Season
					QueryExpression targetSeasonQuery = new QueryExpression("new_season") { ColumnSet = new ColumnSet("new_seasonid") };
					targetSeasonQuery.Criteria.AddCondition("new_seasonyear", ConditionOperator.Equal, targetYear);
					targetSeasonQuery.Criteria.AddCondition("new_name", ConditionOperator.Like, $"%{seasonSuffix}%");
					Entity targetSeason = service.RetrieveMultiple(targetSeasonQuery).Entities.FirstOrDefault();

					if (targetSeason == null)
						throw new InvalidPluginExecutionException($"Season not found for year '{targetYear}' and inventory '{seasonSuffix}'.");

					// B. Clone Opportunity (Management)
					Entity newOpp = new Entity("opportunity");
					newOpp["new_opportunitytype"] = new OptionSetValue(100000006); // Always Current (100000006)
					newOpp["new_basketballseason"] = targetSeason.ToEntityReference();

					if (opp.Contains("name"))
						newOpp["name"] = opp.GetAttributeValue<string>("name").Replace(startYear.ToString(), targetYear.ToString());

					string[] oppFieldsToCopy = {
						"parentaccountid", "parentcontactid", "campaignid", "new_leadsource",
						"budgetstatus", "new_pitchtype", "new_confidencelevel", "estimatedvalue"
					};
					foreach (string of in oppFieldsToCopy)
					{
						if (opp.Contains(of)) newOpp[of] = opp[of];
					}

					// Force cloned opportunities to be 1 Year long. 
					// This guarantees that if a user closes a cloned opp, the plugin aborts early and prevents an infinite loop.
					newOpp["new_pitchedcontractlength"] = new OptionSetValue(100000000);

					// Shift dates forward 
					if (opp.Contains("new_pitchdate"))
						newOpp["new_pitchdate"] = opp.GetAttributeValue<DateTime>("new_pitchdate").AddYears(i - 1);
					if (opp.Contains("estimatedclosedate"))
						newOpp["estimatedclosedate"] = opp.GetAttributeValue<DateTime>("estimatedclosedate").AddYears(i - 1);

					Guid newOppId = service.Create(newOpp);

					// C. Clone Deal
					Entity newDeal = new Entity("new_deals");
					if (baseDeal.Contains("new_name"))
						newDeal["new_name"] = baseDeal.GetAttributeValue<string>("new_name").Replace(startYear.ToString(), targetYear.ToString());

					newDeal["new_season"] = targetSeason.ToEntityReference();
					newDeal["new_originatingopportunity"] = opp.ToEntityReference();
					newDeal["new_opportunity"] = new EntityReference("opportunity", newOppId);

					// Copy Deal Status, Type, and personnel
					string[] dealFieldsToCopy = { "new_accountid", "new_dealstatus", "new_dealtype", "new_partnershipassigneeemail", "new_salesperson", "new_serviceperson" };
					foreach (string df in dealFieldsToCopy)
					{
						if (baseDeal.Contains(df)) newDeal[df] = baseDeal[df];
					}

					// Evaluate Risk
					newDeal["new_revenuecertainty"] = new OptionSetValue(100000000); // Default to Guaranteed

					Guid newDealId = service.Create(newDeal);

					// D. Clone Deal Lines
					foreach (Entity line in baseLines.Entities)
					{
						Entity newLine = new Entity("new_deallines");
						newLine["new_dealid"] = new EntityReference("new_deals", newDealId);

						if (line.Contains("new_name"))
							newLine["new_name"] = line.GetAttributeValue<string>("new_name").Replace(startYear.ToString(), targetYear.ToString());

						string[] lineFieldsToCopy = { "new_inventory", "new_quantity", "new_discount", "new_notes", "new_ratecard" };
						foreach (string lf in lineFieldsToCopy)
						{
							if (line.Contains(lf)) newLine[lf] = line[lf];
						}

						if (line.Contains("new_seasonid"))
						{
							newLine["new_seasonid"] = targetSeason.ToEntityReference();
						}

						// Escalate the input Rate (Rate Charged)
						if (line.Contains("new_rate"))
						{
							decimal baseValue = ((Money)line["new_rate"]).Value;

							// Rounding to the nearest whole dollar amount (0 decimal places).
							// MidpointRounding.AwayFromZero ensures standard commercial rounding (e.g., .5 rounds up to the next dollar).
							decimal escalatedValue = Math.Round(baseValue * currentMultiplier, 0, MidpointRounding.AwayFromZero);

							newLine["new_rate"] = new Money(escalatedValue);
						}

						service.Create(newLine);
					}
				}
				tracingService.Trace("Multi-Year Cloning (with Opportunities) Completed!");
			}
			catch (Exception ex)
			{
				tracingService.Trace($"EXCEPTION: {ex.Message}");
				// Si lanzamos la excepcion original de arriba, queremos que el usuario la lea limpia, sin el "Error generating..." extra.
				if (ex is InvalidPluginExecutionException)
				{
					throw;
				}
				throw new InvalidPluginExecutionException($"Error generating future deals: {ex.Message}");
			}
		}
	}
}