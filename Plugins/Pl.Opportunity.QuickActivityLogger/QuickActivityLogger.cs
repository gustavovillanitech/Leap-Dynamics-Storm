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

				// Validate which actions the user requested (Multi-client safe)
				bool createMainActivity = targetOpp.Contains("new_activitytype") && targetOpp["new_activitytype"] != null;
				bool createFollowUpActivity = targetOpp.Contains("new_followupactivitytype") && targetOpp["new_followupactivitytype"] != null;

				// If there is no main activity type and no follow-up activity type, there's nothing to process
				if (!createMainActivity && !createFollowUpActivity)
				{
					tracingService.Trace("No Main Activity Type or Follow-Up Activity Type found. Exiting.");
					return;
				}

				try
				{
					tracingService.Trace("Retrieving Opportunity details for ID: {0}", targetOpp.Id);
					Entity fullOpp = service.Retrieve("opportunity", targetOpp.Id, new ColumnSet("parentcontactid", "name"));
					string oppName = fullOpp.GetAttributeValue<string>("name") ?? "N/A";

					// ==========================================
					// 1. MAIN ACTIVITY CREATION
					// ==========================================
					if (createMainActivity)
					{
						tracingService.Trace("Processing Main Activity.");
						OptionSetValue activityType = targetOpp.GetAttributeValue<OptionSetValue>("new_activitytype");
						string details = targetOpp.GetAttributeValue<string>("new_activitydetail");
						DateTime? dueDate = targetOpp.GetAttributeValue<DateTime?>("new_activityduedate") ?? DateTime.UtcNow;

						string activityLogicalName = "";
						string subjectLabel = "";
						int? subStatusValue = null;
						int? engagementTypeValue = null;
						bool markAsCompleted = true; // All completed by default

						// Check if the user opted to keep the main activity open (Multi-client safe check)
						if (targetOpp.Contains("new_createasopenactivity") && targetOpp.GetAttributeValue<bool>("new_createasopenactivity"))
						{
							markAsCompleted = false;
							tracingService.Trace("Create as Open Activity flag is checked. Main activity will remain open.");
						}

						switch (activityType.Value)
						{
							case 100000000:
								activityLogicalName = "phonecall"; subjectLabel = "Phone Call"; break;
							case 100000001:
								activityLogicalName = "task"; subjectLabel = "Task"; break;
							case 100000003:
								activityLogicalName = "email"; subjectLabel = "Email"; break;
							case 100000002: // Attended Meeting
								activityLogicalName = "appointment"; subjectLabel = "Meeting Attended"; subStatusValue = 100000001; break;
							case 100000004: // Set Meeting
								activityLogicalName = "appointment"; subjectLabel = "Meeting Set"; subStatusValue = 100000000; markAsCompleted = false; break;
							case 100000005: // Text Message
								activityLogicalName = "new_textmessage"; subjectLabel = "Text Message"; break;
							case 100000006: // Event Engagement
								activityLogicalName = "new_engagement"; subjectLabel = "Event Engagement"; engagementTypeValue = 100000001; break;
							case 100000007: // Game Engagement
								activityLogicalName = "new_engagement"; subjectLabel = "Game Engagement"; engagementTypeValue = 100000002; break;
						}

						if (!string.IsNullOrEmpty(activityLogicalName))
						{
							tracingService.Trace("Mapping activity type: {0}", activityLogicalName);
							Entity activity = new Entity(activityLogicalName);

							if (subStatusValue != null) activity["new_substatus"] = new OptionSetValue(subStatusValue.Value);
							if (engagementTypeValue != null) activity["new_engagementtype"] = new OptionSetValue(engagementTypeValue.Value);

							// Subject includes "Quick Log" prefix for traceability
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

							if (markAsCompleted)
							{
								tracingService.Trace("Updating activity status to Completed.");
								Entity updateStatus = new Entity(activityLogicalName, activityId);
								updateStatus["statecode"] = new OptionSetValue(1); //Completed

								if (activityLogicalName == "phonecall" || activityLogicalName == "email" || activityLogicalName == "new_engagement" || activityLogicalName == "new_textmessage")
									updateStatus["statuscode"] = new OptionSetValue(2);
								else if (activityLogicalName == "task")
									updateStatus["statuscode"] = new OptionSetValue(5);
								else if (activityLogicalName == "appointment")
									updateStatus["statuscode"] = new OptionSetValue(3);

								service.Update(updateStatus);
							}
						}
					}

					// ==========================================
					// 2. FOLLOW-UP ACTIVITY CREATION
					// ==========================================
					if (createFollowUpActivity)
					{
						tracingService.Trace("Processing Follow-up Activity.");
						OptionSetValue fuActivityType = targetOpp.GetAttributeValue<OptionSetValue>("new_followupactivitytype");
						string fuDetails = targetOpp.GetAttributeValue<string>("new_followupactivitydetail");
						DateTime? fuDueDate = targetOpp.GetAttributeValue<DateTime?>("new_followupactivityduedate") ?? DateTime.UtcNow;

						string fuLogicalName = "";
						string fuSubjectLabel = "";
						int? fuSubStatusValue = null;
						int? fuEngagementTypeValue = null;

						switch (fuActivityType.Value)
						{
							case 100000000: fuLogicalName = "phonecall"; fuSubjectLabel = "Phone Call"; break;
							case 100000001: fuLogicalName = "task"; fuSubjectLabel = "Task"; break;
							case 100000003: fuLogicalName = "email"; fuSubjectLabel = "Email"; break;
							case 100000002: fuLogicalName = "appointment"; fuSubjectLabel = "Meeting Attended"; fuSubStatusValue = 100000001; break;
							case 100000004: fuLogicalName = "appointment"; fuSubjectLabel = "Meeting Set"; fuSubStatusValue = 100000000; break;
							case 100000005: fuLogicalName = "new_textmessage"; fuSubjectLabel = "Text Message"; break;
							case 100000006: fuLogicalName = "new_engagement"; fuSubjectLabel = "Event Engagement"; fuEngagementTypeValue = 100000001; break;
							case 100000007: fuLogicalName = "new_engagement"; fuSubjectLabel = "Game Engagement"; fuEngagementTypeValue = 100000002; break;
						}

						if (!string.IsNullOrEmpty(fuLogicalName))
						{
							tracingService.Trace("Creating follow-up activity: {0}", fuLogicalName);
							Entity fuActivity = new Entity(fuLogicalName);

							if (fuSubStatusValue != null) fuActivity["new_substatus"] = new OptionSetValue(fuSubStatusValue.Value);
							if (fuEngagementTypeValue != null) fuActivity["new_engagementtype"] = new OptionSetValue(fuEngagementTypeValue.Value);

							fuActivity["subject"] = $"Follow-Up: {fuSubjectLabel} - {oppName}";
							fuActivity["description"] = fuDetails;
							fuActivity["regardingobjectid"] = targetOpp.ToEntityReference();
							fuActivity["scheduledend"] = fuDueDate;
							fuActivity["ownerid"] = new EntityReference("systemuser", context.InitiatingUserId);

							// Automatic To and From mapping for Follow-Up Email and Phone Call
							if ((fuLogicalName == "email" || fuLogicalName == "phonecall") && fullOpp.Contains("parentcontactid"))
							{
								Entity partyTo = new Entity("activityparty");
								partyTo["partyid"] = fullOpp.GetAttributeValue<EntityReference>("parentcontactid");
								fuActivity["to"] = new EntityCollection(new[] { partyTo });

								Entity partyFrom = new Entity("activityparty");
								partyFrom["partyid"] = new EntityReference("systemuser", context.InitiatingUserId);
								fuActivity["from"] = new EntityCollection(new[] { partyFrom });
							}

							Guid fuActivityId = service.Create(fuActivity);
							tracingService.Trace("Follow-up activity created successfully with ID: {0}. (Created as Open by default)", fuActivityId);
						}
					}

					// ==========================================
					// 3. CLEANUP
					// ==========================================
					tracingService.Trace("Cleaning up Opportunity logger fields.");
					Entity oppCleanup = new Entity("opportunity", targetOpp.Id);

					// Only clean up fields if they were present in the target payload (Multi-client safe)
					if (targetOpp.Contains("new_activitytype")) oppCleanup["new_activitytype"] = null;
					if (targetOpp.Contains("new_activitydetail")) oppCleanup["new_activitydetail"] = null;
					if (targetOpp.Contains("new_opportunityoutcome")) oppCleanup["new_opportunityoutcome"] = null;
					if (targetOpp.Contains("new_activityduedate")) oppCleanup["new_activityduedate"] = null;
					if (targetOpp.Contains("new_createasopenactivity")) oppCleanup["new_createasopenactivity"] = false;
					if (targetOpp.Contains("new_followupactivitytype")) oppCleanup["new_followupactivitytype"] = null;
					if (targetOpp.Contains("new_followupactivityduedate")) oppCleanup["new_followupactivityduedate"] = null;
					if (targetOpp.Contains("new_followupactivitydetail")) oppCleanup["new_followupactivitydetail"] = null;

					service.Update(oppCleanup);
					tracingService.Trace("Plugin execution completed successfully.");

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