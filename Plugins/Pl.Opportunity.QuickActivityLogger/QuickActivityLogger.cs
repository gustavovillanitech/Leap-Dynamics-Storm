using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

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
					// Retrieve additional Opportunity data (Primary Contact and Stage)
					Entity fullOpp = service.Retrieve("opportunity", targetOpp.Id,
						new ColumnSet("parentcontactid", "new_ticketingstage", "name"));

					OptionSetValue activityType = targetOpp.GetAttributeValue<OptionSetValue>("new_activitytype");
					string details = targetOpp.GetAttributeValue<string>("new_activitydetail");
					OptionSetValue outcome = targetOpp.GetAttributeValue<OptionSetValue>("new_opportunityOutcome");
					DateTime? dueDate = targetOpp.GetAttributeValue<DateTime?>("new_activityduedate") ?? DateTime.Now;

					// Capture the current stage name
					string currentStage = fullOpp.FormattedValues.Contains("new_ticketingstage")
						? fullOpp.FormattedValues["new_ticketingstage"]
						: "Unknown Stage";

					Entity activity = null;
					string activityLogicalName = "";

					// 1. Activity Type Mapping
					// NOTE: Verify the Integer values of your "new_activitytype" OptionSet
					switch (activityType.Value)
					{
						case 1: activityLogicalName = "phonecall"; break;
						case 2: activityLogicalName = "appointment"; break;
						case 3: activityLogicalName = "task"; break;
						case 4: activityLogicalName = "email"; break;
					}

					if (!string.IsNullOrEmpty(activityLogicalName))
					{
						activity = new Entity(activityLogicalName);
						activity["subject"] = $"Quick Log: {activityLogicalName} - {fullOpp.GetAttributeValue<string>("name")}";
						activity["description"] = details;
						activity["regardingobjectid"] = targetOpp.ToEntityReference();
						activity["new_ticketingstage"] = currentStage;
						activity["scheduledend"] = dueDate; // Maps the due date to the activity end date

						// Map outcome to the corresponding field in the activity if applicable
						// activity["new_outcome_field"] = outcome; 

						if (activityLogicalName == "email")
						{
							// Email Logic: Link to the Primary Contact as the recipient
							if (fullOpp.Contains("parentcontactid"))
							{
								Entity party = new Entity("activityparty");
								party["partyid"] = fullOpp.GetAttributeValue<EntityReference>("parentcontactid");
								activity["to"] = new EntityCollection(new[] { party });
							}
						}

						// 2. Create the activity record
						Guid activityId = service.Create(activity);

						// 3. Mark as Completed
						// StateCode 1 represents 'Completed' for PhoneCall, Task, Email, and Appointment
						Entity updateStatus = new Entity(activityLogicalName, activityId);
						updateStatus["statecode"] = new OptionSetValue(1);

						// StatusCode mapping varies by entity
						if (activityLogicalName == "phonecall") updateStatus["statuscode"] = new OptionSetValue(2); // Made
						else if (activityLogicalName == "task") updateStatus["statuscode"] = new OptionSetValue(5); // Completed
						else updateStatus["statuscode"] = new OptionSetValue(-1); // Default completed status

						service.Update(updateStatus);

						// 4. Cleanup Opportunity fields for the next entry
						Entity oppCleanup = new Entity("opportunity", targetOpp.Id);
						oppCleanup["new_activitytype"] = null;
						oppCleanup["new_activitydetail"] = null;
						oppCleanup["new_opportunityOutcome"] = null;
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