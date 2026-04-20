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
						new ColumnSet("parentcontactid", "name"));

					OptionSetValue activityType = targetOpp.GetAttributeValue<OptionSetValue>("new_activitytype");
					string details = targetOpp.GetAttributeValue<string>("new_activitydetail");
					DateTime? dueDate = targetOpp.GetAttributeValue<DateTime?>("new_activityduedate") ?? DateTime.UtcNow;

					string activityLogicalName = "";
					string subjectLabel = "";
					int? subStatusValue = null;
					int? engagementTypeValue = null;
					bool markAsCompleted = true; // All completed for default
					switch (activityType.Value)
					{
						case 100000000:
							activityLogicalName = "phonecall";
							subjectLabel = "Phone Call";
							break;
						case 100000001:
							activityLogicalName = "task";
							subjectLabel = "Task";
							break;
						case 100000003:
							activityLogicalName = "email";
							subjectLabel = "Email";
							break;

						// APPOINTMENTS
						case 100000002: // Attended Meeting
							activityLogicalName = "appointment";
							subjectLabel = "Meeting Attended";
							subStatusValue = 100000001;
							break;
						case 100000004: // Set Meeting
							activityLogicalName = "appointment";
							subjectLabel = "Meeting Set";
							subStatusValue = 100000000;
							markAsCompleted = false; // "Set" meetings remain scheduled
							break;

						// NEW STANDALONE TEXT MESSAGE ENTITY
						case 100000005: // Text Message
							activityLogicalName = "new_textmessage";
							subjectLabel = "Text Message";
							break;

						// ENGAGEMENTS
						case 100000006: // Event Engagement
							activityLogicalName = "new_engagement";
							subjectLabel = "Event Engagement";
							engagementTypeValue = 100000001;
							break;
						case 100000007: // Game Engagement
							activityLogicalName = "new_engagement";
							subjectLabel = "Game Engagement";
							engagementTypeValue = 100000002;
							break;

					}

					if (!string.IsNullOrEmpty(activityLogicalName))
					{
						tracingService.Trace("Mapping activity type: {0}", activityLogicalName);
						Entity activity = new Entity(activityLogicalName);

						if (subStatusValue != null)
							activity["new_substatus"] = new OptionSetValue(subStatusValue.Value);

						if (engagementTypeValue != null)
							activity["new_engagementtype"] = new OptionSetValue(engagementTypeValue.Value);

						// Subject includes "Quick Log" prefix for traceability
						string oppName = fullOpp.GetAttributeValue<string>("name") ?? "N/A";
						activity["subject"] = $"Quick Log: {subjectLabel} - {oppName}"; 
						
						activity["description"] = details;
						activity["regardingobjectid"] = targetOpp.ToEntityReference();
						activity["scheduledend"] = dueDate;
						activity["ownerid"] = new EntityReference("systemuser", context.InitiatingUserId);

						// Automatic To and From mapping for Email and Phone Call
						if ((activityLogicalName == "email" || activityLogicalName == "phonecall") && fullOpp.Contains("parentcontactid"))
						{
							// 1. Set "To" (Recipient = Opportunity's Primary Contact)
							Entity partyTo = new Entity("activityparty");
							partyTo["partyid"] = fullOpp.GetAttributeValue<EntityReference>("parentcontactid");
							activity["to"] = new EntityCollection(new[] { partyTo });

							// 2. Set "From" (Sender = Current user executing the plugin)
							Entity partyFrom = new Entity("activityparty");
							partyFrom["partyid"] = new EntityReference("systemuser", context.InitiatingUserId);
							activity["from"] = new EntityCollection(new[] { partyFrom });
						}

						Guid activityId = service.Create(activity);
						tracingService.Trace("Activity created with ID: {0}", activityId);

						tracingService.Trace("Updating activity status to Completed.");
						if (markAsCompleted)
						{
							Entity updateStatus = new Entity(activityLogicalName, activityId);
							updateStatus["statecode"] = new OptionSetValue(1); //Completed

							if (activityLogicalName == "phonecall" || activityLogicalName == "email")
								updateStatus["statuscode"] = new OptionSetValue(2);
							else if (activityLogicalName == "task")
								updateStatus["statuscode"] = new OptionSetValue(5);
							else if (activityLogicalName == "appointment")
								updateStatus["statuscode"] = new OptionSetValue(3);
							else if (activityLogicalName == "new_engagement" || activityLogicalName == "new_textmessage")
								updateStatus["statuscode"] = new OptionSetValue(2); //Completed

							service.Update(updateStatus);
						}
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