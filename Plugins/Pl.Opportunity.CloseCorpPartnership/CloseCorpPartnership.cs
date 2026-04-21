using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Crm.Sdk.Messages; // Required for WinOpportunityRequest & LoseOpportunityRequest

namespace Pl.Opportunity.CloseCorpPartnership
{
	public class CloseCorpPartnership : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			// Standard Plugin setup
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = factory.CreateOrganizationService(context.UserId);
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			tracingService.Trace("--- PLUGIN STARTED: CloseCorpPartnership (Sync Close Process) ---");

			try
			{
				// Prevent infinite loops, but allow Depth up to 2 just in case it's triggered by another safe sync process
				if (context.Depth > 2)
				{
					tracingService.Trace("ABORT: Context Depth > 2. Exiting to prevent infinite loops.");
					return;
				}

				// Check for valid context (Must be an Update on the Opportunity entity)
				if (context.MessageName.ToLower() != "update" || context.PrimaryEntityName != "opportunity")
				{
					tracingService.Trace("ABORT: Not an Update message or not an Opportunity entity.");
					return;
				}

				if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity))
				{
					tracingService.Trace("ABORT: Target is missing or invalid.");
					return;
				}

				Entity target = (Entity)context.InputParameters["Target"];

				// Check if the triggering update includes the 'new_salesstage' field
				if (!target.Contains("new_salesstage"))
				{
					tracingService.Trace("ABORT: 'new_salesstage' field was not updated in this transaction.");
					return;
				}

				int salesStage = target.GetAttributeValue<OptionSetValue>("new_salesstage").Value;
				tracingService.Trace($"Triggered by Sales Stage change. New Value: {salesStage}");

				// Retrieve the current state of the opportunity to ensure it's Open (0), 
				// and grab the 'estimatedvalue' which is needed for Won opportunities.
				Entity oppToClose = service.Retrieve("opportunity", target.Id, new ColumnSet("statecode", "estimatedvalue"));

				if (oppToClose.GetAttributeValue<OptionSetValue>("statecode").Value != 0) // 0 = Open
				{
					tracingService.Trace("ABORT: Opportunity is already Closed (Won/Lost). No action taken.");
					return;
				}

				// Set estimated value to 0 if null, otherwise grab the value
				decimal estimatedValue = oppToClose.Contains("estimatedvalue") ? oppToClose.GetAttributeValue<Money>("estimatedvalue").Value : 0m;

				// Execute logic based on the Flow (JSON) logic mapped to C#
				switch (salesStage)
				{
					// WON SCENARIOS: 10 - Closed - Current OR 10 - Closed - Prospect
					case 100000017:
					case 100000003:
						tracingService.Trace("Action: Closing Opportunity as WON.");

						Entity winOppClose = new Entity("opportunityclose");
						winOppClose["opportunityid"] = new EntityReference("opportunity", target.Id);
						winOppClose["actualend"] = DateTime.UtcNow;
						winOppClose["actualrevenue"] = new Money(estimatedValue);

						WinOpportunityRequest winReq = new WinOpportunityRequest
						{
							OpportunityClose = winOppClose,
							Status = new OptionSetValue(3) // Standard Status Code for Won
						};

						service.Execute(winReq);
						tracingService.Trace("SUCCESS: Opportunity closed as WON.");
						tracingService.Trace("Proceeding to close associated Deals as Won...");
						CloseAssociatedDealsAsWon(target.Id, service, tracingService);
						break;

					// LOST SCENARIOS: 11 - Declined - Prospect OR 11 - Declined - Current
					case 100000004:
					case 100000018:
						tracingService.Trace("Action: Closing Opportunity as LOST.");

						Entity loseOppClose = new Entity("opportunityclose");
						loseOppClose["opportunityid"] = new EntityReference("opportunity", target.Id);
						loseOppClose["actualend"] = DateTime.UtcNow;
						loseOppClose["actualrevenue"] = new Money(0m); // Lost deals always have $0 actual revenue

						LoseOpportunityRequest loseReq = new LoseOpportunityRequest
						{
							OpportunityClose = loseOppClose,
							Status = new OptionSetValue(100000001) // Custom Status Code for Lost (from JSON)
						};

						service.Execute(loseReq);
						tracingService.Trace("SUCCESS: Opportunity closed as LOST.");
						break;

					default:
						tracingService.Trace($"ABORT: Sales Stage '{salesStage}' does not trigger a close action. Doing nothing.");
						break;
				}
			}
			catch (Exception ex)
			{
				tracingService.Trace($"EXCEPTION: {ex.Message}");
				// This throw is crucial! It will perform the rollback and show the error on the user's screen.
				throw new InvalidPluginExecutionException($"Error processing Opportunity Close based on Sales Stage: {ex.Message}");
			}
		}
		private void CloseAssociatedDealsAsWon(Guid opportunityId, IOrganizationService service, ITracingService tracing)
		{
			// Retrieve the "Closed Won" status record (DS-1008)
			Guid? closedWonId = GetDealStatusIdByCode("DS-1008", service, tracing);
			if (!closedWonId.HasValue)
			{
				tracing.Trace("WARNING: Could not find Deal Status DS-1008. Skipping Deal status update.");
				return;
			}

			// Query all active Deals of this Opportunity that are NOT Playoff Deals
			// (new_regularseasondeal is null means it's a regular Deal, not a Playoff)
			QueryExpression q = new QueryExpression("new_deals")
			{
				ColumnSet = new ColumnSet("new_dealsid", "new_name")
			};
			q.Criteria.AddCondition("new_opportunity", ConditionOperator.Equal, opportunityId);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // Active only
			q.Criteria.AddCondition("new_regularseasondeal", ConditionOperator.Null); // NOT a Playoff Deal

			EntityCollection deals = service.RetrieveMultiple(q);
			tracing.Trace($"Found {deals.Entities.Count} active regular Deals to close as Won.");

			foreach (Entity deal in deals.Entities)
			{
				string name = deal.GetAttributeValue<string>("new_name") ?? "Unknown";
				tracing.Trace($"Closing Deal '{name}' ({deal.Id}) as Won.");

				Entity update = new Entity("new_deals", deal.Id);
				update["new_dealstatus"] = new EntityReference("new_dealstatus", closedWonId.Value);
				service.Update(update);
			}
		}

		private Guid? GetDealStatusIdByCode(string code, IOrganizationService service, ITracingService tracing)
		{
			QueryExpression q = new QueryExpression("new_dealstatus") { ColumnSet = new ColumnSet("new_dealstatusid") };
			q.Criteria.AddCondition("new_code", ConditionOperator.Equal, code);
			q.TopCount = 1;
			var results = service.RetrieveMultiple(q);
			if (results.Entities.Count == 0)
			{
				tracing.Trace($"No Deal Status found with code '{code}'.");
				return null;
			}
			return results.Entities[0].Id;
		}
	}
}