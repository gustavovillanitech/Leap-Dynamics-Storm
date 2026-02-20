var OpportunityForm = OpportunityForm || {};
var OpportunityFormCP = OpportunityFormCP || {};

// --- Cache initialization ---
// Ticketing / Original Namespace
OpportunityForm.allTicketOptions = [];
OpportunityForm.allProductTypeOptions = [];
OpportunityForm.allProductDetailOptions = [];
OpportunityForm.allSalesSourceOptions = [];
OpportunityForm.allLostReasonOptions = [];

// Corporate Partnerships (CP) Namespace
OpportunityFormCP.allOppTypeOptions = [];
OpportunityFormCP.allSalesStageOptions = [];
OpportunityFormCP.allLostReasonOptions = [];

// --- IDs Lost Reason (Corporate Partnership specific values) ---
var cpLostReasonIds = [
    100000016, // Budget
    100000017, // Timing
    100000018, // Invested with another team
    100000019, // ghosted/lost comms
    100000020, // Assets not available
    100000021  // Shifted marketing priorities
];

// --- OpportunityForm (Ticketing Logic) ---
OpportunityForm.onLoad = function(executionContext) {
    var formContext = executionContext.getFormContext();

    var ticketingCtrl = formContext.getControl("new_ticketingstage");
    if (ticketingCtrl) { ticketingCtrl.setDisabled(false); }

    var cacheOptions = function(attributeName) {
        var attr = formContext.getAttribute(attributeName);
        if (attr) {
            var options = attr.getOptions();
            return (options && options.length > 0) ? options.map(function(opt) {
                return { value: Number(opt.value), text: opt.text };
            }) : [];
        }
        return []; // Always return an empty array to avoid .length errors
    };

    OpportunityForm.allTicketOptions = cacheOptions("new_ticketingstage");
    OpportunityForm.allProductTypeOptions = cacheOptions("new_producttype");
    OpportunityForm.allProductDetailOptions = cacheOptions("new_producttypedetail");
    OpportunityForm.allSalesSourceOptions = cacheOptions("new_salessource");
    OpportunityForm.allLostReasonOptions = cacheOptions("new_lostreason");

    // SAFETY CHECK: Only wait for critical fields if they actually exist on the current form
    var needsWait = false;
    if (formContext.getAttribute("new_producttype") && OpportunityForm.allProductTypeOptions.length === 0) needsWait = true;
    if (formContext.getAttribute("new_lostreason") && OpportunityForm.allLostReasonOptions.length === 0) needsWait = true;
    
    if (needsWait) {
        setTimeout(function() { OpportunityForm.onLoad(executionContext); }, 300);
    } else {
        OpportunityForm.applyFilters(formContext);
    }
};

// --- OpportunityFormCP (Corporate Partnerships Logic) ---
OpportunityFormCP.onLoad = function(executionContext) {
    var formContext = executionContext.getFormContext();
    console.log("CP Logic Started");

    var cacheOptionsCP = function(attributeName) {
        var attr = formContext.getAttribute(attributeName);
        if (attr) {
            var options = attr.getOptions();
            return (options && options.length > 0) ? options.map(function(opt) {
                return { value: Number(opt.value), text: opt.text };
            }) : [];
        }
        return []; // Always return an empty array
    };

    OpportunityFormCP.setTopicPlaceholder(formContext);

    OpportunityFormCP.allOppTypeOptions = cacheOptionsCP("new_opportunitytype");
    OpportunityFormCP.allSalesStageOptions = cacheOptionsCP("new_salesstage");
    OpportunityFormCP.allLostReasonOptions = cacheOptionsCP("new_lostreason");

    var needsWaitCP = false;
    if (formContext.getAttribute("new_opportunitytype") && OpportunityFormCP.allOppTypeOptions.length === 0) needsWaitCP = true;

    if (needsWaitCP) {
        setTimeout(function() { OpportunityFormCP.onLoad(executionContext); }, 300);
    } else {
        console.log("Applying CP Filters...");
        OpportunityFormCP.applyFiltersCP(formContext);
        OpportunityFormCP.toggleLostReasonVisibilityCP(executionContext);
        OpportunityFormCP.setRequiredFieldsCP(executionContext);
    }
};

