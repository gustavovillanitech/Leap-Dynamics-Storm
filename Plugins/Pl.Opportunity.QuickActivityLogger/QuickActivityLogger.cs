using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Opportunity.QuickActivityLogger
{
	public class QuickActivityLogger : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
			{
				Entity targetOpp = (Entity)context.InputParameters["Target"];
				if (targetOpp.LogicalName != "opportunity") return;

				// Only proceed if the Activity Type field contains data
				if (!targetOpp.Contains("new_activitytype") || targetOpp["new_activitytype"] == null) return;

				try
				{
					// Retrieve additional Opportunity data (Primary Contact and Ticketing Stage)
					Entity fullOpp = service.Retrieve("opportunity", targetOpp.Id,
						new ColumnSet("parentcontactid", "new_ticketingstage", "name"));

					OptionSetValue activityType = targetOpp.GetAttributeValue<OptionSetValue>("new_activitytype");
					string details = targetOpp.GetAttributeValue<string>("new_activitydetail");
					OptionSetValue outcome = targetOpp.GetAttributeValue<OptionSetValue>("new_opportunityoutcome");
					DateTime? dueDate = targetOpp.GetAttributeValue<DateTime?>("new_activityduedate") ?? DateTime.UtcNow;

					// Capture the current stage as an OptionSetValue
					OptionSetValue currentStage = fullOpp.GetAttributeValue<OptionSetValue>("new_ticketingstage");

					Entity activity = null;
					string activityLogicalName = "";

					// 1. Activity Type Mapping
					switch (activityType.Value)
					{
						case 100000000: activityLogicalName = "phonecall"; break;
						case 100000001: activityLogicalName = "task"; break;
						case 100000002: activityLogicalName = "appointment"; break;
						case 100000003: activityLogicalName = "email"; break;
					}

					if (!string.IsNullOrEmpty(activityLogicalName))
					{
						activity = new Entity(activityLogicalName);
						activity["subject"] = $"Quick Log: {activityLogicalName} - {fullOpp.GetAttributeValue<string>("name")}";
						activity["description"] = details;
						activity["regardingobjectid"] = targetOpp.ToEntityReference();

						// Map the Ticketing Stage OptionSet value directly to the activity
						if (currentStage != null)
						{
							activity["new_ticketingstage"] = new OptionSetValue(currentStage.Value);
						}

						activity["scheduledend"] = dueDate;

						if (activityLogicalName == "email")
						{
							if (fullOpp.Contains("parentcontactid"))
							{
								Entity party = new Entity("activityparty");
								party["partyid"] = fullOpp.GetAttributeValue<EntityReference>("parentcontactid");
								activity["to"] = new EntityCollection(new[] { party });
							}
						}
						activity["ownerid"] = new EntityReference("systemuser", context.InitiatingUserId);

						// 2. Create the activity record
						Guid activityId = service.Create(activity);

						// 3. Mark as Completed
						Entity updateStatus = new Entity(activityLogicalName, activityId);

						if (activityLogicalName == "phonecall" || activityLogicalName == "email") { updateStatus["statuscode"] = new OptionSetValue(2); updateStatus["statecode"] = new OptionSetValue(1); } //Completed
						else if (activityLogicalName == "task") { updateStatus["statuscode"] = new OptionSetValue(5); updateStatus["statecode"] = new OptionSetValue(1); } //Completed
						else if (activityLogicalName == "appointment") { updateStatus["statuscode"] = new OptionSetValue(3); updateStatus["statecode"] = new OptionSetValue(1); } //Completed

						service.Update(updateStatus);

						// 4. Cleanup Opportunity fields for the next entry
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
					throw new InvalidPluginExecutionException("Quick Logger Plugin Error: " + ex.Message);
				}
			}
		}
	}
}