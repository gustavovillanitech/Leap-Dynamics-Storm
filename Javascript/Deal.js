var DealForm = DealForm || {};

// --- Event Handlers ---

/**
 * Triggered on the 'new_opportunity' field OnChange event.
 * Retrieves the Account from the selected Opportunity and populates the Deal's Account field.
 * @param {object} executionContext 
 */
DealForm.onOpportunityChange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var oppAttr = formContext.getAttribute("new_opportunity");
    var accountAttr = formContext.getAttribute("new_accountid");

    // Safety check: ensure both attributes exist on the form
    if (!oppAttr || !accountAttr) return;

    var oppValue = oppAttr.getValue();

    // If the Opportunity field is cleared, clear the Account field as well
    if (oppValue === null) {
        accountAttr.setValue(null);
        return;
    }

    var oppId = oppValue[0].id.replace("{", "").replace("}", "");

    // Notify the user that data is being synced
    formContext.ui.setFormNotification("Syncing Account from Opportunity...", "INFO", "opp_sync");

    /**
     * We retrieve the 'parentaccountid' from the Opportunity.
     * Note: In standard Dynamics, the field is 'parentaccountid'. 
     * If your Opportunity uses a custom field like 'new_accountid', change the string below.
     */
    Xrm.WebApi.retrieveRecord("opportunity", oppId, "?$select=_parentaccountid_value").then(
        function success(result) {
            formContext.ui.clearFormNotification("opp_sync");

            // Extracting the ID and the Formatted Name (Label) of the Account
            var accountId = result["_parentaccountid_value"];
            var accountName = result["_parentaccountid_value@OData.Community.Display.V1.FormattedValue"];
            var accountLogicalName = result["_parentaccountid_value@Microsoft.Dynamics.CRM.lookuplogicalname"];

            if (accountId && accountName) {
                // Set the value for the Account lookup field in the Deal form
                accountAttr.setValue([{
                    id: accountId,
                    name: accountName,
                    entityType: accountLogicalName || "account"
                }]);

                console.log("Account synced successfully: " + accountName);
            } else {
                // If the Opportunity has no Account associated
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