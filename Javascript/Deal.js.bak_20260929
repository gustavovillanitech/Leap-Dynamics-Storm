var DealForm = DealForm || {};
 
// --- Event Handlers ---
 
DealForm.onLoad = function (executionContext) {
    DealForm.fixCanvasAppHeight(executionContext);
    DealForm.setDefaultAccountFromOpportunity(executionContext);
    DealForm.setDealOptionRequirements(executionContext);
    DealForm.setPlayoffOptionRequirements(executionContext);
    DealForm.applyConditionalVisibility(executionContext);
    DealForm.applyIncrementalGamesRules(executionContext);
    DealForm.attachGridSaveHandlers(executionContext);
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

    // Rule 5: Frozen Max Activation Spend Percent — visible only when Deal has the value
    var frozenPercentAttr = formContext.getAttribute("new_maxactivationspendpercent");
    var hasFrozenPercent = (frozenPercentAttr && frozenPercentAttr.getValue() !== null);
    setFieldVisible("new_maxactivationspendpercent", hasFrozenPercent);
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

    var IN = 100000002;
    var OUT = 100000003;
    var UNKNOWN = 100000004;

    // A deadline and a decision only make sense when there is an option still to be exercised,
    // which is Opt-In and Opt-Out. In and Out are already settled by the contract: the playoff
    // package is either included or it is not, so there is no date to decide by and no decision
    // to record. Unknown has nothing to go on yet.
    //
    // This mirrors the Deal Option rule, where No Option and Unknown are exempt for the same
    // reason. Keeping the two sections asymmetric was blocking deals from being moved to
    // Closed Won over a date that does not exist in the contract.
    var isSettledOrUnknown = (status === null || status === IN || status === OUT || status === UNKNOWN);

    if (!isSettledOrUnknown) {
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
            "{ height: 450px !important; max-height: 450px !important; overflow: hidden !important; }";
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
 * Priority:
 *   1. If the Deal has a frozen percent (new_maxactivationspendpercent), use it.
 *   2. Otherwise, fall back to the global percent in new_DealConfiguration.
 *
 * NOTE: The plugin (InventoryManagement.RollupTotalsToParentDeal) is the
 * authoritative source. This JS only mirrors the calculation in the UI for
 * immediate visual feedback when the form is open and new_total changes.
 */
DealForm.calculateMaxActivationSpend = function (executionContext) {
    var formContext = executionContext.getFormContext();

    var totalAttr = formContext.getAttribute("new_total");
    var maxAttr = formContext.getAttribute("new_maxactivationspend");
    var frozenPercentAttr = formContext.getAttribute("new_maxactivationspendpercent");

    if (!totalAttr || !maxAttr) {
        console.warn("DealForm.calculateMaxActivationSpend: missing new_total or new_maxactivationspend on form.");
        return;
    }

    var dealTotal = totalAttr.getValue() || 0;

    if (dealTotal === 0) {
        maxAttr.setValue(0);
        return;
    }

    // Priority 1: Deal-level frozen percent
    var frozenPercent = frozenPercentAttr ? frozenPercentAttr.getValue() : null;

    if (frozenPercent !== null && frozenPercent !== undefined) {
        var maxSpendFrozen = Math.round((dealTotal * (frozenPercent / 100)) * 100) / 100;
        maxAttr.setValue(maxSpendFrozen);
        console.log("MaxActivationSpend (UI, FROZEN) -> Total: " + dealTotal + " × " + frozenPercent + "% = " + maxSpendFrozen);
        return;
    }

    // Priority 2: Global config
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

            var globalPercent = result.entities[0].new_maxactivationspendpercent;
            if (globalPercent === null || globalPercent === undefined) {
                console.warn("DealForm.calculateMaxActivationSpend: new_maxactivationspendpercent is null in config.");
                maxAttr.setValue(null);
                return;
            }

            var maxSpendGlobal = Math.round((dealTotal * (globalPercent / 100)) * 100) / 100;
            maxAttr.setValue(maxSpendGlobal);
            console.log("MaxActivationSpend (UI, GLOBAL) -> Total: " + dealTotal + " × " + globalPercent + "% = " + maxSpendGlobal);
        },
        function error(err) {
            console.error("DealForm.calculateMaxActivationSpend: error fetching config: " + err.message);
        }
    );
};


