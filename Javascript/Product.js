/**
 * Product.js
 * Web resource for the Product form (Corporate Partnerships).
 *
 * Purpose:
 *   Show the "Package Components" section only when the product is flagged
 *   as a package (new_ispackage = Yes). This is where the package "recipe"
 *   (its component products) is configured via the editable subgrid.
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

ProductForm.onLoad = function (executionContext) {
    ProductForm.togglePackageComponents(executionContext);
};

ProductForm.onIsPackageChange = function (executionContext) {
    ProductForm.togglePackageComponents(executionContext);
};

/**
 * Shows/hides the Package Components section based on new_ispackage.
 */
ProductForm.togglePackageComponents = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var isPkgAttr = formContext.getAttribute("new_ispackage");
    if (!isPkgAttr) {
        return;
    }
    var isPackage = isPkgAttr.getValue() === true;

    var tab = formContext.ui.tabs.get(ProductForm.PKG_TAB);
    if (!tab) {
        return;
    }
    var section = tab.sections.get(ProductForm.PKG_SECTION);
    if (section) {
        section.setVisible(isPackage);
    }
};
