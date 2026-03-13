using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;
using System.Security.Principal;

namespace Pl.Opportunity.SetCPName
{
	public class SetCPName : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			// Obtain the core Dynamics 365 services
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

			// Validate that we are working with the Opportunity entity and that a Target exists
			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target)
			{
				if (target.LogicalName != "opportunity") return;

				// 1. Check if the user/JS explicitly sent a custom name in this transaction
				bool hasCustomName = target.Contains("name") && !string.IsNullOrWhiteSpace(target.GetAttributeValue<string>("name"));

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
					// On Update, generate ONLY if Account or Season changed, and NO new Topic was provided manually
					shouldGenerate = (target.Contains("parentaccountid") || target.Contains("new_basketballseason")) && !hasCustomName;
				}

				if (!shouldGenerate) return;

				// 2. Extract necessary data (From Target, or from the Pre-Image if not included in the update payload)
				Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;

				EntityReference accountRef = target.Contains("parentaccountid") ? target.GetAttributeValue<EntityReference>("parentaccountid") :
											 (preImage != null && preImage.Contains("parentaccountid") ? preImage.GetAttributeValue<EntityReference>("parentaccountid") : null);

				EntityReference seasonRef = target.Contains("new_basketballseason") ? target.GetAttributeValue<EntityReference>("new_basketballseason") :
											(preImage != null && preImage.Contains("new_basketballseason") ? preImage.GetAttributeValue<EntityReference>("new_basketballseason") : null);

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
					// Note: Assuming the season entity is custom, we will look for its Name property
					if (!string.IsNullOrEmpty(seasonRef.Name)) seasonName = seasonRef.Name;
					else
					{
						// IMPORTANT: Make sure to change "new_season" and "new_name" to the actual logical names of your Seasons table
						Entity season = service.Retrieve("new_season", seasonRef.Id, new ColumnSet("new_name"));
						seasonName = season.GetAttributeValue<string>("new_name");
					}
				}

				// 3. Build and Inject the name into the Target
				if (!string.IsNullOrEmpty(accountName) || !string.IsNullOrEmpty(seasonName))
				{
					// Concatenate and remove any extra hyphens or spaces if any data is missing
					string newName = $"{accountName} - {seasonName}".Trim(' ', '-');
					target["name"] = newName;
				}
			}
		}
	}
}