// --- Event Handlers ---
OpportunityForm.onControllingFieldChange = function(executionContext) {
    var formContext = executionContext.getFormContext();
    OpportunityForm.applyFilters(formContext);
};

OpportunityFormCP.onControllingFieldChange = function(executionContext) {
    var formContext = executionContext.getFormContext();
    OpportunityFormCP.applyFiltersCP(formContext);
};

OpportunityFormCP.onSalesStageChange = function(executionContext) {
    OpportunityFormCP.toggleLostReasonVisibilityCP(executionContext);
};

// --- Orchestrators ---
OpportunityForm.applyFilters = function(formContext) {
    OpportunityForm.filterProductType(formContext);
    OpportunityForm.filterSalesSource(formContext);
    OpportunityForm.filterLostReason(formContext);
    OpportunityForm.filterTicketingStage(formContext);
    OpportunityForm.filterProductDetail(formContext);
};

OpportunityFormCP.applyFiltersCP = function(formContext) {
    OpportunityFormCP.filterOpportunityTypeByAppCP(formContext);
    OpportunityFormCP.filterSalesStageCP(formContext);
    OpportunityFormCP.filterLostReasonCP(formContext);
};

// --- Filtering Methods ---
OpportunityForm.filterLostReason = function(formContext) {
    var ctrl = formContext.getControl("new_lostreason");
    if (!ctrl || OpportunityForm.allLostReasonOptions.length === 0) return;
    var originalToHide = [100000015, 100000013, 100000012, 100000006, 100000011, 100000014];

    // Combine original exclusions with the CP global list
    var combinedToHide = originalToHide.concat(cpLostReasonIds);

    ctrl.clearOptions();
    OpportunityForm.allLostReasonOptions.forEach(function(o) {
        if (combinedToHide.indexOf(o.value) === -1) ctrl.addOption(o);
    });
};

OpportunityForm.filterSalesSource = function(formContext) {
    var ctrl = formContext.getControl("new_salessource");
    if (!ctrl || OpportunityForm.allSalesSourceOptions.length === 0) return;
    var toHide = [100000010, 100000000, 100000001, 100000008];
    ctrl.clearOptions();
    OpportunityForm.allSalesSourceOptions.forEach(function(o) {
        if (toHide.indexOf(o.value) === -1) ctrl.addOption(o);
    });
};

OpportunityForm.filterProductType = function(formContext) {
    var ctrl = formContext.getControl("new_producttype");
    if (!ctrl || OpportunityForm.allProductTypeOptions.length === 0) return;
    var toHide = [100000000, 100000003, 100000006, 100000004, 100000005, 100000009];
    ctrl.clearOptions();
    OpportunityForm.allProductTypeOptions.forEach(function(o) {
        if (toHide.indexOf(o.value) === -1) ctrl.addOption(o);
    });
};

OpportunityForm.filterTicketingStage = function(formContext) {
    var attr = formContext.getAttribute("new_opportunitytype");
    var ctrl = formContext.getControl("new_ticketingstage");
    if (!attr || !ctrl || OpportunityForm.allTicketOptions.length === 0) return;
    var type = attr.getValue();
    
    var map = {
        100000000: [100000000, 100000001, 100000002, 100000003, 100000004, 100000005, 100000006],
        100000001: [100000000, 100000001, 100000002, 100000007, 100000005, 100000006],
        100000004: [100000000, 100000010, 100000011, 100000012, 100000013, 100000005, 100000006],
        100000005: [100000000, 100000014, 100000015, 100000016, 100000017, 100000018, 100000019, 100000020, 100000021, 100000022, 100000006],
        100000002: [100000000, 100000001, 100000002, 100000008, 100000009, 100000023, 100000024, 100000025, 100000026, 100000027, 100000028, 100000029, 100000030]
    };
    
    ctrl.clearOptions();
    var allowed = map[type] || [];
    OpportunityForm.allTicketOptions.forEach(function(o) {
        if (type === null || allowed.indexOf(o.value) > -1) ctrl.addOption(o);
    });
};

