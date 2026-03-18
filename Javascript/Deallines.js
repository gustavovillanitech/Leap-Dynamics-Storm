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

    // If inventory is cleared, clear product and rates
    if (inventoryValue === null) {
        productAttr.setValue(null);
        // Optional: clear rates and recalculate if inventory is removed
        // formContext.getAttribute("new_rate").setValue(null);
        // formContext.getAttribute("new_ratecard").setValue(null);
        // DealLineForm.calculateFinancialMetrics(executionContext);
        return;
    }

    var inventoryId = inventoryValue[0].id.replace("{", "").replace("}", "");

    formContext.ui.setFormNotification("Fetching associated data...", "INFO", "inventory_fetch");

    /**
     * Query the Inventory table to retrieve the Product lookup AND the single Rate.
     * Ensure "new_rate" is the actual logical name of the price field on the Inventory entity.
     */
    var selectQuery = "?$select=_new_productid_value,new_rate";

    Xrm.WebApi.retrieveRecord("new_inventory", inventoryId, selectQuery).then(
        function success(result) {
            formContext.ui.clearFormNotification("inventory_fetch");

            // 1. Set the Product Lookup
            var productId = result["_new_productid_value"];
            var productName = result["_new_productid_value@OData.Community.Display.V1.FormattedValue"];
            var productLogicalName = result["_new_productid_value@Microsoft.Dynamics.CRM.lookuplogicalname"];

            if (productId && productName) {
                productAttr.setValue([{
                    id: productId,
                    name: productName,
                    entityType: productLogicalName || "new_product" 
                }]);
                console.log("Successfully updated product: " + productName);
            } else {
                productAttr.setValue(null);
                console.warn("Inventory found, but Product field is empty.");
            }

            // 2. Set the Rate from Inventory to BOTH Rate and Rate Card on the Deal Line
            // (If the rate field on Inventory is not called "new_rate", change it here)
            var inventoryRate = result["new_rate"] || 0; 

            var rateAttr = formContext.getAttribute("new_rate");
            var rateCardAttr = formContext.getAttribute("new_ratecard");

            // Requirement: Populate the Rate from the Inventory as the "Rate" and "Rate Card" values
            if (rateAttr) rateAttr.setValue(inventoryRate);
            if (rateCardAttr) rateCardAttr.setValue(inventoryRate);

            // 3. NOW trigger the math!
            DealLineForm.calculateFinancialMetrics(executionContext);
        },
        function error(error) {
            formContext.ui.clearFormNotification("inventory_fetch");
            console.error("Data Retrieval Error: " + error.message);
            
            var alertStrings = { 
                confirmButtonLabel: "Ok", 
                text: "Error: " + error.message + ". Please verify the schema names.", 
                title: "Data Retrieval Error" 
            };
            Xrm.Navigation.openAlertDialog(alertStrings, { height: 120, width: 350 });
        }
    );
};
/**
 * Triggered on the OnChange event of 'new_quantity', 'new_rate', 'new_ratecard', or 'new_discount'.
 * Calculates Discount, Total, List Rate, Gain/Loss, and Yield dynamically on the form.
 * @param {object} executionContext 
 */
DealLineForm.calculateFinancialMetrics = function (executionContext) {
    var formContext = executionContext.getFormContext();

    // 1. Get Input Attributes
    var quantityAttr = formContext.getAttribute("new_quantity");
    var rateAttr = formContext.getAttribute("new_rate");           // Rate Charged
    var rateCardAttr = formContext.getAttribute("new_ratecard");   // Rate Card (List Price)
    var discountAttr = formContext.getAttribute("new_discount");   // NEW: Discount Percentage

    // 2. Get Output (Calculated) Attributes
    var totalAttr = formContext.getAttribute("new_total");
    var listRateAttr = formContext.getAttribute("new_listrate");
    var gainLossAttr = formContext.getAttribute("new_gainloss");
    var yieldAttr = formContext.getAttribute("new_yield");

    // 3. Validate that essential fields exist on the form
    if (!quantityAttr || !rateAttr || !rateCardAttr || !totalAttr) {
        console.warn("DealLineForm.calculateFinancialMetrics: Missing essential financial fields.");
        return;
    }

    // 4. Retrieve values
    var quantity = quantityAttr.getValue() || 0;
    var rateCard = rateCardAttr.getValue() || 0;
    var discount = discountAttr ? (discountAttr.getValue() || 0) : 0;
    var rate = rateAttr.getValue() || 0;

    // 5. DISCOUNT LOGIC: If a discount percentage exists, calculate the new Rate automatically
    // Formula: Rate = Rate Card - (Rate Card * (Discount / 100))
    if (discount > 0 && rateCard > 0) {
        rate = rateCard - (rateCard * (discount / 100));
        rateAttr.setValue(rate); // Update the Rate field on the screen
    }

    // 6. Perform the Math Calculations
    var total = quantity * rate;
    var listRate = quantity * rateCard;
    var gainLoss = total - listRate;

    var yieldValue = 0;
    if (listRate !== 0) {
        yieldValue = total / listRate;
    }

    // 7. Set the calculated values back to the form
    totalAttr.setValue(total);
    if (listRateAttr) listRateAttr.setValue(listRate);
    if (gainLossAttr) gainLossAttr.setValue(gainLoss);
    if (yieldAttr) yieldAttr.setValue(yieldValue);

    console.log("Financial Metrics Updated -> Discount: " + discount + "%, New Rate: " + rate + ", Total: " + total);
};