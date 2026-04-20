var DealForm = DealForm || {};
 
// --- Event Handlers ---
 
DealForm.onLoad = function (executionContext) {
    DealForm.fixCanvasAppHeight(executionContext);
    DealForm.setDefaultAccountFromOpportunity(executionContext);
    DealForm.setDealOptionRequirements(executionContext);
    DealForm.setPlayoffOptionRequirements(executionContext);
    DealForm.applyConditionalVisibility(executionContext);
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
 
    // Reset
    var conditionalFields = [
        "new_optoutdeadline", "new_optionnegotiationwindow",
        "new_optionnegotiationstartdate", "new_optionnegotiationdeadlinedate",
        "new_dealoptiondecision"
    ];
    conditionalFields.forEach(function (f) { setReq(f, "none"); });
 
    var statusAttr = formContext.getAttribute("new_optouttype");
    if (!statusAttr) return;
    var status = statusAttr.getValue();
 
    // Optionset values: Opt-In=100000001, Opt-Out=100000002, No Option=100000000, Unknown=100000003
    var NO_OPTION = 100000000;
    var UNKNOWN = 100000003;
 
    var isNoOptionOrUnknown = (status === NO_OPTION || status === UNKNOWN || status === null);
 
    if (!isNoOptionOrUnknown) {
        // Status is Opt-In or Opt-Out → require these fields
        setReq("new_optoutdeadline", "required");
        setReq("new_optionnegotiationwindow", "required");
        setReq("new_dealoptiondecision", "required");
 
        // Show the negotiation window dates only if window = Yes
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
        // Hide date range fields entirely
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
 
    // Reset
    setReq("new_playoffoptiondeadline", "none");
    setReq("new_playoffoptiondecision", "none");
 
    var statusAttr = formContext.getAttribute("new_playoffoptionstatus");
    if (!statusAttr) return;
    var status = statusAttr.getValue();
 
    // Values: Opt-In=100000000, Opt-Out=100000001, In=100000002, Out=100000003, Unknown=100000004
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
