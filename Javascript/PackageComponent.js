/**
 * PackageComponent.js
 * Web resource for the Package Component form (new_packagecomponent).
 *
 * Purpose:
 *   1. Restrict the "Component Product" lookup (new_componentproduct) so it
 *      cannot select a product that is itself a package (new_ispackage = Yes).
 *      This enforces the "no packages within packages" (flatten) decision.
 *   2. On Create, clear the auto-filled Component Product. When a child table
 *      has two lookups to the same table (Package Product + Component Product,
 *      both -> Product), the Quick Create launched from the subgrid inherits the
 *      parent record into BOTH lookups. We clear Component Product (only when it
 *      equals Package Product) so the user picks the real component.
 *
 * Form registration (register on BOTH the main and the Quick Create forms):
 *   - Form OnLoad -> PackageComponentForm.onLoad (pass execution context)
 */
var PackageComponentForm = PackageComponentForm || {};

PackageComponentForm.onLoad = function (executionContext) {
    var formContext = executionContext.getFormContext();

    // 1. Exclude packages from the Component Product lookup
    var ctrl = formContext.getControl("new_componentproduct");
    if (ctrl && typeof ctrl.addPreSearch === "function") {
        ctrl.addPreSearch(function () {
            var filter =
                "<filter type='or'>" +
                "  <condition attribute='new_ispackage' operator='eq' value='0' />" +
                "  <condition attribute='new_ispackage' operator='null' />" +
                "</filter>";
            ctrl.addCustomFilter(filter, "new_product");
        });
    }

    // 2 & 3. Create-only tweaks
    if (formContext.ui.getFormType() === 1) {
        // 2. Clear Component Product if it was auto-filled = Package Product
        var comp = formContext.getAttribute("new_componentproduct");
        var pkg = formContext.getAttribute("new_packageproduct");
        if (comp && pkg) {
            var compVal = comp.getValue();
            var pkgVal = pkg.getValue();
            if (compVal && pkgVal && compVal[0].id === pkgVal[0].id) {
                comp.setValue(null);
            }
        }

        // 3. Default Quantity to 1 when blank
        var qty = formContext.getAttribute("new_quantity");
        if (qty && (qty.getValue() === null || qty.getValue() === undefined)) {
            qty.setValue(1);
        }
    }
};
