using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Linq;
using System.Security.Principal;

namespace Pl.Deal.OptionAutomation
{
	public class OptionAutomation : IPlugin
	{
		// Status codes (from new_dealstatus lookup entity - the new_code field)
		private const string STATUS_CLOSED_WON = "DS-1008";
		private const string STATUS_CLOSED_LOST = "DS-1009";

		// new_dealoptiondecision optionset values
		private const int DECISION_CLIENT_OPTED_IN = 100000000;
		private const int DECISION_CLIENT_OPTED_OUT = 100000001;
		private const int DECISION_MUTUAL_OPTED_OUT = 100000002;
		private const int DECISION_MUTUAL_OPTED_IN = 100000003;
		private const int DECISION_STORM_OPTED_OUT = 100000004;

		// new_playoffoptiondecision optionset values
		private const int PLAYOFF_DECISION_CLIENT_OPTED_IN = 100000000;
		private const int PLAYOFF_DECISION_CLIENT_OPTED_OUT = 100000001;

		// new_playoffoptionstatus optionset values (triggers playoff deal creation)
		private const int PLAYOFF_STATUS_OPT_IN = 100000000;
		private const int PLAYOFF_STATUS_IN = 100000002;

		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = factory.CreateOrganizationService(context.UserId);
			ITracingService tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			tracing.Trace($"--- DealOptionAutomation START. Depth: {context.Depth} ---");

			// Prevent infinite loops
			if (context.Depth > 2)
			{
				tracing.Trace("ABORT: Depth > 2.");
				return;
			}

			if (context.MessageName.ToLower() != "update" || context.PrimaryEntityName != "new_deals")
				return;

			if (!context.InputParameters.Contains("Target") || !(context.InputParameters["Target"] is Entity))
				return;

			Entity target = (Entity)context.InputParameters["Target"];
			Entity preImage = context.PreEntityImages.Contains("PreImage")
				? context.PreEntityImages["PreImage"]
				: new Entity("new_deals");

			try
			{
				// FLUJO 1: Opt-Out → Closed Lost (+ cascada multi-year)
				if (target.Contains("new_dealoptiondecision"))
				{
					HandleDealOptionDecisionChange(target, preImage, service, tracing);
				}

				// FLUJO 2: Playoff Option Decision → Close Playoff Deal as Lost
				if (target.Contains("new_playoffoptiondecision"))
				{
					HandlePlayoffOptionDecisionChange(target, preImage, service, tracing);
				}

				// FLUJO 3: Auto-create Playoff Deal (triggered by either status change)
				bool statusChanged = target.Contains("new_dealstatus");
				bool playoffStatusChanged = target.Contains("new_playoffoptionstatus");
				if (statusChanged || playoffStatusChanged)
				{
					EvaluateAndCreatePlayoffDeal(target, preImage, service, tracing);
				}
			}
			catch (Exception ex)
			{
				tracing.Trace($"EXCEPTION: {ex.Message}");
				if (ex is InvalidPluginExecutionException) throw;
				throw new InvalidPluginExecutionException($"DealOptionAutomation error: {ex.Message}");
			}

			tracing.Trace("--- DealOptionAutomation END ---");
		}

		// ==================================================================
		// FLUJO 1: Handle Deal Option Decision change
		// ==================================================================
		private void HandleDealOptionDecisionChange(Entity target, Entity preImage, IOrganizationService service, ITracingService tracing)
		{
			OptionSetValue decision = target.GetAttributeValue<OptionSetValue>("new_dealoptiondecision");
			if (decision == null)
			{
				tracing.Trace("Decision cleared. No action.");
				return;
			}

			int decisionValue = decision.Value;
			tracing.Trace($"Deal Option Decision changed to: {decisionValue}");

			// If decision is NOT Opted-In (client or mutual), close as Lost
			bool isOptedIn = (decisionValue == DECISION_CLIENT_OPTED_IN ||
							 decisionValue == DECISION_MUTUAL_OPTED_IN);

			if (isOptedIn)
			{
				tracing.Trace("Decision is Opted-In. No close action.");
				return;
			}

			// Get current deal's sequence and opportunity for cascade logic
			Entity currentDeal = service.Retrieve("new_deals", target.Id,
				new ColumnSet("new_contractyearsequence", "new_originatingopportunity", "new_dealstatus"));

			// Close THIS deal as Lost
			CloseDealAsLost(target.Id, service, tracing);

			// Cascade: close future years
			int? currentSeq = currentDeal.GetAttributeValue<int?>("new_contractyearsequence");
			EntityReference originatingOpp = currentDeal.GetAttributeValue<EntityReference>("new_originatingopportunity");

			if (currentSeq.HasValue && originatingOpp != null)
			{
				tracing.Trace($"Cascading. Current sequence: {currentSeq}, Opp: {originatingOpp.Id}");
				CascadeCloseFutureYears(originatingOpp.Id, currentSeq.Value, target.Id, service, tracing);
			}
			else
			{
				tracing.Trace("No cascade (single-year deal or missing tracking fields).");
			}
		}

