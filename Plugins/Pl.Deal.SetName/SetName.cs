using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Deal.SetName
{
	public class SetName : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			// Obtain the core Dynamics 365 services
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

			// Validate that we are working with the new_deal entity and that a Target exists
			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target)
			{
				if (target.LogicalName != "new_deal") return;

				// 1. Check if the user/JS explicitly sent a custom name in this transaction
				bool hasCustomName = target.Contains("new_name") && !string.IsNullOrWhiteSpace(target.GetAttributeValue<string>("new_name"));

				// If it's an Update message and a custom name was provided, respect it (Do nothing)
				if (context.MessageName.ToLower() == "update" && hasCustomName)
				{
					return;
				}

				// Logic to determine if we MUST auto-generate the name
				bool shouldGenerate = false;

				if (context.MessageName.ToLower() == "create")
				{
					// On Create, generate if the name is empty
					shouldGenerate = !hasCustomName;
				}
				else if (context.MessageName.ToLower() == "update")
				{
					// On Update, generate ONLY if Account or Season changed, and NO new name was provided manually
					shouldGenerate = (target.Contains("new_accountid") || target.Contains("new_season")) && !hasCustomName;
				}

				if (!shouldGenerate) return;

				// 2. Extract necessary data (From Target, or from the Pre-Image if not included in the update payload)
				Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;

				EntityReference accountRef = target.Contains("new_accountid") ? target.GetAttributeValue<EntityReference>("new_accountid") :
											 (preImage != null && preImage.Contains("new_accountid") ? preImage.GetAttributeValue<EntityReference>("new_accountid") : null);

				EntityReference seasonRef = target.Contains("new_season") ? target.GetAttributeValue<EntityReference>("new_season") :
											(preImage != null && preImage.Contains("new_season") ? preImage.GetAttributeValue<EntityReference>("new_season") : null);

				string accountName = "";
				if (accountRef != null)
				{
					// Attempt to get the name from the reference; otherwise, fetch it from the database
					if (!string.IsNullOrEmpty(accountRef.Name)) accountName = accountRef.Name;
					else
					{
						Entity account = service.Retrieve("account", accountRef.Id, new ColumnSet("name"));
						accountName = account.GetAttributeValue<string>("name");
					}
				}

				string seasonName = "";
				if (seasonRef != null)
				{
					// Assuming the season entity is custom, we look for its Name property (new_name)
					if (!string.IsNullOrEmpty(seasonRef.Name)) seasonName = seasonRef.Name;
					else
					{
						// IMPORTANT: Ensure "new_season" and "new_name" match your actual Seasons table configuration
						Entity season = service.Retrieve("new_season", seasonRef.Id, new ColumnSet("new_name"));
						seasonName = season.GetAttributeValue<string>("new_name");
					}
				}

				// 3. Build and Inject the name into the Target
				if (!string.IsNullOrEmpty(accountName) || !string.IsNullOrEmpty(seasonName))
				{
					// Concatenate and remove any extra hyphens or spaces if any data is missing
					string newDealName = $"{accountName} - {seasonName}".Trim(' ', '-');
					target["new_name"] = newDealName;
				}
			}
		}
	}
}