// ============================================================================
//  INCREMENTAL GAMES
//  Section behaviour for the "Incremental Games" block on the Deal form.
//  Everything in this block is driven by Incremental Games Clause so the
//  section stays short on the deals that do not have the clause.
// ============================================================================

DealForm.IncrementalGames = {
    // Incremental Games Clause
    CLAUSE_IN: 100000000,
    CLAUSE_OPT_IN: 100000001,
    CLAUSE_OPT_OUT: 100000002,
    CLAUSE_OUT: 100000003,

    // Incremental Pricing Method
    METHOD_PER_GAME_RATE: 100000000,
    METHOD_ITEMIZED: 100000001,
    METHOD_FLAT_AMOUNT: 100000002,

    // Home-game context: read-only, shown whenever the partner receives extra games
    GAMES_FIELDS: [
        "new_contractedhomegames",
        "new_gamescoveredbycontract",
        "new_seasonhomegames",
        "new_incrementalhomegames"
    ],

    // Away-game context: only when the contract has away-game benefits
    AWAY_GAMES_FIELDS: [
        "new_contractedawaygames",
        "new_seasonawaygames",
        "new_incrementalawaygames"
    ],

    // Money: hidden when the contract carries no incremental charge
    MONEY_FIELDS: [
        "new_incrementalpricingmethod",
        "new_incrementalcontractvalue",
        "new_totalincrementalrevenue",
        "new_unallocatedvariance"
    ],

    // The per-game investment only means something under Per Game Rate. Under Flat Amount the
    // figure is negotiated outright, and under Itemized by Line it lives on each deal line. Left
    // visible in those methods, the rates invite the reader to do the arithmetic and reconcile it
    // against a contract value that was never derived from them.
    PER_GAME_RATE_FIELDS: [
        "new_investmentperhomegame"
    ],

    AWAY_MONEY_FIELDS: [
        "new_investmentperawaygame"
    ],

    // Season Home/Away Games cached per Season record, filled by
    // DealForm.getSeasonGames so typing in Contracted Home Games does not fire
    // one request per keystroke.
    _seasonCache: { id: null, home: 0, away: 0 }
};

/**
 * Applies visibility and requirement levels for the Incremental Games section.
 * Called from onLoad and from OnChange of Incremental Games Clause,
 * Away Game Benefits and Incremental Pricing Method.
 *
 * Visibility by clause:
 *   Out / blank -> Clause and Notes only.
 *   Opt-Out     -> Clause, games context and Notes. The partner still receives
 *                  the additional games, so the counts stay visible, but there
 *                  is no incremental charge to record.
 *   In / Opt-In -> full section.
 *
 * @param {object} executionContext
 */