OpportunityForm.setRequiredFields = function(formContext) {
    var oppTypeAttr = formContext.getAttribute("new_opportunitytype");
    var prodTypeAttr = formContext.getAttribute("new_producttype");
    var stageAttr = formContext.getAttribute("new_ticketingstage");
    var detailAttr = formContext.getAttribute("new_producttypedetail");

    if (!oppTypeAttr || !prodTypeAttr || !stageAttr || !detailAttr) return;

    var oppType = oppTypeAttr.getValue();
    var prodType = prodTypeAttr.getValue();
    var stage = stageAttr.getValue();
    var isRequired = false;

    // RULE 1: Ticketing - New FSE (100000000)
    // Mandatory ONLY when Product is Deposit (100000008)
    // Partial (100000010) and Flex (100000011) removed per request
    if (oppType === 100000000) {
        var prodTypesR1 = [100000008]; 
        var stagesR1 = [100000003, 100000004, 100000005];
        
        if (prodTypesR1.indexOf(prodType) > -1 && stagesR1.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    // RULE 2: Ticketing - Groups (100000001)
    if (!isRequired && oppType === 100000001) {
        var stagesR2 = [100000007, 100000005];
        if (prodType === 100000001 && stagesR2.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    // RULE 3: Ticketing - New FSE (100000000) + Premium Hospitality (100000012)
    if (!isRequired && oppType === 100000000 && prodType === 100000012) {
        var stagesR3 = [100000011, 100000012, 100000013, 100000005];
        if (stagesR3.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    detailAttr.setRequiredLevel(isRequired ? "required" : "none");
};

OpportunityForm.filterProductDetail = function(formContext) {
    var attr = formContext.getAttribute("new_producttype");
    var detAttr = formContext.getAttribute("new_producttypedetail");
    var ctrl = formContext.getControl("new_producttypedetail");
    
    if (!attr || !ctrl || OpportunityForm.allProductDetailOptions.length === 0) return;
    
    var type = attr.getValue();
    
    var map = {
        100000008: [100000000, 100000001, 100000002], 
        100000001: [100000003, 100000004, 100000005], 
        100000012: [100000006, 100000007, 100000008, 100000009, 100000010, 100000011, 100000012], 
        100000010: [100000013, 100000014, 100000015, 100000016, 100000017], 
        100000011: [100000018, 100000019] 
    };
    
    ctrl.clearOptions();
    var allowed = map[type] || [];
    
    OpportunityForm.allProductDetailOptions.forEach(function(o) {
        if (allowed.indexOf(o.value) > -1) ctrl.addOption(o);
    });
    
    if (detAttr.getValue() !== null && allowed.indexOf(detAttr.getValue()) === -1) {
        detAttr.setValue(null);
    }

    // Run the requirement logic check
    OpportunityForm.setRequiredFields(formContext);
};

OpportunityForm.onSave = function(executionContext) {
    var formContext = executionContext.getFormContext();
    
    // Refresh Timeline
    OpportunityForm.refreshTimeline(formContext);
};

OpportunityForm.refreshTimeline = function(formContext) {
    setTimeout(function () {
        var timelineControl = formContext.getControl("Timeline");
        if (timelineControl) {
            timelineControl.refresh();
        }
    }, 2500);
};

// Filter Sales Stage based on Opportunity Type
OpportunityFormCP.filterSalesStageCP = function(formContext) {
    var attr = formContext.getAttribute("new_opportunitytype");
    var ctrl = formContext.getControl("new_salesstage");
    
    if (!attr || !ctrl || OpportunityFormCP.allSalesStageOptions.length === 0) return;
    
    var type = attr.getValue();
    
    // Mapping of Opportunity Type to allowed Sales Stage values
    var map = {
        // Corporate Partnership - Prospect
        100000003: [
            100000000, // 01 - Prospect
            100000010, // 02 - Discovery Meeting
            100000011, // 03 - Define Objectives
            100000012, // 04 - Idea Generation – Internal
            100000013, // 05 - Idea Generation – External
            100000027, // 06 - Ready for Proposal
            100000001, // 07 - Pitched
            100000015, // 08 - Follow up / Negotiation
            100000016, // 09 - Verbal / At Contract
            100000003, // 10 - Closed
            100000004  // 11 - Declined
        ],
        // Corporate Partnership - Current
        100000006: [
            100000019, // 01 - Tip-off Meeting
            100000008, // 02 - Activation
            100000021, // 03 - Mid-season Recap
            100000022, // 04 - Playoff Option / Letter
            100000006, // 05 - Recap
            100000024, // 06 - Agreement Option
            100000007, // 07 - Renewal
            100000025, // 08 - Upsell
            100000026, // 09 - Verbal / At Contract
            100000017, // 10 - Closed
            100000018  // 11 - Declined
        ]
    };
    
    ctrl.clearOptions();
    //If no type is selected, keep it disabled and empty
    if (type === null) {
        ctrl.setDisabled(true);
        formContext.getAttribute("new_salesstage").setValue(null);
    } else {
        // Enable and show allowed options
        ctrl.setDisabled(false);
        var allowed = map[type];
        OpportunityFormCP.allSalesStageOptions.forEach(function(o) {
            if (allowed && allowed.indexOf(o.value) > -1) {
                ctrl.addOption(o);
            }
        });
    }
};

// Filter Opportunity Type based on the Model-Driven App
OpportunityFormCP.filterOpportunityTypeByAppCP = function(formContext) {
    var ctrl = formContext.getControl("new_opportunitytype");
    if (!ctrl || OpportunityFormCP.allOppTypeOptions.length === 0) return;

    // Get the Global Context to check the App Unique Name
    var globalContext = Xrm.Utility.getGlobalContext();
    
    globalContext.getCurrentAppProperties().then(function (appProperties) {

    // --- DEBUG: Revisa este valor en la consola del navegador (F12) ---
        console.log("Current App Unique Name: " + appProperties.uniqueName);

        // Check if the current app is "new_CorporatePartnerships"
        if (appProperties.uniqueName === "new_CorporatePartnerships") {
            var allowedTypes = [100000003, 100000006]; // Prospect and Current
            
            ctrl.clearOptions();
            
            OpportunityFormCP.allOppTypeOptions.forEach(function (o) {
                if (allowedTypes.indexOf(o.value) > -1) {
                    ctrl.addOption(o);
                }
            });
        }
    }, function (error) {
        console.error("Error retrieving app properties: " + error.message);
    });
};


//Method for Corporate Partnerships logic, shows ONLY the CP-specific Lost Reasons.
OpportunityFormCP.filterLostReasonCP = function(formContext) {
    var ctrl = formContext.getControl("new_lostreason");
    if (!ctrl || OpportunityFormCP.allLostReasonOptions.length === 0) return;

    ctrl.clearOptions();
    OpportunityFormCP.allLostReasonOptions.forEach(function(o) {
        // Show ONLY what is in the CP global list
        if (cpLostReasonIds.indexOf(o.value) > -1) {
            ctrl.addOption(o);
        }
    });
};

// Method to handle placeholder, managing null Account values
OpportunityFormCP.setTopicPlaceholder = function(formContext) {
    // 1 = Create (New Record), we only act on new ones!
    if (formContext.ui.getFormType() !== 1) return;

    var topicAttr = formContext.getAttribute("name");
    var accountAttr = formContext.getAttribute("parentaccountid");
    
    // Safety check: Does the attribute exist in this form?
    if (!topicAttr || !accountAttr) return;

    var accountValue = accountAttr.getValue();

    // Case 1: Account is selected
    if (accountValue && accountValue.length > 0) {
        var accountName = accountValue[0].name;
        
        // Only overwrite if it's empty or has the generic "New Opportunity" text
        if (!topicAttr.getValue() || topicAttr.getValue() === "New Opportunity") {
            topicAttr.setValue(accountName); 
        }
    } 
    // Case 2: Account is NULL
    else {
        // Optional: Set a generic text so the "Required" validation is met
        if (!topicAttr.getValue()) {
            topicAttr.setValue("Draft: Corporate Partnership");
        }
    }
};

OpportunityFormCP.onSave = function(executionContext) {
    var formContext = executionContext.getFormContext();
    
    // Refresh Timeline
    OpportunityForm.refreshTimeline(formContext);

    // Refresh the record to fetch the Flow's generated name
    // We wait 3 seconds to let the Flow finish
    setTimeout(function () {
        formContext.data.refresh(false).then(
            function() { console.log("Form refreshed with Flow data."); },
            function(error) { console.error("Error refreshing: " + error.message); }
        );
    }, 2000); 
};


// Manages the visibility and requirement level of the Lost Reason field 
// based on the Sales Stage specific to the Corporate Partnerships app.
OpportunityFormCP.toggleLostReasonVisibilityCP = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var globalContext = Xrm.Utility.getGlobalContext();

    globalContext.getCurrentAppProperties().then(function (appProperties) {
        // Check if we are inside the Corporate Partnerships App
        if (appProperties.uniqueName === "new_CorporatePartnerships") {
            var salesStageAttr = formContext.getAttribute("new_salesstage");
            var lostReasonAttr = formContext.getAttribute("new_lostreason");
            var lostReasonCtrl = formContext.getControl("new_lostreason");

            if (salesStageAttr && lostReasonAttr && lostReasonCtrl) {
                var stageValue = salesStageAttr.getValue();
                
                // 100000004 = 10 - Declined (Prospect)
                // 100000018 = 11 - Declined (Current)
                var isDeclined = (stageValue === 100000004 || stageValue === 100000018);
                
                // Toggle visibility based on the result
                lostReasonCtrl.setVisible(isDeclined);

                // Set requirement level: "required" if visible, "none" if hidden
                lostReasonAttr.setRequiredLevel(isDeclined ? "required" : "none");
                
                // Optional: Clear value if hidden to maintain data integrity
                if (!isDeclined) {
                    lostReasonAttr.setValue(null);
                }
            }
        }
    }, function (error) {
        console.error("Error identifying app for visibility/requirement logic: " + error.message);
    });
};

// Makes specific fields mandatory ONLY when inside the Corporate Partnerships App
OpportunityFormCP.setRequiredFieldsCP = function (executionContext) {
    var formContext = executionContext.getFormContext();
    var globalContext = Xrm.Utility.getGlobalContext();

    globalContext.getCurrentAppProperties().then(function (appProperties) {
        // Check if we are inside the Corporate Partnerships App
        if (appProperties.uniqueName === "new_CorporatePartnerships") {
            
            // Mandatory fields for CP Opportunities
            var fieldsToRequire = [
                "parentaccountid",       // Account
                "new_leadsource",        // Lead Source
                "new_basketballseason",  // Season
                "new_opportunitytype",   // Opportunity Type
                "new_salesstage"         // Sales Stage
            ];

            // iterate and set required level
            fieldsToRequire.forEach(function(fieldName) {
                var attr = formContext.getAttribute(fieldName);
                if (attr) {
                    attr.setRequiredLevel("required");
                }
            });
        }
    }, function (error) {
        console.error("Error identifying app for required CP fields logic: " + error.message);
    });
};