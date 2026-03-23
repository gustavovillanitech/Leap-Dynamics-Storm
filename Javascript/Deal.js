var DealForm = DealForm || {};

// --- Event Handlers ---

/**
 * Main OnLoad handler. Add all onLoad logic here by calling the appropriate functions.
 * @param {object} executionContext
 */
DealForm.onLoad = function (executionContext) {
    DealForm.fixCanvasAppHeight(executionContext);
    
    // Check if we are creating from a subgrid and sync the account on load
    DealForm.setDefaultAccountFromOpportunity(executionContext);
};

/**
 * Checks if the form is in "Create" mode and if the Opportunity is already populated.
 * This handles the scenario where a Deal is created from an Opportunity subgrid.
 * @param {object} executionContext 
 */
DealForm.setDefaultAccountFromOpportunity = function (executionContext) {
    var formContext = executionContext.getFormContext();
    
    // 1 = Create form. We only want this to run when creating a NEW Deal.
    if (formContext.ui.getFormType() !== 1) return;
    
    var oppAttr = formContext.getAttribute("new_opportunity");
    
    // If the Opportunity is already filled (e.g., mapped from the subgrid), trigger the sync
    if (oppAttr && oppAttr.getValue() !== null) {
        DealForm.fetchAndSetAccount(formContext);
    }
};

/**
 * Triggered on the 'new_opportunity' field OnChange event.
 * @param {object} executionContext 
 */
DealForm.onOpportunityChange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    DealForm.fetchAndSetAccount(formContext);
};

/**
 * Triggered on the 'new_accountid' or 'new_season' field OnChange events.
 * @param {object} executionContext 
 */
DealForm.onDependencyChangeForName = function (executionContext) {
    var formContext = executionContext.getFormContext();
    DealForm.generateName(formContext);
};


// --- Core Logic Methods ---

/**
 * Retrieves the Account from the selected Opportunity 
 * and populates the Deal's Account field.
 * @param {object} formContext 
 */
DealForm.fetchAndSetAccount = function (formContext) {
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
                
                // Trigger the name generation automatically after syncing the account
                DealForm.generateName(formContext);
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
 * Builds the Deal Name based on Account and Season values.
 * Format: [Account Name] - [Season]
 * @param {object} formContext 
 */
DealForm.generateName = function (formContext) {
    var accountAttr = formContext.getAttribute("new_accountid");
    var seasonAttr = formContext.getAttribute("new_season");
    var nameAttr = formContext.getAttribute("new_name");

    if (!accountAttr || !seasonAttr || !nameAttr) return;

    var accountVal = accountAttr.getValue();
    var seasonVal = seasonAttr.getValue();

    var accountName = (accountVal && accountVal.length > 0) ? accountVal[0].name : "";
    var seasonName = (seasonVal && seasonVal.length > 0) ? seasonVal[0].name : "";

    var newName = "";
    if (accountName && seasonName) {
        newName = accountName + " - " + seasonName;
    } else if (accountName) {
        newName = accountName;
    } else if (seasonName) {
        newName = seasonName;
    }

    if (newName !== "") {
        nameAttr.setValue(newName);
    }
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
            "[data-lp-id='MscrmControls.Containers.FieldSectionItem|new_appcontroldeallinebuilder|new_deals'] " +
            "{ height: 480px !important; max-height: 480px !important; overflow: hidden !important; }";

        mainDocument.head.appendChild(style);
        console.log("[DealForm] Canvas height fix injected.");
    } catch (error) {
        console.error("[DealForm] fixCanvasAppHeight error: ", error);
    }
};

DealForm.lockGridDealLineFields = function(executionContext) {
    var formContext = executionContext.getFormContext();
    
    // Lista de campos a bloquear en la grilla
    var fieldsToLock = ["new_total", "new_listrate", "new_gainloss", "new_yield", "new_ratecard"];

    fieldsToLock.forEach(function(fieldName) {
        var control = formContext.getControl(fieldName);
        if (control) {
            control.setDisabled(true); // Bloquea la celda
        }
    });
};