DealForm.applyIncrementalGamesRules = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var cfg = DealForm.IncrementalGames;

    var setVisible = function (fieldName, visible) {
        var ctrl = formContext.getControl(fieldName);
        if (ctrl) ctrl.setVisible(visible);
    };
    var setReq = function (fieldName, level) {
        var attr = formContext.getAttribute(fieldName);
        if (attr) attr.setRequiredLevel(level);
    };
    var setList = function (fieldNames, visible) {
        fieldNames.forEach(function (f) { setVisible(f, visible); });
    };

    var clauseAttr = formContext.getAttribute("new_incrementalgamesclause");
    if (!clauseAttr) return; // section not on this form

    var clause = clauseAttr.getValue();

    var hasClause = (clause === cfg.CLAUSE_IN || clause === cfg.CLAUSE_OPT_IN);
    var receivesGames = (hasClause || clause === cfg.CLAUSE_OPT_OUT);

    // Away-game fields only when the contract actually has away-game benefits
    var awayAttr = formContext.getAttribute("new_awaygamebenefits");
    var hasAwayBenefits = (awayAttr && awayAttr.getValue() === true);

    // How the incremental amount is arrived at decides which money fields are meaningful.
    var methodAttr = formContext.getAttribute("new_incrementalpricingmethod");
    var method = methodAttr ? methodAttr.getValue() : null;
    var isPerGameRate = (method === cfg.METHOD_PER_GAME_RATE);

    // --- Visibility ---
    setVisible("new_awaygamebenefits", receivesGames);
    setList(cfg.GAMES_FIELDS, receivesGames);
    setList(cfg.AWAY_GAMES_FIELDS, receivesGames && hasAwayBenefits);
    setList(cfg.MONEY_FIELDS, hasClause);
    setList(cfg.PER_GAME_RATE_FIELDS, hasClause && isPerGameRate);
    setList(cfg.AWAY_MONEY_FIELDS, hasClause && hasAwayBenefits && isPerGameRate);

    // Notes only matter once the deal actually has a clause. Left visible on a
    // deal with no clause, the empty multiline box stretches the section to full
    // height with nothing beside it.
    setVisible("new_incrementalgamesnotes", receivesGames);
    DealForm.setSectionVisibleByName(formContext, "IncrementalGamesNotes", receivesGames);

    // --- Requirement levels ---
    // Reset everything first so a change of clause never leaves a stale requirement.
    cfg.GAMES_FIELDS.concat(cfg.AWAY_GAMES_FIELDS, cfg.MONEY_FIELDS, cfg.PER_GAME_RATE_FIELDS, cfg.AWAY_MONEY_FIELDS)
        .forEach(function (f) { setReq(f, "none"); });

    if (receivesGames) {
        setReq("new_contractedhomegames", "required");
        if (hasAwayBenefits) setReq("new_contractedawaygames", "required");
    }

    if (hasClause && isPerGameRate) {
        setReq("new_investmentperhomegame", "required");
        if (hasAwayBenefits) setReq("new_investmentperawaygame", "required");
    }

    // When there is no clause the contract value must read zero, not a leftover.
    if (!hasClause) {
        var contractValueAttr = formContext.getAttribute("new_incrementalcontractvalue");
        if (contractValueAttr && contractValueAttr.getValue()) {
            contractValueAttr.setValue(0);
        }
    }
};

/**
 * Shows or hides a form section by name, searching every tab so the tab name
 * does not have to be hard-coded. Does nothing when the section is not on the
 * form, so the form works whether or not the notes were split into their own
 * section.
 * @param {object} formContext
 * @param {string} sectionName
 * @param {boolean} visible
 */
DealForm.setSectionVisibleByName = function (formContext, sectionName, visible) {
    try {
        formContext.ui.tabs.forEach(function (tab) {
            var section = tab.sections.get(sectionName);
            if (section) section.setVisible(visible);
        });
    } catch (e) {
        console.warn("DealForm.setSectionVisibleByName: " + e.message);
    }
};

/**
 * OnChange handler for Incremental Games Clause, Away Game Benefits
 * and Incremental Pricing Method.
 * @param {object} executionContext
 */
DealForm.onIncrementalGamesTriggerChange = function (executionContext) {
    DealForm.applyIncrementalGamesRules(executionContext);
    DealForm.calculateIncrementalContractValue(executionContext);
};

/**
 * Reads Home Games / Away Games from the Season the deal points to.
 *
 * The form has Season Home Games / Season Away Games, but those are formula
 * columns: Dataverse evaluates them server-side when the record is retrieved,
 * so on a record that has not been saved since the Season was set they read
 * zero. Going to the Season record directly gives the real numbers while the
 * user is still typing.
 *
 * The result is cached per Season so changing Contracted Home Games five times
 * in a row does not fire five requests.
 *
 * @param {object} formContext
 * @returns {Promise<{home: number, away: number}>}
 */
