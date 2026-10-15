using System;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace Pl.Inventory.RenameDealLines
{
    /// <summary>
    /// Keeps active deal line names in sync when an inventory item is renamed.
    ///
    /// Registration
    ///   Message:            Update
    ///   Primary entity:     new_inventory
    ///   Filtering attrs:    new_name
    ///   Stage:              PostOperation (40), Synchronous
    ///   Pre-image:          alias "PreImage", attribute new_name
    ///
    /// Rules (requested by Ray, 2026-10-14)
    ///   * Going forward only: runs when an inventory name changes. Existing names are not backfilled.
    ///   * Only ACTIVE deal lines are touched.
    ///   * The rest of the line name is preserved, including the package name. Only the old
    ///     inventory name is swapped, and only where the Deal Line Builder puts it:
    ///       "[Deal] - [Inventory]"      (item added on its own)       -> suffix
    ///       "[Inventory] - [Package]"   (item added from a package)   -> prefix
    ///       "[Inventory]"               (exact match)
    ///   * When the renamed inventory is itself a PACKAGE, lines linked to it through the
    ///     Package lookup get the package part of their name updated:
    ///       "[Item] - [Package]"                                        -> suffix
    ///   * Lines renamed by hand (old name not found in those positions) are skipped on purpose.
    /// </summary>
    public class RenameDealLines : IPlugin
    {
        private const string PreImageAlias = "PreImage";
        private const string Separator = " - ";

        public void Execute(IServiceProvider serviceProvider)
        {
            var context = (IPluginExecutionContext)serviceProvider.GetService(typeof(IPluginExecutionContext));
            var tracing = (ITracingService)serviceProvider.GetService(typeof(ITracingService));
            var factory = (IOrganizationServiceFactory)serviceProvider.GetService(typeof(IOrganizationServiceFactory));
            var service = factory.CreateOrganizationService(context.UserId);

            // Guard against recursion and unexpected registrations
            if (context.Depth > 2) { tracing.Trace("ABORT: depth {0}.", context.Depth); return; }
            if (!string.Equals(context.MessageName, "Update", StringComparison.OrdinalIgnoreCase)) return;
            if (!string.Equals(context.PrimaryEntityName, "new_inventory", StringComparison.OrdinalIgnoreCase)) return;

            var target = context.InputParameters.Contains("Target") ? context.InputParameters["Target"] as Entity : null;
            if (target == null || !target.Contains("new_name")) return;

            var preImage = context.PreEntityImages.Contains(PreImageAlias) ? context.PreEntityImages[PreImageAlias] : null;
            string oldName = preImage?.GetAttributeValue<string>("new_name")?.Trim();
            string newName = target.GetAttributeValue<string>("new_name")?.Trim();

            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName))
            {
                tracing.Trace("ABORT: old or new name is empty (old='{0}', new='{1}').", oldName, newName);
                return;
            }
            if (string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                tracing.Trace("ABORT: name did not change.");
                return;
            }

            tracing.Trace("Inventory {0} renamed: '{1}' -> '{2}'.", target.Id, oldName, newName);

            // 1) Lines that USE this inventory item
            int updatedItem = RenameLines(service, tracing, "new_inventory", target.Id, oldName, newName, matchPrefix: true);

            // 2) Lines that came FROM this inventory when it is a package
            int updatedPackage = RenameLines(service, tracing, "new_package", target.Id, oldName, newName, matchPrefix: false);

            tracing.Trace("Done. Lines renamed: {0} by item, {1} by package.", updatedItem, updatedPackage);
        }

        /// <summary>
        /// Renames the active deal lines whose lookup <paramref name="lookupField"/> points to the inventory.
        /// The old name is replaced as a suffix ("... - old"), as a prefix ("old - ...") when allowed,
        /// or when the whole name equals it. Anything else is left untouched.
        /// </summary>
        private static int RenameLines(IOrganizationService service, ITracingService tracing,
            string lookupField, Guid inventoryId, string oldName, string newName, bool matchPrefix)
        {
            var query = new QueryExpression("new_deallines")
            {
                ColumnSet = new ColumnSet("new_name"),
                Criteria = new FilterExpression(LogicalOperator.And),
                PageInfo = new PagingInfo { Count = 5000, PageNumber = 1 }
            };
            query.Criteria.AddCondition(lookupField, ConditionOperator.Equal, inventoryId);
            query.Criteria.AddCondition("statecode", ConditionOperator.Equal, 0); // active only

            int updated = 0;
            while (true)
            {
                EntityCollection page = service.RetrieveMultiple(query);
                foreach (Entity line in page.Entities)
                {
                    string current = line.GetAttributeValue<string>("new_name") ?? string.Empty;
                    string renamed = BuildNewName(current, oldName, newName, matchPrefix);

                    if (renamed == null)
                    {
                        tracing.Trace("SKIP {0}: '{1}' does not contain the old name in a known position.", line.Id, current);
                        continue;
                    }
                    if (string.Equals(renamed, current, StringComparison.Ordinal)) continue;

                    // Only new_name is sent, so no deal line plugin is triggered (none filters on new_name)
                    var update = new Entity("new_deallines", line.Id);
                    update["new_name"] = renamed;
                    service.Update(update);
                    updated++;
                    tracing.Trace("RENAMED {0}: '{1}' -> '{2}'.", line.Id, current, renamed);
                }

                if (!page.MoreRecords) break;
                query.PageInfo.PageNumber++;
                query.PageInfo.PagingCookie = page.PagingCookie;
            }
            return updated;
        }

        /// <summary>
        /// Returns the new line name, or null when the old name is not in a position the builder uses.
        /// Comparison is case-insensitive; the rest of the name keeps its original text.
        /// </summary>
        internal static string BuildNewName(string current, string oldName, string newName, bool matchPrefix)
        {
            if (string.IsNullOrWhiteSpace(current)) return null;
            string trimmed = current.Trim();

            // Whole name equals the old name
            if (string.Equals(trimmed, oldName, StringComparison.OrdinalIgnoreCase))
                return newName;

            // "... - old"  ->  "... - new"   ([Deal] - [Inventory]  or  [Item] - [Package])
            string suffix = Separator + oldName;
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return trimmed.Substring(0, trimmed.Length - suffix.Length) + Separator + newName;

            // "old - ..."  ->  "new - ..."   ([Inventory] - [Package])
            if (matchPrefix)
            {
                string prefix = oldName + Separator;
                if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return newName + Separator + trimmed.Substring(prefix.Length);
            }

            return null;
        }
    }
}
