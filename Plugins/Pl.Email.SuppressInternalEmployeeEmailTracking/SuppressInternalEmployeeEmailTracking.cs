using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Email.SuppressInternalEmployeeEmailTracking
{
	public class SuppressInternalEmployeeEmailTracking : IPlugin
	{
		private const string InternalEmployeeField = "new_isinternalemployee";

		public void Execute(IServiceProvider serviceProvider)
		{
			// --- Core Dynamics SDK services ---
			var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			var tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
			var serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			var service = serviceFactory.CreateOrganizationService(context.UserId);

			tracingService.Trace("SuppressInternalEmployeeEmailTracking: Plugin execution started.");
			tracingService.Trace($"Stage: {context.Stage} | Message: {context.MessageName} | Entity: {context.PrimaryEntityName}");

			try
			{
				// --- Validate execution context ---
				if (context.MessageName.ToLower() != "create")
				{
					tracingService.Trace("Message is not 'Create'. Exiting plugin.");
					return;
				}

				if (context.Stage != 20) // 20 = Pre-Operation
				{
					tracingService.Trace("Stage is not Pre-Operation. Exiting plugin.");
					return;
				}

				if (!context.InputParameters.Contains("Target") ||
					!(context.InputParameters["Target"] is Entity))
				{
					tracingService.Trace("Target is missing or not an Entity. Exiting plugin.");
					return;
				}

				var target = (Entity)context.InputParameters["Target"];
				tracingService.Trace($"Email Target retrieved. Id: {target.Id}");

				// --- Party fields to inspect ---
				var partyFields = new[] { "from", "to", "cc", "bcc" };

				// --- Step 1: Collect all Contact IDs across all party fields ---
				// We do this first to enable bulk retrieval and avoid per-record DB calls
				var contactPartyMap = new Dictionary<Guid, List<(string Field, Entity Party)>>();

				foreach (var field in partyFields)
				{
					if (!target.Contains(field)) continue;

					var parties = target.GetAttributeValue<EntityCollection>(field);
					if (parties == null || parties.Entities.Count == 0) continue;

					tracingService.Trace($"Field '{field}' has {parties.Entities.Count} party record(s).");

					foreach (var party in parties.Entities)
					{
						var partyRef = party.GetAttributeValue<EntityReference>("partyid");

						if (partyRef == null || partyRef.LogicalName != "contact")
						{
							tracingService.Trace($"Field '{field}': partyid is null or not a Contact. Skipping.");
							continue;
						}

						if (!contactPartyMap.ContainsKey(partyRef.Id))
							contactPartyMap[partyRef.Id] = new List<(string, Entity)>();

						contactPartyMap[partyRef.Id].Add((field, party));
						tracingService.Trace($"Field '{field}': Queued Contact Id {partyRef.Id} for bulk retrieval.");
					}
				}

				if (contactPartyMap.Count == 0)
				{
					tracingService.Trace("No Contact-linked activity parties found. Exiting plugin.");
					return;
				}

				tracingService.Trace($"Total unique Contacts to evaluate: {contactPartyMap.Count}");

				// --- Step 2: Bulk retrieve all Contacts in a single DB call ---
				var contactQuery = new QueryExpression("contact")
				{
					ColumnSet = new ColumnSet("emailaddress1", InternalEmployeeField)
				};
				contactQuery.Criteria.AddCondition("contactid", ConditionOperator.In, contactPartyMap.Keys.Cast<object>().ToArray());

				var contacts = service.RetrieveMultiple(contactQuery);
				tracingService.Trace($"Contacts retrieved from DB: {contacts.Entities.Count}");

				// --- Step 3: Filter only internal employees ---
				var internalContactEmails = new Dictionary<Guid, string>(); // ContactId → email

				foreach (var contact in contacts.Entities)
				{
					var isInternal = contact.GetAttributeValue<bool>(InternalEmployeeField);
					var emailAddress = contact.GetAttributeValue<string>("emailaddress1") ?? string.Empty;

					bool shouldProcess = isInternal;

					tracingService.Trace($"Contact Id: {contact.Id} | Email: {emailAddress} | IsInternal: {isInternal} | ShouldProcess: {shouldProcess}");

					if (shouldProcess)
						internalContactEmails[contact.Id] = emailAddress;
				}

				if (internalContactEmails.Count == 0)
				{
					tracingService.Trace("No internal employee Contacts found. No swap needed. Exiting plugin.");
					return;
				}

				tracingService.Trace($"Internal employee Contacts identified: {internalContactEmails.Count}");

				// --- Step 4: Bulk retrieve matching SystemUsers in a single DB call ---
				var emailList = internalContactEmails.Values.Distinct().Cast<object>().ToArray();

				var userQuery = new QueryExpression("systemuser")
				{
					ColumnSet = new ColumnSet("systemuserid", "internalemailaddress")
				};
				userQuery.Criteria.AddCondition("internalemailaddress", ConditionOperator.In, emailList);

				var systemUsers = service.RetrieveMultiple(userQuery);
				tracingService.Trace($"SystemUsers retrieved from DB: {systemUsers.Entities.Count}");

				// Build a lookup: email (lowercase) → systemuser EntityReference
				var userLookup = systemUsers.Entities
					.ToDictionary(
						u => (u.GetAttributeValue<string>("internalemailaddress") ?? string.Empty).ToLower(),
						u => new EntityReference("systemuser", u.Id)
					);

				// --- Step 5: Perform the swap on the Target (in-memory, Pre-Operation) ---
				foreach (var kvp in internalContactEmails)
				{
					var contactId = kvp.Key;
					var contactEmail = kvp.Value.ToLower();

					if (!contactPartyMap.ContainsKey(contactId)) continue;

					foreach (var (field, party) in contactPartyMap[contactId])
					{
						if (userLookup.TryGetValue(contactEmail, out var systemUserRef))
						{
							// Swap: point partyid to SystemUser instead of Contact
							party["partyid"] = systemUserRef;
							party["partyobjecttypecode"] = "systemuser";
							tracingService.Trace($"Field '{field}': Swapped Contact {contactId} → SystemUser {systemUserRef.Id} for email '{contactEmail}'.");
						}
						else
						{
							// Fallback: no matching SystemUser found, leave Contact as-is
							tracingService.Trace($"Field '{field}': No matching SystemUser found for '{contactEmail}'. Leaving Contact reference as fallback.");
						}
					}
				}

				tracingService.Trace("SuppressInternalEmployeeEmailTracking: Plugin execution completed successfully.");
			}
			catch (Exception ex)
			{
				tracingService.Trace($"Unhandled exception in SuppressInternalEmployeeEmailTracking: {ex.Message}");
				tracingService.Trace($"Stack Trace: {ex.StackTrace}");
				throw new InvalidPluginExecutionException(
					$"SuppressInternalEmployeeEmailTracking failed: {ex.Message}", ex);
			}
		}
	}
}