DealForm.getSeasonGames = function (formContext) {
    var cache = DealForm.IncrementalGames._seasonCache;
    var seasonAttr = formContext.getAttribute("new_season");
    var seasonVal = seasonAttr ? seasonAttr.getValue() : null;

    if (!seasonVal || seasonVal.length === 0) {
        return Promise.resolve({ home: 0, away: 0 });
    }

    var seasonId = seasonVal[0].id.replace(/[{}]/g, "").toLowerCase();
    if (cache.id === seasonId) {
        return Promise.resolve({ home: cache.home, away: cache.away });
    }

    return Xrm.WebApi.retrieveRecord("new_season", seasonId, "?$select=new_homegames,new_awaygames")
        .then(function (season) {
            cache.id = seasonId;
            cache.home = season.new_homegames || 0;
            cache.away = season.new_awaygames || 0;
            return { home: cache.home, away: cache.away };
        }, function (error) {
            console.warn("DealForm.getSeasonGames: " + error.message);
            return { home: 0, away: 0 };
        });
};

/**
 * Works out the incremental game counts client-side, with the same rule the
 * formula columns use server-side:
 *
 *   Incremental Home Games = Max(0, Season Home Games - Contracted Home Games)
 *   Incremental Away Games = Max(0, Season Away Games - Contracted Away Games)
 *
 * Away games count only when the contract carries away-game benefits.
 *
 * @param {object} formContext
 * @returns {Promise<{home: number, away: number, seasonHome: number, seasonAway: number}>}
 */
DealForm.getIncrementalGameCounts = function (formContext) {
    var getNumber = function (fieldName) {
        var attr = formContext.getAttribute(fieldName);
        var value = attr ? attr.getValue() : null;
        return (value === null || value === undefined) ? 0 : value;
    };

    return DealForm.getSeasonGames(formContext).then(function (season) {
        var awayAttr = formContext.getAttribute("new_awaygamebenefits");
        var hasAwayBenefits = (awayAttr && awayAttr.getValue() === true);

        var home = season.home - getNumber("new_contractedhomegames");
        var away = hasAwayBenefits ? (season.away - getNumber("new_contractedawaygames")) : 0;

        return {
            home: home > 0 ? home : 0,
            away: away > 0 ? away : 0,
            seasonHome: season.home,
            seasonAway: season.away
        };
    });
};

/**
 * Tells the user what the incremental counts are going to be while the formula
 * columns on the form still show the pre-save figures.
 *
 * Incremental Home Games and Incremental Away Games are formula columns. They
 * are recalculated when the record is read back from the server, not while the
 * form is open, so right after typing Contracted Home Games they still show the
 * old value (zero on a new record). Without this notification the form looks
 * broken. The notification clears itself as soon as the saved values catch up.
 *
 * @param {object} formContext
 * @param {{home: number, away: number}} counts
 */
DealForm.showIncrementalGamesPreview = function (formContext, counts) {
    var NOTIFICATION_ID = "incrementalGamesPreview";

    var getNumber = function (fieldName) {
        var attr = formContext.getAttribute(fieldName);
        var value = attr ? attr.getValue() : null;
        return (value === null || value === undefined) ? 0 : value;
    };

    var savedHome = getNumber("new_incrementalhomegames");
    var savedAway = getNumber("new_incrementalawaygames");

    if (savedHome === counts.home && savedAway === counts.away) {
        formContext.ui.clearFormNotification(NOTIFICATION_ID);
        return;
    }

    var message = "Incremental games for this contract: " + counts.home + " home";
    var awayAttr = formContext.getAttribute("new_awaygamebenefits");
    if (awayAttr && awayAttr.getValue() === true) {
        message += ", " + counts.away + " away";
    }
    message += ". The Incremental Home/Away Games fields are calculated by " +
               "Dataverse and will show these numbers once you save.";

    formContext.ui.setFormNotification(message, "INFO", NOTIFICATION_ID);
};

/**
 * Defaults Incremental Contract Value from the per-game investment and the
 * incremental game counts:
 *
 *   (Investment per Home Game x Incremental Home Games)
 * + (Investment per Away Game x Incremental Away Games)
 *
 * The counts are worked out from the Season rather than read from the formula
 * columns on the form, so the value is right on the first pass, before the
 * record has ever been saved.
 *
 * The field stays editable: this only fills it in so the seller confirms a
 * figure instead of working it out. It is left untouched when the pricing
 * method is not Per Game Rate, because in that case the amount comes from the
 * contract rather than from a rate, and it is never overwritten once the user
 * has typed something different from what the rates produce.
 *
 * @param {object} executionContext
 * @returns {Promise}
 */
