var DealForm = DealForm || {};
 
// --- Event Handlers ---
 
DealForm.onLoad = function (executionContext) {
    DealForm.fixCanvasAppHeight(executionContext);
    DealForm.setDefaultAccountFromOpportunity(executionContext);
    DealForm.setDealOptionRequirements(executionContext);
    DealForm.setPlayoffOptionRequirements(executionContext);
    DealForm.applyConditionalVisibility(executionContext);
    DealForm.calculateMaxActivationSpend(executionContext);
};
 
DealForm.setDefaultAccountFromOpportunity = function (executionContext) {
    var formContext = executionContext.getFormContext();
    if (formContext.ui.getFormType() !== 1) return; // 1 = Create
    var oppAttr = formContext.getAttribute("new_opportunity");
    if (oppAttr && oppAttr.getValue() !== null) {
        DealForm.fetchAndSetAccount(formContext);
    }
};
 
DealForm.onOpportunityChange = function (executionContext) {
    DealForm.fetchAndSetAccount(executionContext.getFormContext());
};
 
DealForm.onDependencyChangeForName = function (executionContext) {
    DealForm.generateName(executionContext.getFormContext());
};
 
DealForm.onDealOptionStatusChange = function (executionContext) {
    DealForm.setDealOptionRequirements(executionContext);
};
 
DealForm.onOptionNegotiationWindowChange = function (executionContext) {
    DealForm.setDealOptionRequirements(executionContext);
};
 
DealForm.onPlayoffOptionStatusChange = function (executionContext) {
    DealForm.setPlayoffOptionRequirements(executionContext);
};
 
DealForm.onOptionNegotiationDatesChange = function (executionContext) {
    DealForm.validateNegotiationDateRange(executionContext);
};

/**
 * Centralized logic for all conditional visibility on the Deal form.
 * Called from onLoad and from OnChange of fields that affect visibility.
 * @param {object} executionContext
 */
DealForm.applyConditionalVisibility = function (executionContext) {
    var formContext = executionContext.getFormContext();

    // ========================================================
    // Helper: safe setVisible by field logical name
    // ========================================================
    var setFieldVisible = function (fieldName, visible) {
        var ctrl = formContext.getControl(fieldName);
        if (ctrl) ctrl.setVisible(visible);
    };

    // ========================================================
    // Helper: safe setVisible by section (requires tab + section names)
    // ========================================================
    var setSectionVisible = function (tabName, sectionName, visible) {
        var tab = formContext.ui.tabs.get(tabName);
        if (!tab) return;
        var section = tab.sections.get(sectionName);
        if (section) section.setVisible(visible);
    };

    // ========================================================
    // Rule 1: Multi-year tracking fields
    // Visible only when Total Contract Years > 1
    // ========================================================
    var totalYearsAttr = formContext.getAttribute("new_totalcontractyears");
    var totalYears = totalYearsAttr ? totalYearsAttr.getValue() : null;
    var isMultiYear = (totalYears !== null && totalYears > 1);

    setFieldVisible("new_contractyearsequence", isMultiYear);
    setFieldVisible("new_totalcontractyears", isMultiYear);

    // ========================================================
    // Rule 2: Regular Season Deal lookup
    // Visible only in Playoff Deals (field has value). Always read-only.
    // ========================================================
    var regularSeasonAttr = formContext.getAttribute("new_regularseasondeal");
    var isPlayoffDeal = (regularSeasonAttr && regularSeasonAttr.getValue() !== null);

    setFieldVisible("new_regularseasondeal", isPlayoffDeal);
    if (isPlayoffDeal) {
        var ctrl = formContext.getControl("new_regularseasondeal");
        if (ctrl) ctrl.setDisabled(true);
    }

    // ========================================================
    // Rule 3: Section "Playoff Information"
    // Visible only on regular Deals (NOT on Playoff Deals themselves)
    // IMPORTANT: Adjust "tab_general" and "SectionPlayoffInformation"
    // to match the actual names you give them in the form editor.
    // ========================================================
    setSectionVisible("tab_general", "SectionPlayoffInformation", !isPlayoffDeal);

    // ========================================================
    // Rule 4: Originating Opportunity
    // Visible only if it has value (i.e., this Deal was cloned from a multi-year)
    // ========================================================
    var originatingAttr = formContext.getAttribute("new_originatingopportunity");
    var hasOriginating = (originatingAttr && originatingAttr.getValue() !== null);
    setFieldVisible("new_originatingopportunity", hasOriginating);
};

/**
 * OnChange handler for fields whose change affects visibility.
 */
DealForm.onVisibilityTriggerChange = function (executionContext) {
    DealForm.applyConditionalVisibility(executionContext);
};
 
