using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
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

					// Mirror 'Is Package' and the descriptions from the related Product.
					// Keeps inventory in sync with the product: the description is the same across all
					// seasons (no per-season override), so we always pull from the product.
					//   External description: product.new_descriptionorspecifications -> inventory.new_description  (syncs to Trak "specifications")
					//   Internal description: product.new_internaldescription        -> inventory.new_internaldescription (Dynamics-only)
					EntityReference productRef = null;
					if (inventory.Contains("new_productid") && inventory["new_productid"] != null)
						productRef = inventory.GetAttributeValue<EntityReference>("new_productid");
					else if (context.PreEntityImages.Contains("PreImage") && context.PreEntityImages["PreImage"].Contains("new_productid"))
						productRef = context.PreEntityImages["PreImage"].GetAttributeValue<EntityReference>("new_productid");

					if (productRef != null)
					{
						Entity product = service.Retrieve("new_product", productRef.Id,
							new ColumnSet("new_ispackage", "new_descriptionorspecifications", "new_internaldescription"));

						inventory["new_ispackage"] = product.GetAttributeValue<bool>("new_ispackage");

						// Mirror both descriptions one-to-one from the product.
						inventory["new_description"] = product.GetAttributeValue<string>("new_descriptionorspecifications");
						inventory["new_internaldescription"] = product.GetAttributeValue<string>("new_internaldescription");

						tracingService.Trace("Mirrored from product -> IsPackage: {0}, ExternalDesc set: {1}, InternalDesc set: {2}",
							inventory["new_ispackage"],
							inventory["new_description"] != null,
							inventory["new_internaldescription"] != null);
					}
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