DealForm.calculateIncrementalContractValue = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var cfg = DealForm.IncrementalGames;

    var clauseAttr = formContext.getAttribute("new_incrementalgamesclause");
    var methodAttr = formContext.getAttribute("new_incrementalpricingmethod");
    var targetAttr = formContext.getAttribute("new_incrementalcontractvalue");
    if (!clauseAttr || !targetAttr) return Promise.resolve();

    var clause = clauseAttr.getValue();
    var hasClause = (clause === cfg.CLAUSE_IN || clause === cfg.CLAUSE_OPT_IN);
    var receivesGames = (hasClause || clause === cfg.CLAUSE_OPT_OUT);

    if (!receivesGames) {
        formContext.ui.clearFormNotification("incrementalGamesPreview");
        return Promise.resolve();
    }

    return DealForm.getIncrementalGameCounts(formContext).then(function (counts) {
        DealForm.showIncrementalGamesPreview(formContext, counts);

        if (!hasClause) return;

        var method = methodAttr ? methodAttr.getValue() : null;
        if (method !== cfg.METHOD_PER_GAME_RATE) return;

        var getNumber = function (fieldName) {
            var attr = formContext.getAttribute(fieldName);
            var value = attr ? attr.getValue() : null;
            return (value === null || value === undefined) ? 0 : value;
        };

        var homeRate = getNumber("new_investmentperhomegame");
        var awayRate = getNumber("new_investmentperawaygame");

        if (homeRate === 0 && awayRate === 0) return;

        var contractValue = (homeRate * counts.home) + (awayRate * counts.away);
        contractValue = Math.round(contractValue * 100) / 100;

        targetAttr.setValue(contractValue);

        console.log("Incremental Contract Value -> home " + homeRate + " x " + counts.home +
                    " + away " + awayRate + " x " + counts.away + " = " + contractValue);
    });
};

/**
 * OnChange handler for every field that feeds the incremental calculation:
 * Contracted Home Games, Contracted Away Games, Investment per Home Game and
 * Investment per Away Game.
 * @param {object} executionContext
 */
DealForm.onIncrementalRecalc = function (executionContext) {
    DealForm.calculateIncrementalContractValue(executionContext);
};

/**
 * Kept as the original name so the existing OnChange registrations on
 * Investment per Home Game and Investment per Away Game keep working.
 * @param {object} executionContext
 */
DealForm.onIncrementalInvestmentChange = function (executionContext) {
    DealForm.onIncrementalRecalc(executionContext);
};

// ============================================================================
//  DISTRIBUTE INCREMENTAL REVENUE
//  Command bar button on the Deal form. Calls the new_DistributeIncrementalRevenue
//  Custom API, which spreads the Incremental Contract Value across the deal lines
//  flagged Include in Auto-Proration, in proportion to what each line is worth.
//
//  The whole calculation runs server-side in one transaction. This script only
//  confirms the intent, calls it, and reports back.
// ============================================================================

/**
 * Enable rule for the command. Keeps the button out of the way on deals where
 * there is nothing to distribute.
 * @param {object} primaryControl formContext, passed by the command bar
 * @returns {boolean}
 */
DealForm.canDistributeIncrementalRevenue = function (primaryControl) {
    try {
        var formContext = primaryControl;
        var cfg = DealForm.IncrementalGames;

        var clauseAttr = formContext.getAttribute("new_incrementalgamesclause");
        if (!clauseAttr) return false;

        var clause = clauseAttr.getValue();
        if (clause !== cfg.CLAUSE_IN && clause !== cfg.CLAUSE_OPT_IN) return false;

        var valueAttr = formContext.getAttribute("new_incrementalcontractvalue");
        var contractValue = valueAttr ? valueAttr.getValue() : null;

        return !!contractValue && contractValue > 0;
    } catch (e) {
        console.warn("canDistributeIncrementalRevenue: " + e.message);
        return false;
    }
};

/**
 * Command action. Saves any pending edits first, because the API reads the deal and
 * its lines from the database and would otherwise work from stale figures.
 * @param {object} primaryControl formContext, passed by the command bar
 */