// --- Core Logic ---
DealForm.setDealOptionRequirements = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var setReq = function (field, level) {
        var a = formContext.getAttribute(field);
        if (a) a.setRequiredLevel(level);
    };
    var setVisible = function (field, visible) {
        var c = formContext.getControl(field);
        if (c) c.setVisible(visible);
    };

    // FIX 4-A: Deal Option Status is ALWAYS required (per Ray's table)
    setReq("new_optouttype", "required");

    // Reset conditional fields
    var conditionalFields = [
        "new_optoutdeadline", "new_optionnegotiationwindow",
        "new_optionnegotiationstartdate", "new_optionnegotiationdeadlinedate",
        "new_dealoptiondecision"
    ];
    conditionalFields.forEach(function (f) { setReq(f, "none"); });

    var statusAttr = formContext.getAttribute("new_optouttype");
    if (!statusAttr) return;
    var status = statusAttr.getValue();

    var NO_OPTION = 100000000;
    var UNKNOWN = 100000003;

    var isNoOptionOrUnknown = (status === NO_OPTION || status === UNKNOWN || status === null);

    if (!isNoOptionOrUnknown) {
        setReq("new_optoutdeadline", "required");
        setReq("new_optionnegotiationwindow", "required");
        setReq("new_dealoptiondecision", "required");

        var windowAttr = formContext.getAttribute("new_optionnegotiationwindow");
        if (windowAttr && windowAttr.getValue() === true) {
            setReq("new_optionnegotiationstartdate", "required");
            setReq("new_optionnegotiationdeadlinedate", "required");
            setVisible("new_optionnegotiationstartdate", true);
            setVisible("new_optionnegotiationdeadlinedate", true);
        } else {
            setVisible("new_optionnegotiationstartdate", false);
            setVisible("new_optionnegotiationdeadlinedate", false);
        }
    } else {
        setVisible("new_optionnegotiationstartdate", false);
        setVisible("new_optionnegotiationdeadlinedate", false);
    }
}; 
DealForm.setPlayoffOptionRequirements = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var setReq = function (field, level) {
        var a = formContext.getAttribute(field);
        if (a) a.setRequiredLevel(level);
    };

    // CRITICAL EARLY EXIT: if is = Playoff Deal, not apply required fields for Playoff
    var regularSeasonAttr = formContext.getAttribute("new_regularseasondeal");
    if (regularSeasonAttr && regularSeasonAttr.getValue() !== null) {
        var playoffFields = [
            "new_playoffoptionstatus", "new_playoffoptiondeadline",
            "new_playoffoptiondecision"
        ];
        playoffFields.forEach(function (f) { setReq(f, "none"); });
        return;
    }

    // Playoff Option Status is ALWAYS required for regular Deals
    setReq("new_playoffoptionstatus", "required");

    // Reset conditional playoff fields
    setReq("new_playoffoptiondeadline", "none");
    setReq("new_playoffoptiondecision", "none");

    var statusAttr = formContext.getAttribute("new_playoffoptionstatus");
    if (!statusAttr) return;
    var status = statusAttr.getValue();

    var OUT = 100000003;
    var UNKNOWN = 100000004;

    var needsRequired = (status !== null && status !== OUT && status !== UNKNOWN);

    if (needsRequired) {
        setReq("new_playoffoptiondeadline", "required");
        setReq("new_playoffoptiondecision", "required");
    }
};
 
DealForm.validateNegotiationDateRange = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var startAttr = formContext.getAttribute("new_optionnegotiationstartdate");
    var endAttr = formContext.getAttribute("new_optionnegotiationdeadlinedate");
    if (!startAttr || !endAttr) return;
 
    var start = startAttr.getValue();
    var end = endAttr.getValue();
    if (start && end && end < start) {
        formContext.ui.setFormNotification(
            "Option Negotiation Deadline Date must be after Start Date.",
            "ERROR", "neg_date_range"
        );
    } else {
        formContext.ui.clearFormNotification("neg_date_range");
    }
};
 
 
DealForm.fetchAndSetAccount = function (formContext) {
    var oppAttr = formContext.getAttribute("new_opportunity");
    var accountAttr = formContext.getAttribute("new_accountid");
    if (!oppAttr || !accountAttr) return;
    var oppValue = oppAttr.getValue();
    if (oppValue === null) { accountAttr.setValue(null); return; }
    var oppId = oppValue[0].id.replace("{", "").replace("}", "");
    formContext.ui.setFormNotification("Syncing Account from Opportunity...", "INFO", "opp_sync");
    Xrm.WebApi.retrieveRecord("opportunity", oppId, "?$select=_parentaccountid_value").then(
        function success(result) {
            formContext.ui.clearFormNotification("opp_sync");
            var accountId = result["_parentaccountid_value"];
            var accountName = result["_parentaccountid_value@OData.Community.Display.V1.FormattedValue"];
            var accountLogicalName = result["_parentaccountid_value@Microsoft.Dynamics.CRM.lookuplogicalname"];
            if (accountId && accountName) {
                accountAttr.setValue([{ id: accountId, name: accountName, entityType: accountLogicalName || "account" }]);
                DealForm.generateName(formContext);
            } else {
                accountAttr.setValue(null);
            }
        },
        function error(error) {
            formContext.ui.clearFormNotification("opp_sync");
            console.error("Error retrieving Account: " + error.message);
        }
    );
};
 
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
    if (accountName && seasonName) newName = accountName + " - " + seasonName;
    else if (accountName) newName = accountName;
    else if (seasonName) newName = seasonName;
    if (newName !== "") nameAttr.setValue(newName);
};
 
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
    } catch (e) { console.error(e); }
};