		private void CascadeCloseFutureYears(Guid originatingOppId, int currentSequence, Guid currentDealId, IOrganizationService service, ITracingService tracing)
		{
			QueryExpression q = new QueryExpression("new_deals")
			{
				ColumnSet = new ColumnSet("new_dealsid", "new_contractyearsequence", "statecode")
			};
			q.Criteria.AddCondition("new_originatingopportunity", ConditionOperator.Equal, originatingOppId);
			q.Criteria.AddCondition("new_contractyearsequence", ConditionOperator.GreaterThan, currentSequence);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // Active only
			q.Criteria.AddCondition("new_dealsid", ConditionOperator.NotEqual, currentDealId);

			EntityCollection futureDeals = service.RetrieveMultiple(q);
			tracing.Trace($"Found {futureDeals.Entities.Count} future-year deals to cascade-close.");

			foreach (Entity futureDeal in futureDeals.Entities)
			{
				tracing.Trace($"Cascade-closing deal {futureDeal.Id} as Lost.");
				CloseDealAsLost(futureDeal.Id, service, tracing);
			}
		}

		private void CloseDealAsLost(Guid dealId, IOrganizationService service, ITracingService tracing)
		{
			// Retrieve the "Closed Lost" lookup record
			Guid? closedLostId = GetDealStatusIdByCode(STATUS_CLOSED_LOST, service, tracing);
			if (!closedLostId.HasValue)
			{
				throw new InvalidPluginExecutionException("Could not find Deal Status record with code DS-1009 (Closed Lost).");
			}

			Entity update = new Entity("new_deals", dealId);
			update["new_dealstatus"] = new EntityReference("new_dealstatus", closedLostId.Value);
			service.Update(update);
			tracing.Trace($"Deal {dealId} updated to Closed Lost.");
		}

		private Guid? GetDealStatusIdByCode(string code, IOrganizationService service, ITracingService tracing)
		{
			QueryExpression q = new QueryExpression("new_dealstatus") { ColumnSet = new ColumnSet("new_dealstatusid") };
			q.Criteria.AddCondition("new_code", ConditionOperator.Equal, code);
			q.TopCount = 1;
			Entity result = service.RetrieveMultiple(q).Entities.FirstOrDefault();
			return result?.Id;
		}

		// ==================================================================
		// FLUJO 2: Playoff Option Decision → Close Playoff Deal
		// ==================================================================
		private void HandlePlayoffOptionDecisionChange(Entity target, Entity preImage, IOrganizationService service, ITracingService tracing)
		{
			OptionSetValue decision = target.GetAttributeValue<OptionSetValue>("new_playoffoptiondecision");
			if (decision == null) return;

			// If Client Opted-Out, find the Playoff Deal and close it as Lost
			if (decision.Value != PLAYOFF_DECISION_CLIENT_OPTED_OUT) return;

			tracing.Trace("Playoff Decision = Client Opted-Out. Finding associated Playoff Deal.");

			// Find the Playoff Deal where new_regularseasondeal = target.Id
			QueryExpression q = new QueryExpression("new_deals")
			{
				ColumnSet = new ColumnSet("new_dealsid")
			};
			q.Criteria.AddCondition("new_regularseasondeal", ConditionOperator.Equal, target.Id);
			q.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0);

