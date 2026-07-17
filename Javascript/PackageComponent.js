/**
 * PackageComponent.js
 * Web resource for the Package Component form (new_packagecomponent).
 *
 * Purpose:
 *   Restrict the "Component Product" lookup (new_componentproduct) so it
 *   cannot select a product that is itself a package (new_ispackage = Yes).
 *   This enforces the "no packages within packages" (flatten) decision.
 *
 * Form registration:
 *   - Form OnLoad -> PackageComponentForm.onLoad (pass execution context)
 *
 * CAVEAT: addCustomFilter applies when the component is added/edited from the
 * Package Component form (the subgrid "+ New" opens the quick-create/main form).
 * Inline lookups in an Editable Grid may NOT honor this JS filter; for strict
 * enforcement there, set a filtered default view (new_ispackage = No) on the
 * Component Product lookup column instead.
 */
var PackageComponentForm = PackageComponentForm || {};

PackageComponentForm.onLoad = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var ctrl = formContext.getControl("new_componentproduct");
    if (!ctrl || typeof ctrl.addPreSearch !== "function") {
        return;
    }

    ctrl.addPreSearch(function () {
        // Exclude products flagged as package (and include those with no flag set).
        var filter =
            "<filter type='or'>" +
            "  <condition attribute='new_ispackage' operator='eq' value='0' />" +
            "  <condition attribute='new_ispackage' operator='null' />" +
            "</filter>";
        ctrl.addCustomFilter(filter, "new_product");
    });
};
