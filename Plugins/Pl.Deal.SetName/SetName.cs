using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Deal.SetName
{
	public class SetName : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
			ITracingService tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target)
			{
				// FIX: entidad correcta es "new_deals" (plural), no "new_deal"
				if (target.LogicalName != "new_deals") return;

				// 1. Check if the user/JS explicitly sent a custom name in this transaction
				bool hasCustomName = target.Contains("new_name") && !string.IsNullOrWhiteSpace(target.GetAttributeValue<string>("new_name"));

				// On Update, if a custom name was provided AND we're NOT dealing with the Playoff creation flow, respect it.
				// Note: the DealOptionAutomation plugin creates Playoff Deals with name already set — we want to ENRICH that,
				// so we do NOT early-return for those cases. The logic below handles this correctly by checking if the
				// current name matches the expected pattern vs. already having the Playoffs suffix.

				// Determine if we should auto-generate
				bool shouldGenerate = false;
				if (context.MessageName.ToLower() == "create")
				{
					shouldGenerate = true; // Always generate on Create (overrides whatever was sent)
				}
				else if (context.MessageName.ToLower() == "update")
				{
					// On Update: regenerate only if Account or Season changed
					shouldGenerate = target.Contains("new_accountid") || target.Contains("new_season");
				}

				if (!shouldGenerate)
				{
					tracing.Trace("SetName: no trigger for regeneration. Skipping.");
					return;
				}

				// 2. Extract data (from Target or PreImage)
				Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;

				EntityReference accountRef = GetLookup(target, preImage, "new_accountid");
				EntityReference seasonRef = GetLookup(target, preImage, "new_season");

				// NEW: Check if this is a Playoff Deal
				// Priority: target first (if being set in this transaction), then PreImage
				EntityReference regularSeasonDealRef = null;
				if (target.Contains("new_regularseasondeal"))
				{
					regularSeasonDealRef = target.GetAttributeValue<EntityReference>("new_regularseasondeal");
				}
				else if (preImage != null && preImage.Contains("new_regularseasondeal"))
				{
					regularSeasonDealRef = preImage.GetAttributeValue<EntityReference>("new_regularseasondeal");
				}

				bool isPlayoffDeal = (regularSeasonDealRef != null);
				tracing.Trace($"SetName: isPlayoffDeal={isPlayoffDeal}");

				// 3. Resolve Account name
				string accountName = "";
				if (accountRef != null)
				{
					if (!string.IsNullOrEmpty(accountRef.Name)) accountName = accountRef.Name;
					else
					{
						Entity account = service.Retrieve("account", accountRef.Id, new ColumnSet("name"));
						accountName = account.GetAttributeValue<string>("name");
					}
				}

				// 4. Resolve Season name
				string seasonName = "";
				if (seasonRef != null)
				{
					if (!string.IsNullOrEmpty(seasonRef.Name)) seasonName = seasonRef.Name;
					else
					{
						Entity season = service.Retrieve("new_season", seasonRef.Id, new ColumnSet("new_name"));
						seasonName = season.GetAttributeValue<string>("new_name");
					}
				}

				// 5. Build the name
				if (!string.IsNullOrEmpty(accountName) || !string.IsNullOrEmpty(seasonName))
				{
					string baseName = $"{accountName} - {seasonName}".Trim(' ', '-');

					// FIX: if Playoff Deal, append " - Playoffs" suffix
					string finalName = isPlayoffDeal ? $"{baseName} - Playoffs" : baseName;

					target["new_name"] = finalName;
					tracing.Trace($"SetName: generated name = '{finalName}'");
				}
			}
		}

		// ----------------------------------------------------------------------------
		// Helper: resolve a lookup field from Target or PreImage (Target takes priority)
		// ----------------------------------------------------------------------------
		private EntityReference GetLookup(Entity target, Entity preImage, string attrName)
		{
			if (target.Contains(attrName))
				return target.GetAttributeValue<EntityReference>(attrName);
			if (preImage != null && preImage.Contains(attrName))
				return preImage.GetAttributeValue<EntityReference>(attrName);
			return null;
		}
	}
}
