using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Opportunity.QuickActivityLogger
{
	public class QuickActivityLogger : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

			tracingService.Trace("QuickActivityLogger Plugin started.");

			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
			{
				Entity targetOpp = (Entity)context.InputParameters["Target"];
				if (targetOpp.LogicalName != "opportunity") return;

				if (!targetOpp.Contains("new_activitytype") || targetOpp["new_activitytype"] == null)
				{
					tracingService.Trace("No Activity Type found. Exiting.");
					return;
				}

				try
				{
					tracingService.Trace("Retrieving Opportunity details for ID: {0}", targetOpp.Id);
					Entity fullOpp = service.Retrieve("opportunity", targetOpp.Id,
						new ColumnSet("parentcontactid", "new_ticketingstage", "name"));

					OptionSetValue activityType = targetOpp.GetAttributeValue<OptionSetValue>("new_activitytype");
					string details = targetOpp.GetAttributeValue<string>("new_activitydetail");
					DateTime? dueDate = targetOpp.GetAttributeValue<DateTime?>("new_activityduedate") ?? DateTime.UtcNow;

					string activityLogicalName = "";
					switch (activityType.Value)
					{
						case 100000000: activityLogicalName = "phonecall"; break;
						case 100000001: activityLogicalName = "task"; break;
						case 100000002: activityLogicalName = "appointment"; break;
						case 100000003: activityLogicalName = "email"; break;
					}

					if (!string.IsNullOrEmpty(activityLogicalName))
					{
						tracingService.Trace("Mapping activity type: {0}", activityLogicalName);
						Entity activity = new Entity(activityLogicalName);
						activity["subject"] = $"Quick Log: {activityLogicalName} - {fullOpp.GetAttributeValue<string>("name")}";
						activity["description"] = details;
						activity["regardingobjectid"] = targetOpp.ToEntityReference();
						activity["scheduledend"] = dueDate;
						activity["ownerid"] = new EntityReference("systemuser", context.InitiatingUserId);

						if (activityLogicalName == "email" && fullOpp.Contains("parentcontactid"))
						{
							Entity party = new Entity("activityparty");
							party["partyid"] = fullOpp.GetAttributeValue<EntityReference>("parentcontactid");
							activity["to"] = new EntityCollection(new[] { party });
						}

						Guid activityId = service.Create(activity);
						tracingService.Trace("Activity created with ID: {0}", activityId);

						tracingService.Trace("Updating activity status to Completed.");
						Entity updateStatus = new Entity(activityLogicalName, activityId);
						updateStatus["statecode"] = new OptionSetValue(1);

						if (activityLogicalName == "phonecall" || activityLogicalName == "email") updateStatus["statuscode"] = new OptionSetValue(2);
						else if (activityLogicalName == "task") updateStatus["statuscode"] = new OptionSetValue(5);
						else if (activityLogicalName == "appointment") updateStatus["statuscode"] = new OptionSetValue(3);

						service.Update(updateStatus);

						tracingService.Trace("Cleaning up Opportunity logger fields.");
						Entity oppCleanup = new Entity("opportunity", targetOpp.Id);
						oppCleanup["new_activitytype"] = null;
						oppCleanup["new_activitydetail"] = null;
						oppCleanup["new_opportunityoutcome"] = null;
						oppCleanup["new_activityduedate"] = null;
						service.Update(oppCleanup);
					}
				}
				catch (Exception ex)
				{
					tracingService.Trace("Error occurred: {0}", ex.ToString());
					throw new InvalidPluginExecutionException("Quick Logger Plugin Error: " + ex.Message);
				}
			}
		}
	}
}