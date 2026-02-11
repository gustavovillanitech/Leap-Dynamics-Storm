using Microsoft.Xrm.Sdk;
using System;

namespace Pl.Inventory.TotalCalculatedField
{
	public class TotalCalculatedField : IPlugin
	{
		public void Execute(IServiceProvider serviceProvider)
		{
			ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
			IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
			IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
			IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);

			tracingService.Trace("Pl.Inventory.TotalCalculatedField Plugin started.");

			// Check if the input parameters contain the 'Target' entity
			if (context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity)
			{
				Entity inventory = (Entity)context.InputParameters["Target"];

				// Ensure we are working with the correct entity: new_inventory
				if (inventory.LogicalName != "new_inventory")
				{
					tracingService.Trace("Wrong entity: {0}. Exiting.", inventory.LogicalName);
					return;
				}

				try
				{
					decimal rate = 0;
					decimal quantity = 0;

					// 1. Retrieve the Rate value (Money type)
					// Check Target first, then PreImage if Target doesn't contain the field (common in Updates)
					if (inventory.Contains("new_rate") && inventory["new_rate"] != null)
					{
						rate = ((Money)inventory["new_rate"]).Value;
					}
					else if (context.PreEntityImages.Contains("PreImage") && context.PreEntityImages["PreImage"].Contains("new_rate"))
					{
						rate = ((Money)context.PreEntityImages["PreImage"]["new_rate"]).Value;
					}

					// 2. Retrieve the Quantity value (Decimal type)
					if (inventory.Contains("new_quantity") && inventory["new_quantity"] != null)
					{
						quantity = (decimal)inventory["new_quantity"];
					}
					else if (context.PreEntityImages.Contains("PreImage") && context.PreEntityImages["PreImage"].Contains("new_quantity"))
					{
						quantity = (decimal)context.PreEntityImages["PreImage"]["new_quantity"];
					}

					tracingService.Trace("Input values retrieved - Rate: {0}, Quantity: {1}", rate, quantity);

					// 3. Perform calculation and set the Total field (Money type)
					decimal totalCalculation = rate * quantity;
					inventory["new_total"] = new Money(totalCalculation);

					tracingService.Trace("Total calculation successful: {0}", totalCalculation);
				}
				catch (Exception ex)
				{
					tracingService.Trace("An error occurred in TotalCalculatedField: {0}", ex.ToString());
					throw new InvalidPluginExecutionException("An error occurred while calculating the Inventory Total.", ex);
				}
			}
		}
	}
}