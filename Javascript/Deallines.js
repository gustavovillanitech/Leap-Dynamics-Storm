var DealLineForm = DealLineForm || {};

// --- Event Handlers ---

/**
 * Triggered on the 'new_inventory' field OnChange event.
 * @param {object} executionContext 
 */
DealLineForm.onInventoryChange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var inventoryAttr = formContext.getAttribute("new_inventory");
    var productAttr = formContext.getAttribute("new_productid");

    if (!inventoryAttr || !productAttr) return;

    var inventoryValue = inventoryAttr.getValue();

    // If inventory is cleared, clear product
    if (inventoryValue === null) {
        productAttr.setValue(null);
        return;
    }

    var inventoryId = inventoryValue[0].id.replace("{", "").replace("}", "");

    formContext.ui.setFormNotification("Fetching associated product...", "INFO", "inventory_fetch");

    /**
     * Query the Inventory table to retrieve the associated Product lookup.
     */
    Xrm.WebApi.retrieveRecord("new_inventory", inventoryId, "?$select=_new_productid_value").then(
        function success(result) {
            formContext.ui.clearFormNotification("inventory_fetch");

            // Lookups return the ID in '_field_value' and the Name in the FormattedValue property
            var productId = result["_new_productid_value"];
            var productName = result["_new_productid_value@OData.Community.Display.V1.FormattedValue"];
            var productLogicalName = result["_new_productid_value@Microsoft.Dynamics.CRM.lookuplogicalname"];

            if (productId && productName) {
                // Set the value for the Product lookup field
                productAttr.setValue([{
                    id: productId,
                    name: productName,
                    entityType: productLogicalName || "new_product" // Uses the real logical name from the metadata
                }]);

                console.log("Successfully updated product: " + productName);
            } else {
                productAttr.setValue(null);
                console.warn("Inventory found, but Product field is empty.");
            }
        },
        function error(error) {
            formContext.ui.clearFormNotification("inventory_fetch");
            console.error("Data Retrieval Error: " + error.message);
            
            var alertStrings = { 
                confirmButtonLabel: "Ok", 
                text: "Error: " + error.message + ". Please verify that 'new_productid' is the correct logical name of the lookup field in the Inventory table.", 
                title: "Data Retrieval Error" 
            };
            Xrm.Navigation.openAlertDialog(alertStrings, { height: 120, width: 350 });
        }
    );
};