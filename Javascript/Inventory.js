/**
 * Inventory.js
 * Web resource for the Inventory form (new_inventory) — Corporate Partnerships.
 *
 * Purpose:
 *   When the inventory row belongs to a PACKAGE product, hide the fields that
 *   only make sense for a single sellable item and would confuse the user on a
 *   package (quantity, total, allocation buckets, etc.). For a package, the only
 *   relevant business field is the price (Rate Card) plus its context
 *   (Product, Season, Name, Description).
 *
 * How "is package" is determined:
 *   - new_ispackage is a mirror field on inventory, populated by the
 *     Pl.Inventory.TotalCalculatedField plugin on save (copied from the product).
 *   - On load we read that mirror. If it is empty (e.g. a brand-new record not
 *     yet saved), we fall back to reading new_ispackage from the selected product.
 *   - On Product Id change we read the product's new_ispackage via Web API so the
 *     form reacts immediately, before the record is saved.
 *
 * Form registration:
 *   - Form OnLoad                 -> InventoryForm.onLoad          (pass execution context)
 *   - Field new_productid OnChange -> InventoryForm.onProductChange (pass execution context)
 */
var InventoryForm = InventoryForm || {};

// Fields hidden when the inventory row is a package (not relevant / misleading).
InventoryForm.PACKAGE_HIDDEN_FIELDS = [
    "new_trakinventoryid",
    "new_division",
    "new_collection",
    "new_assigneduser",
    "new_duedate",
    "new_expense",
    "new_quantity",   // Quantity Available — tracked per component, not on the package
    "new_total",      // rate x quantity -> meaningless for a package
    "new_sold",
    "new_pitched",
    "new_allocated",
    "new_unsold"
];

InventoryForm.onLoad = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var isPkgAttr = formContext.getAttribute("new_ispackage");
    var mirror = isPkgAttr ? isPkgAttr.getValue() : null;

    if (mirror === true || mirror === false) {
        // Mirror already set (saved record) -> use it directly.
        InventoryForm.applyPackageLayout(formContext, mirror === true);
    } else {
        // Mirror not set yet -> derive from the selected product, if any.
        InventoryForm.resolveFromProduct(formContext);
    }
};

InventoryForm.onProductChange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    InventoryForm.resolveFromProduct(formContext);
};

/**
 * Reads new_ispackage from the currently selected product (new_productid) via
 * Web API and applies the layout. If no product is selected, treats as non-package.
 */
InventoryForm.resolveFromProduct = function (formContext) {
    var prodAttr = formContext.getAttribute("new_productid");
    var prod = prodAttr ? prodAttr.getValue() : null;

    if (!prod || prod.length === 0) {
        InventoryForm.applyPackageLayout(formContext, false);
        return;
    }

    var productId = prod[0].id.replace(/[{}]/g, "");
    Xrm.WebApi.retrieveRecord("new_product", productId, "?$select=new_ispackage").then(
        function (result) {
            InventoryForm.applyPackageLayout(formContext, result.new_ispackage === true);
        },
        function (error) {
            console.error("Inventory.js - could not read new_ispackage from product: " + error.message);
            // On error, do not hide anything (fail open).
            InventoryForm.applyPackageLayout(formContext, false);
        }
    );
};

/**
 * Hides the package-irrelevant fields when isPackage is true; shows them otherwise.
 * Iterates every control bound to each attribute (handles fields placed twice).
 */
InventoryForm.applyPackageLayout = function (formContext, isPackage) {
    InventoryForm.PACKAGE_HIDDEN_FIELDS.forEach(function (fieldName) {
        var attr = formContext.getAttribute(fieldName);
        if (!attr) {
            return;
        }
        attr.controls.forEach(function (ctrl) {
            ctrl.setVisible(!isPackage);
        });
    });
};
