using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using System;

namespace Pl.PackageComponent.SetName
{
    /// <summary>
    /// Auto-generates the primary name of a Package Component record as
    /// "[Package Product] - [Component Product]".
    ///
    /// Registration (Plugin Registration Tool):
    ///   - Message:            Create   AND   Update
    ///   - Primary Entity:     new_packagecomponent
    ///   - Stage:              Pre-Operation (20)
    ///   - Mode:               Synchronous
    ///   - Filtering attrs (Update step): new_packageproduct, new_componentproduct
    ///   - Pre-Image "PreImage" (Update step): new_packageproduct, new_componentproduct, new_name
    ///
    /// Runs server-side so the name is set consistently whether the row is
    /// created from the editable subgrid, the quick-create form, the API, or a
    /// data import.
    /// </summary>
    public class SetName : IPlugin
    {
        public void Execute(IServiceProvider serviceProvider)
        {
            // Core Dynamics 365 services
            IPluginExecutionContext context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            IOrganizationServiceFactory serviceFactory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            IOrganizationService service = serviceFactory.CreateOrganizationService(context.UserId);
            ITracingService tracingService = (ITracingService)serviceProvider.GetService(typeof(ITracingService));

            if (!(context.InputParameters.Contains("Target") && context.InputParameters["Target"] is Entity target))
                return;

            if (target.LogicalName != "new_packagecomponent")
                return;

            // 1. Respect an explicit name provided in this transaction
            bool hasCustomName = target.Contains("new_name") && !string.IsNullOrWhiteSpace(target.GetAttributeValue<string>("new_name"));

            if (context.MessageName.ToLower() == "update" && hasCustomName)
                return;

            // 2. Decide whether we must auto-generate
            bool shouldGenerate = false;

            if (context.MessageName.ToLower() == "create")
            {
                shouldGenerate = !hasCustomName;
            }
            else if (context.MessageName.ToLower() == "update")
            {
                // Regenerate only if one of the two lookups changed and no manual name was provided
                shouldGenerate = (target.Contains("new_packageproduct") || target.Contains("new_componentproduct")) && !hasCustomName;
            }

            if (!shouldGenerate)
                return;

            // 3. Resolve the two product references (from Target, else from the Pre-Image)
            Entity preImage = context.PreEntityImages.Contains("PreImage") ? context.PreEntityImages["PreImage"] : null;

            EntityReference packageRef = target.Contains("new_packageproduct")
                ? target.GetAttributeValue<EntityReference>("new_packageproduct")
                : (preImage != null && preImage.Contains("new_packageproduct") ? preImage.GetAttributeValue<EntityReference>("new_packageproduct") : null);

            EntityReference componentRef = target.Contains("new_componentproduct")
                ? target.GetAttributeValue<EntityReference>("new_componentproduct")
                : (preImage != null && preImage.Contains("new_componentproduct") ? preImage.GetAttributeValue<EntityReference>("new_componentproduct") : null);

            string packageName = GetProductName(packageRef, service);
            string componentName = GetProductName(componentRef, service);

            // 4. Build and inject the name into the Target
            if (!string.IsNullOrEmpty(packageName) || !string.IsNullOrEmpty(componentName))
            {
                string newName = $"{packageName} - {componentName}".Trim(' ', '-');
                target["new_name"] = newName;
                tracingService.Trace($"SetName (PackageComponent): generated name = '{newName}'");
            }
        }

        /// <summary>
        /// Returns the product's primary name. Uses the lookup's cached Name when
        /// available; otherwise retrieves it.
        /// NOTE: confirm new_product's primary name attribute is "new_name"; adjust if different.
        /// </summary>
        private string GetProductName(EntityReference productRef, IOrganizationService service)
        {
            if (productRef == null)
                return "";

            if (!string.IsNullOrEmpty(productRef.Name))
                return productRef.Name;

            Entity product = service.Retrieve("new_product", productRef.Id, new ColumnSet("new_name"));
            return product.GetAttributeValue<string>("new_name");
        }
    }
}