DealForm.distributeIncrementalRevenue = function (primaryControl) {
    var formContext = primaryControl;

    var valueAttr = formContext.getAttribute("new_incrementalcontractvalue");
    var contractValue = valueAttr ? valueAttr.getValue() : 0;
    var formatted = DealForm.formatCurrency(contractValue);

    // The dialog does not grow to fit its text: anything past the fixed height is cut off with
    // no scrollbar the user would notice. Keep the copy short and the box tall enough for it.
    var confirmOptions = { height: 280, width: 500 };
    var confirmStrings = {
        title: "Distribute incremental revenue",
        text: formatted + " will be spread across the deal lines marked Include in " +
              "Auto-Proration, in proportion to what each line is worth.\n\n" +
              "The whole allocation is recalculated. Lines that are not marked are cleared.",
        confirmButtonLabel: "Distribute",
        cancelButtonLabel: "Cancel"
    };

    Xrm.Navigation.openConfirmDialog(confirmStrings, confirmOptions).then(function (result) {
        if (!result.confirmed) return;

        // The API rewrites every line of the deal, so on a large deal this takes a few seconds.
        // Without a progress indicator the form looks frozen and people click the button again.
        Xrm.Utility.showProgressIndicator("Distributing incremental revenue...");

        // Unsaved edits would not be visible to the API, which reads from the database.
        var save = formContext.data.getIsDirty()
            ? formContext.data.save()
            : Promise.resolve();

        save.then(function () {
            return DealForm.callDistributeApi(formContext.data.entity.getId());
        }).then(function (response) {
            return response.json();
        }).then(function (output) {
            // Closed before the dialog: a modal on top of the spinner leaves the user with
            // two overlays and no way to tell which one is waiting on them.
            Xrm.Utility.closeProgressIndicator();

            return Xrm.Navigation.openAlertDialog({
                title: "Incremental revenue distributed",
                text: output.Message
            }).then(function () {
                Xrm.Utility.showProgressIndicator("Refreshing the deal...");
                return formContext.data.refresh(false);
            }).then(function () {
                // The subgrids are refreshed after the record reloads, so the lines show the
                // amounts the plugin just wrote rather than what was on screen before.
                DealForm.refreshDealLinesGrid(formContext);
                Xrm.Utility.closeProgressIndicator();
            }).catch(function (refreshError) {
                // The distribution already succeeded. A refresh that fails is an inconvenience,
                // not an error worth an error dialog - say so on the form and let the user reload.
                Xrm.Utility.closeProgressIndicator();
                console.warn("Distribution succeeded but the refresh failed: " + refreshError.message);
                formContext.ui.setFormNotification(
                    "The revenue was distributed. Refresh the form to see the updated figures.",
                    "INFO",
                    "incrementalDistributeRefresh");
            });
        }).catch(function (error) {
            Xrm.Utility.closeProgressIndicator();
            Xrm.Navigation.openErrorDialog({
                message: error.message || "The distribution could not be completed.",
                details: error.raw || ""
            });
        });
    });
};

/**
 * Calls the bound Custom API new_DistributeIncrementalRevenue.
 * @param {string} dealId the deal's id, with or without braces
 * @param {boolean} [dryRun] calculate and report without writing
 * @returns {Promise}
 */
DealForm.callDistributeApi = function (dealId, dryRun) {
    var id = dealId.replace(/[{}]/g, "");

    // The Custom API is registered Global (unbound), so the deal travels as a parameter
    // rather than as a bound target.
    var request = {
        DealId: id,
        DryRun: dryRun === true,

        getMetadata: function () {
            return {
                boundParameter: null,
                parameterTypes: {
                    "DealId": { typeName: "Edm.String", structuralProperty: 1 },
                    "DryRun": { typeName: "Edm.Boolean", structuralProperty: 1 }
                },
                operationType: 0,           // 0 = Action
                operationName: "new_DistributeIncrementalRevenue"
            };
        }
    };

    return Xrm.WebApi.online.execute(request);
};

