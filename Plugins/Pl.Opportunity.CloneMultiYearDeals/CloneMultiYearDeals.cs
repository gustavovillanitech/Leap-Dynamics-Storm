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

			try
			{
				if (context.Depth > 1) return;

				Guid oppId = Guid.Empty;

				if (context.MessageName.ToLower() == "win" && context.InputParameters.Contains("OpportunityClose") && context.InputParameters["OpportunityClose"] is Entity)
				{
					oppId = ((Entity)context.InputParameters["OpportunityClose"]).GetAttributeValue<EntityReference>("opportunityid").Id;
				}
				else if (context.MessageName.ToLower() == "update" && context.PrimaryEntityName == "opportunity")
				{
					Entity target = (Entity)context.InputParameters["Target"];
					if (!target.Contains("statecode") || target.GetAttributeValue<OptionSetValue>("statecode").Value != 1) return;
					oppId = target.Id;
				}
				else return;

				if (oppId == Guid.Empty) return;

				// 1. Retrieve Master Opportunity 
				Entity opp = service.Retrieve("opportunity", oppId, new ColumnSet(
					"name", "parentaccountid", "parentcontactid", "new_pitchdate", "estimatedclosedate",
					"new_opportunitytype", "new_pitchedcontractlength", "new_escalator"
				));

				if (!opp.Contains("new_opportunitytype")) return;
				int oppType = opp.GetAttributeValue<OptionSetValue>("new_opportunitytype").Value;
				if (oppType != 100000003 && oppType != 100000006) return; //Corporate Partnership Prospect (100000003) or Corporate Partnership Current (100000006) Only

				if (!opp.Contains("new_pitchedcontractlength")) return;
				int optionValue = opp.GetAttributeValue<OptionSetValue>("new_pitchedcontractlength").Value;
				int totalYears = (optionValue - 100000000) + 1;
				if (totalYears <= 1) return;

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

				if (baseDeal == null || !baseDeal.Contains("new_season")) return;

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

				// 5. Retrieve Base Deal Lines (Only unlocked fields we need to copy/escalate)
				QueryExpression lineQuery = new QueryExpression("new_deallines")
				{
					ColumnSet = new ColumnSet(
					"new_name", "new_inventory", "new_quantity", "new_discount", "new_rate", "new_notes"
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

					if (opp.Contains("parentaccountid")) newOpp["parentaccountid"] = opp["parentaccountid"];

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
					newDeal["new_originatingopportunity"] = opp.ToEntityReference(); // Master Sale
					newDeal["new_opportunity"] = new EntityReference("opportunity", newOppId); // Management Sale (New Opp)

					// Copy Deal Status, Type, and personnel
					string[] dealFieldsToCopy = { "new_accountid", "new_dealstatus", "new_dealtype", "new_partnershipassigneeemail", "new_salesperson", "new_serviceperson" };
					foreach (string df in dealFieldsToCopy)
					{
						if (baseDeal.Contains(df)) newDeal[df] = baseDeal[df];
					}

					// Evaluate Risk
					// Cloned deals will now always default to 'Guaranteed' (100000000). 
					// The new Opt-Out Type field and Business Rule will handle any 'At Risk' logic dynamically from the UI.
					newDeal["new_revenuecertainty"] = new OptionSetValue(100000000);

					Guid newDealId = service.Create(newDeal);

					// D. Clone Deal Lines
					foreach (Entity line in baseLines.Entities)
					{
						Entity newLine = new Entity("new_deallines");
						newLine["new_dealid"] = new EntityReference("new_deals", newDealId);

						if (line.Contains("new_name"))
							newLine["new_name"] = line.GetAttributeValue<string>("new_name").Replace(startYear.ToString(), targetYear.ToString());

						// Copy direct input fields (No locked fields)
						string[] lineFieldsToCopy = { "new_inventory", "new_quantity", "new_discount", "new_notes" };
						foreach (string lf in lineFieldsToCopy)
						{
							if (line.Contains(lf)) newLine[lf] = line[lf];
						}

						// Escalate the input Rate (Rate Charged)
						if (line.Contains("new_rate"))
						{
							decimal baseValue = ((Money)line["new_rate"]).Value;
							decimal escalatedValue = Math.Round(baseValue * currentMultiplier, 2);
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
				throw new InvalidPluginExecutionException($"Error generating future deals: {ex.Message}");
			}
		}
	}
}