			Entity playoffDeal = service.RetrieveMultiple(q).Entities.FirstOrDefault();
			if (playoffDeal != null)
			{
				tracing.Trace($"Found Playoff Deal {playoffDeal.Id}. Closing as Lost.");
				CloseDealAsLost(playoffDeal.Id, service, tracing);
			}
			else
			{
				tracing.Trace("No active Playoff Deal found for this regular deal. Nothing to close.");
			}
		}

		// ==================================================================
		// FLUJO 3: Auto-create Playoff Deal
		// ==================================================================
		private void EvaluateAndCreatePlayoffDeal(Entity target, Entity preImage, IOrganizationService service, ITracingService tracing)
		{
			// Retrieve the full current state of the deal
			Entity deal = service.Retrieve("new_deals", target.Id, new ColumnSet(
				"new_dealstatus", "new_playoffoptionstatus", "new_regularseasondeal",
				"new_opportunity", "new_accountid", "new_season",
				"new_salesperson", "new_serviceperson", "new_partnershipassigneeemail",
				"new_dealtype", "new_name"
			));

			// Skip if this is already a Playoff Deal (has regular season parent)
			if (deal.GetAttributeValue<EntityReference>("new_regularseasondeal") != null)
			{
				tracing.Trace("Skipping: this IS a Playoff Deal, not a regular one.");
				return;
			}

			// Check: must be Closed Won
			EntityReference statusRef = deal.GetAttributeValue<EntityReference>("new_dealstatus");
			if (statusRef == null) return;
			Entity statusRecord = service.Retrieve(statusRef.LogicalName, statusRef.Id, new ColumnSet("new_code"));
			string statusCode = statusRecord.GetAttributeValue<string>("new_code");
			if (statusCode != STATUS_CLOSED_WON)
			{
				tracing.Trace($"Skipping: Deal status is {statusCode}, not Closed Won.");
				return;
			}

			// Check: Playoff Option Status must be Opt-In or In
			OptionSetValue playoffStatus = deal.GetAttributeValue<OptionSetValue>("new_playoffoptionstatus");
			if (playoffStatus == null) return;
			bool isPlayoffTrigger = (playoffStatus.Value == PLAYOFF_STATUS_OPT_IN ||
									 playoffStatus.Value == PLAYOFF_STATUS_IN);
			if (!isPlayoffTrigger)
			{
				tracing.Trace($"Skipping: Playoff Option Status is {playoffStatus.Value}, not Opt-In or In.");
				return;
			}

			// Idempotency: does a Playoff Deal already exist for this regular deal?
			if (PlayoffDealExists(target.Id, service, tracing))
			{
				tracing.Trace("Skipping: Playoff Deal already exists.");
				return;
			}

			// All conditions met. Create Playoff Deal.
			CreatePlayoffDeal(deal, service, tracing);
		}

		private bool PlayoffDealExists(Guid regularDealId, IOrganizationService service, ITracingService tracing)
		{
			QueryExpression q = new QueryExpression("new_deals") { ColumnSet = new ColumnSet("new_dealsid") };
			q.Criteria.AddCondition("new_regularseasondeal", ConditionOperator.Equal, regularDealId);
			q.TopCount = 1;
			return service.RetrieveMultiple(q).Entities.Any();
		}

		private void CreatePlayoffDeal(Entity regularDeal, IOrganizationService service, ITracingService tracing)
		{
			tracing.Trace("Creating Playoff Deal...");

			Entity playoff = new Entity("new_deals");

			// Link to regular deal (this is what identifies it as a Playoff Deal)
			playoff["new_regularseasondeal"] = new EntityReference("new_deals", regularDeal.Id);

			// Copy core fields from regular deal
			CopyIfExists(regularDeal, playoff, "new_opportunity");
			CopyIfExists(regularDeal, playoff, "new_accountid");
			CopyIfExists(regularDeal, playoff, "new_season");
			CopyIfExists(regularDeal, playoff, "new_salesperson");
			CopyIfExists(regularDeal, playoff, "new_serviceperson");
			CopyIfExists(regularDeal, playoff, "new_partnershipassigneeemail");
			CopyIfExists(regularDeal, playoff, "new_dealtype");

			// Build Playoff Deal name: "[Regular Deal Name] - Playoffs"
			string regName = regularDeal.GetAttributeValue<string>("new_name") ?? "Deal";
			playoff["new_name"] = $"{regName} - Playoffs";

			// Set initial Deal Status to '1 - Prospect IdentifiedOpen' (first stage of new_dealstatus)
			Guid? openStatusId = GetDealStatusIdByCode("DS-1001", service, tracing); //DS-1001 = 1 - Prospect Identified.
			if (openStatusId.HasValue)
			{
				playoff["new_dealstatus"] = new EntityReference("new_dealstatus", openStatusId.Value);
			}

			Guid playoffId = service.Create(playoff);
			tracing.Trace($"Playoff Deal created: {playoffId}");
		}

		private void CopyIfExists(Entity source, Entity target, string attr)
		{
			if (source.Contains(attr) && source[attr] != null)
			{
				target[attr] = source[attr];
			}
		}
	}
}
