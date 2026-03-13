var DealForm = DealForm || {};

// --- Event Handlers ---

/**
 * Main OnLoad handler. Add all onLoad logic here by calling the appropriate functions.
 * @param {object} executionContext
 */
DealForm.onLoad = function (executionContext) {
    DealForm.fixCanvasAppHeight(executionContext);
};

/**
 * Triggered on the 'new_opportunity' field OnChange event.
 * Retrieves the Account from the selected Opportunity and populates the Deal's Account field.
 * @param {object} executionContext 
 */
DealForm.onOpportunityChange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var oppAttr = formContext.getAttribute("new_opportunity");
    var accountAttr = formContext.getAttribute("new_accountid");
    if (!oppAttr || !accountAttr) return;
    var oppValue = oppAttr.getValue();
    if (oppValue === null) {
        accountAttr.setValue(null);
        return;
    }
    var oppId = oppValue[0].id.replace("{", "").replace("}", "");
    formContext.ui.setFormNotification("Syncing Account from Opportunity...", "INFO", "opp_sync");
    Xrm.WebApi.retrieveRecord("opportunity", oppId, "?$select=_parentaccountid_value").then(
        function success(result) {
            formContext.ui.clearFormNotification("opp_sync");
            var accountId = result["_parentaccountid_value"];
            var accountName = result["_parentaccountid_value@OData.Community.Display.V1.FormattedValue"];
            var accountLogicalName = result["_parentaccountid_value@Microsoft.Dynamics.CRM.lookuplogicalname"];
            if (accountId && accountName) {
                accountAttr.setValue([{
                    id: accountId,
                    name: accountName,
                    entityType: accountLogicalName || "account"
                }]);
                console.log("Account synced successfully: " + accountName);
            } else {
                accountAttr.setValue(null);
                console.warn("The selected Opportunity does not have an associated Account.");
            }
        },
        function error(error) {
            formContext.ui.clearFormNotification("opp_sync");
            console.error("Error retrieving Account from Opportunity: " + error.message);
            var alertStrings = {
                confirmButtonLabel: "Ok",
                text: "Could not sync Account: " + error.message,
                title: "Sync Error"
            };
            Xrm.Navigation.openAlertDialog(alertStrings, { height: 120, width: 300 });
        }
    );
};

/**
 * Fixes canvas app height using polling + MutationObserver
 * to handle D365 re-renders after canvas app finishes loading.
 * @param {object} executionContext
 */
DealForm.fixCanvasAppHeight = function (executionContext) {
    try {
        var mainDocument = window.top.document;
        if (mainDocument.getElementById("canvas-height-fix")) return;

        var style = mainDocument.createElement("style");
        style.id = "canvas-height-fix";
        style.innerHTML =
            "[data-lp-id='MscrmControls.Containers.FieldSectionItem|new_canvasappcontrol|new_deals'] " +
            "{ height: 480px !important; max-height: 480px !important; overflow: hidden !important; }";

        mainDocument.head.appendChild(style);
        console.log("[DealForm] Canvas height fix injected.");
    } catch (error) {
        console.error("[DealForm] fixCanvasAppHeight error: ", error);
    }
};