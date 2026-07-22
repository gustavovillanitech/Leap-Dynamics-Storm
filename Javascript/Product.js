/**
 * Product.js
 * Web resource for the Product form (Corporate Partnerships).
 *
 * Purpose:
 *   1. Show the "Package Components" section only when the product is flagged
 *      as a package (new_ispackage = Yes). This is where the package "recipe"
 *      (its component products) is configured via the editable subgrid.
 *   2. When the product is a package, hide the single-product / inventory
 *      attribute fields that do not apply to a bundle, so the form is cleaner.
 *
 * Form registration:
 *   - Form OnLoad                  -> ProductForm.onLoad            (pass execution context)
 *   - Field new_ispackage OnChange -> ProductForm.onIsPackageChange (pass execution context)
 *
 * NOTE: adjust PKG_TAB / PKG_SECTION to the real technical names of the tab
 * and section that contain the Package Components subgrid.
 */
var ProductForm = ProductForm || {};

// Technical names of the tab and section that hold the Package Components subgrid.
ProductForm.PKG_TAB = "tab_general";
ProductForm.PKG_SECTION = "section_packagecomponents";

// Fields that are hidden when the product IS a package (they only make sense
// for a single inventory product, not for a bundle).
ProductForm.PACKAGE_HIDDEN_FIELDS = [
    "new_trakproductid",        // Trak has no package endpoint
    "new_division",
    "new_collection",
    "new_rate",                 // package price lives on the inventory Rate Card
    "new_expense",
    "new_quantityavailable",
    "new_category",
    "new_rulepreventoverselling" // overselling is controlled at the component level
];

ProductForm.onLoad = function (executionContext) {
    ProductForm.applyPackageLayout(executionContext);
};

ProductForm.onIsPackageChange = function (executionContext) {
    ProductForm.applyPackageLayout(executionContext);
};

/**
 * Applies the package-specific layout: toggles the Package Components section
 * and hides/shows the single-product fields based on new_ispackage.
 */
ProductForm.applyPackageLayout = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var isPkgAttr = formContext.getAttribute("new_ispackage");
    if (!isPkgAttr) {
        return;
    }
    var isPackage = isPkgAttr.getValue() === true;

    ProductForm.togglePackageComponents(formContext, isPackage);
    ProductForm.togglePackageFields(formContext, isPackage);
};

/**
 * Shows/hides the Package Components section based on isPackage.
 */
ProductForm.togglePackageComponents = function (formContext, isPackage) {
    var tab = formContext.ui.tabs.get(ProductForm.PKG_TAB);
    if (!tab) {
        return;
    }
    var section = tab.sections.get(ProductForm.PKG_SECTION);
    if (section) {
        section.setVisible(isPackage);
    }
};

/**
 * Hides the single-product fields when isPackage is true; shows them otherwise.
 * Iterates every control bound to each attribute so it works even if a field
 * is placed on the form more than once.
 */
ProductForm.togglePackageFields = function (formContext, isPackage) {
    ProductForm.PACKAGE_HIDDEN_FIELDS.forEach(function (fieldName) {
        var attr = formContext.getAttribute(fieldName);
        if (!attr) {
            return;
        }
        attr.controls.forEach(function (ctrl) {
            ctrl.setVisible(!isPackage);
        });
    });
};