/**
 * OnSave validation. Block save if Deal is being closed as Won
 * with Deal Option Status blank or Unknown.
 * Provides immediate user feedback (UX), but the plugin guards against
 * API / Power Automate / CloseCorpPartnership flow bypass.
 */
DealForm.onSaveValidateOptionStatusForWon = function (executionContext) {
    var formContext = executionContext.getFormContext();

    // Skip validation if this is a Playoff Deal (different lifecycle)
    var regularSeasonAttr = formContext.getAttribute("new_regularseasondeal");
    if (regularSeasonAttr && regularSeasonAttr.getValue() !== null) {
        return;
    }

    var statusAttr = formContext.getAttribute("new_dealstatus");
    var optionStatusAttr = formContext.getAttribute("new_optouttype");
    if (!statusAttr || !optionStatusAttr) return;

    var dealStatus = statusAttr.getValue();
    if (!dealStatus || dealStatus.length === 0) return;

    // Get the Deal Status name to detect "Closed Won"
    // Note: dealStatus is an array of lookup references with format [{id, name, entityType}]
    var statusName = dealStatus[0].name || "";

    // Match by name pattern (more reliable than hardcoded GUIDs)
    // Looks for "Closed Won" — could be "8 - Closed Won" or similar
    var isClosingWon = /Closed\s*Won/i.test(statusName);
    if (!isClosingWon) return;

    var optionStatus = optionStatusAttr.getValue();
    var UNKNOWN = 100000003;

    if (optionStatus === null || optionStatus === UNKNOWN) {
        // Block save with user-friendly message
        var eventArgs = executionContext.getEventArgs();
        eventArgs.preventDefault();

        formContext.ui.setFormNotification(
            "Cannot close this Deal as Won: 'Deal Option Status' must be set to Opt-In, Opt-Out, or No Option (not blank or Unknown).",
            "ERROR",
            "won_validation"
        );
    } else {
        formContext.ui.clearFormNotification("won_validation");
    }
};

/**
 * Calculates and displays Max Activation Spend on the form.
 * Reads the percentage from the Deal Configuration table (singleton, first record).
 * Triggered onLoad and OnChange of new_total.
 *
 * NOTE: The plugin (InventoryManagement.RollupTotalsToParentDeal) is the
 * authoritative source. This JS only mirrors the calculation in the UI for
 * immediate visual feedback when the form is open and new_total changes.
 *
 * @param {object} executionContext
 */
DealForm.calculateMaxActivationSpend = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var totalAttr = formContext.getAttribute("new_total");
    var maxAttr = formContext.getAttribute("new_maxactivationspend");

    if (!totalAttr || !maxAttr) {
        console.warn("DealForm.calculateMaxActivationSpend: missing new_total or new_maxactivationspend on form.");
        return;
    }

    var dealTotal = totalAttr.getValue() || 0;

    if (dealTotal === 0) {
        maxAttr.setValue(0);
        return;
    }

    // Fetch the singleton config record
    Xrm.WebApi.retrieveMultipleRecords(
        "new_dealconfiguration",
        "?$select=new_maxactivationspendpercent&$top=1"
    ).then(
        function success(result) {
            if (!result.entities || result.entities.length === 0) {
                console.warn("DealForm.calculateMaxActivationSpend: no Deal Configuration record found.");
                maxAttr.setValue(null);
                return;
            }

            var percent = result.entities[0].new_maxactivationspendpercent;
            if (percent === null || percent === undefined) {
                console.warn("DealForm.calculateMaxActivationSpend: new_maxactivationspendpercent is null in config.");
                maxAttr.setValue(null);
                return;
            }

            var maxSpend = Math.round((dealTotal * (percent / 100)) * 100) / 100;
            maxAttr.setValue(maxSpend);

            console.log("MaxActivationSpend (UI) -> Total: " + dealTotal + " × " + percent + "% = " + maxSpend);
        },
        function error(err) {
            console.error("DealForm.calculateMaxActivationSpend: error fetching config: " + err.message);
        }
    );
};