/**
 * Refreshes every subgrid on the form so the new amounts appear without reloading the page.
 *
 * Walks the whole control collection instead of looking up subgrids by name. Names differ per
 * form and are easy to rename in the designer; a lookup by name fails silently and leaves the
 * user staring at stale figures with no clue that anything went wrong.
 *
 * @param {object} formContext
 * @returns {number} how many subgrids were refreshed, for the console trace
 */
DealForm.refreshDealLinesGrid = function (formContext) {
    var refreshed = 0;

    try {
        formContext.getControl().forEach(function (control) {
            try {
                if (!control || typeof control.getControlType !== "function") return;
                if (control.getControlType() !== "subgrid") return;
                if (typeof control.refresh !== "function") return;

                control.refresh();
                refreshed++;
            } catch (inner) {
                console.warn("refreshDealLinesGrid: could not refresh a subgrid - " + inner.message);
            }
        });
    } catch (e) {
        console.warn("DealForm.refreshDealLinesGrid: " + e.message);
    }

    console.log("DealForm.refreshDealLinesGrid: " + refreshed + " subgrid(s) refreshed.");
    return refreshed;
};

/**
 * Formats a number as US currency for the confirmation dialog.
 * @param {number} value
 * @returns {string}
 */
DealForm.formatCurrency = function (value) {
    if (value === null || value === undefined) return "$0.00";
    try {
        return value.toLocaleString("en-US", { style: "currency", currency: "USD" });
    } catch (e) {
        return "$" + value;
    }
};


/**
 * Keeps the deal's own totals honest when a deal line is edited inline in a subgrid.
 *
 * Editing a line in the grid saves the line, and the plugin rolls the new figures up to the deal
 * server-side. The form never hears about it: the record changed underneath it, so Total,
 * Total Incremental Revenue and Unallocated Variance keep showing what they showed on load. The
 * user changes a rate, sees the line total move, and the variance above stays wrong.
 *
 * addOnSave only exists on editable grids, so this quietly does nothing on read-only ones.
 *
 * @param {object} executionContext
 */
DealForm.attachGridSaveHandlers = function (executionContext) {
    var formContext = executionContext.getFormContext();

    try {
        formContext.getControl().forEach(function (control) {
            try {
                if (!control || typeof control.getControlType !== "function") return;
                if (control.getControlType() !== "subgrid") return;
                if (typeof control.addOnSave !== "function") return; // not an editable grid

                control.addOnSave(function () {
                    DealForm.scheduleDealTotalsRefresh(formContext);
                });
            } catch (inner) {
                console.warn("attachGridSaveHandlers: " + inner.message);
            }
        });
    } catch (e) {
        console.warn("DealForm.attachGridSaveHandlers: " + e.message);
    }
};

/**
 * Reloads the deal a moment after a grid row is saved, so the rolled-up totals are read back.
 *
 * Debounced rather than immediate, for two reasons. The rollup happens in a post-operation plugin,
 * so the deal is only correct once that round trip finishes; and a user correcting three rates in
 * a row would otherwise trigger three reloads, each one fighting the next.
 *
 * @param {object} formContext
 */
DealForm.scheduleDealTotalsRefresh = function (formContext) {
    var DELAY_MS = 1500;

    if (DealForm._totalsRefreshTimer) {
        window.clearTimeout(DealForm._totalsRefreshTimer);
    }

    DealForm._totalsRefreshTimer = window.setTimeout(function () {
        DealForm._totalsRefreshTimer = null;

        // Never reload over the top of someone's unsaved edits on the deal itself. The totals
        // will catch up on their next save; losing typed work to a background refresh would not.
        if (formContext.data.getIsDirty()) {
            formContext.ui.setFormNotification(
                "The deal lines changed. Save this form to see the updated incremental totals.",
                "INFO",
                "incrementalTotalsStale");
            return;
        }

        formContext.data.refresh(false).then(function () {
            formContext.ui.clearFormNotification("incrementalTotalsStale");
            console.log("Deal totals refreshed after a grid save.");
        }, function (error) {
            console.warn("Could not refresh the deal totals after a grid save: " + error.message);
        });
    }, DELAY_MS);
};
