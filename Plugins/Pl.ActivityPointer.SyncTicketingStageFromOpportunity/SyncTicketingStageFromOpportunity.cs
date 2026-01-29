using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.ActivityPointer.SyncTicketingStageFromOpportunity
{
	public class SyncTicketingStageFromOpportunity : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

			tracingService.Trace("SyncTicketingStageFromOpportunity Plugin started.");

			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
			{
				Entity activity = (Entity)context.InputParameters["Target"];

				if (activity.Contains("regardingobjectid") && activity["regardingobjectid"] is EntityReference regarding)
				{
					if (regarding.LogicalName == "opportunity")
					{
						try
						{
							tracingService.Trace("Activity linked to Opportunity ID: {0}. Retrieving stage.", regarding.Id);
							Entity opportunity = service.Retrieve("opportunity", regarding.Id, new ColumnSet("new_ticketingstage"));

							if (opportunity.Contains("new_ticketingstage"))
							{
								OptionSetValue stageValue = opportunity.GetAttributeValue<OptionSetValue>("new_ticketingstage");
								tracingService.Trace("Stage found: {0}. Syncing to activity.", stageValue.Value);
								activity["new_ticketingstage"] = stageValue;
							}
							else
							{
								tracingService.Trace("No Ticketing Stage value found on Opportunity.");
							}
						}
						catch (Exception ex)
						{
							tracingService.Trace("Error occurred: {0}", ex.ToString());
							throw new InvalidPluginExecutionException("Error syncing Ticketing Stage: " + ex.Message);
						}
					}
					else
					{
						tracingService.Trace("Activity is regarding '{0}', not an Opportunity. Skipping.", regarding.LogicalName);
					}
				}
			}
		}
	}
}