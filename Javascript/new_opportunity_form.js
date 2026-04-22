var OpportunityForm = OpportunityForm || {};
var OpportunityFormCP = OpportunityFormCP || {};

// --- Cache initialization ---
// Ticketing / Original Namespace
OpportunityForm.allTicketOptions = [];
OpportunityForm.allProductTypeOptions = [];
OpportunityForm.allProductDetailOptions = [];
OpportunityForm.allSalesSourceOptions = [];
OpportunityForm.allLostReasonOptions = [];
OpportunityForm.allOppTypeOptions = [];

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

    OpportunityForm.allOppTypeOptions = cacheOptions("new_opportunitytype");
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
    OpportunityForm.setRequiredFields(formContext);
};

OpportunityFormCP.onControllingFieldChange = function(executionContext) {
    var formContext = executionContext.getFormContext();
    OpportunityFormCP.applyFiltersCP(formContext);
    // Validate required fields on Opportunity Type change
    OpportunityFormCP.setRequiredFieldsCP(executionContext);
};

OpportunityFormCP.onSalesStageChange = function(executionContext) {
    OpportunityFormCP.toggleLostReasonVisibilityCP(executionContext);
    // Validate required fields on Sales Stage change
    OpportunityFormCP.setRequiredFieldsCP(executionContext);
};

// --- Auto-generate CP Topic Name  ---
OpportunityFormCP.onDependencyChangeForTopic = function(executionContext) {
    var formContext = executionContext.getFormContext();
    
    // Is App de Corporate Partnerships?
    var globalContext = Xrm.Utility.getGlobalContext();
    globalContext.getCurrentAppProperties().then(function (appProperties) {
        if (appProperties.uniqueName === "new_CorporatePartnerships") {
            OpportunityFormCP.generateTopicName(formContext);
        }
    });
};

// --- Orchestrators ---
OpportunityForm.applyFilters = function(formContext) {
    OpportunityForm.filterOpportunityTypeByApp(formContext);
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
        100000000: [100000000, 100000001, 100000002, 100000003, 100000004, 100000005, 100000006], //Ticketing - New FSE
        100000001: [100000000, 100000001, 100000002, 100000007, 100000005, 100000006], //Ticketing - Groups
        100000004: [100000000, 100000010, 100000011, 100000012, 100000013, 100000005, 100000006], //Ticketing - Premium Sales
        100000005: [100000000, 100000014, 100000015, 100000016, 100000017, 100000018, 100000019, 100000020, 100000021, 100000022, 100000006], //Ticketing - Premium Service
        100000002: [100000031, 100000032, 100000033, 100000044, 100000045, 100000034, 100000037, 100000046, 100000039, 100000040, 100000036, 100000041, 100000042, 100000043], //Ticketing - Service
        100000007: [100000031, 100000032, 100000033, 100000044, 100000045, 100000034, 100000037, 100000046, 100000039, 100000040, 100000036, 100000041, 100000042, 100000043] //Ticketing - Service
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
    var prevCallAttr = formContext.getAttribute("new_previousphonecallguid");

    // Validate core attributes needed for decision making
    if (!oppTypeAttr || !prodTypeAttr || !stageAttr) return;

    var oppType = oppTypeAttr.getValue();
    var prodType = prodTypeAttr.getValue();
    var stage = stageAttr.getValue();
    var prevCall = prevCallAttr ? prevCallAttr.getValue() : null;

    // ---------------------------------------------------------
    // RULES FOR: 'Product Type Detail' (new_producttypedetail)
    // ---------------------------------------------------------
    var detailAttr = formContext.getAttribute("new_producttypedetail");
    if (detailAttr) {
        var isDetailRequired = false;

        // RULE 1: Ticketing - New FSE (100000000)
        if (oppType === 100000000) {
            var prodTypesR1 = [100000008]; 
            var stagesR1 = [100000003, 100000004, 100000005];
            if (prodTypesR1.indexOf(prodType) > -1 && stagesR1.indexOf(stage) > -1) {
                isDetailRequired = true;
            }
        }
        // RULE 2: Ticketing - Groups (100000001)
        if (!isDetailRequired && oppType === 100000001) {
            var stagesR2 = [100000007, 100000005];
            if (prodType === 100000001 && stagesR2.indexOf(stage) > -1) {
                isDetailRequired = true;
            }
        }
        // RULE 3: Ticketing - New FSE (100000000) + Premium Hospitality (100000012)
        if (!isDetailRequired && oppType === 100000000 && prodType === 100000012) {
            var stagesR3 = [100000011, 100000012, 100000013, 100000005];
            if (stagesR3.indexOf(stage) > -1) {
                isDetailRequired = true;
            }
        }

        detailAttr.setRequiredLevel(isDetailRequired ? "required" : "none");
    }

    // ---------------------------------------------------------
    // RULES FOR: 'Section' (new_section)
    // ---------------------------------------------------------
    var sectionAttr = formContext.getAttribute("new_section");
    if (sectionAttr) {
        var isSectionRequired = false;

        // RULE 4: Ticketing - Premium Sales (100000004)
        // Stage "2-Premium Pitched" (100000011) and later
        var premiumStages = [100000011, 100000012, 100000013, 100000005]; 
        if (oppType === 100000004 && premiumStages.indexOf(stage) > -1) {
            isSectionRequired = true;
        }

        // RULE 6
        // IF Opp Type is New FSE (100000000) or Service (100000002)
        // AND Stage is Closed Won (100000005) or 11-Closed-Auto Renewed (100000029)
        // AND Previous Phone Call GUID is empty
        // AND Product Type is NOT FSE-Flexible Plans (100000011)
        if (!isSectionRequired) {
            var oppTypesBR = [100000000, 100000002];
            var stagesBR = [100000005, 100000029];
            var isPrevCallEmpty = (prevCall === null || prevCall === "");
            
            if (oppTypesBR.indexOf(oppType) > -1 && 
                stagesBR.indexOf(stage) > -1 && 
                isPrevCallEmpty && 
                prodType !== 100000011) {
                isSectionRequired = true;
            }
        }

        sectionAttr.setRequiredLevel(isSectionRequired ? "required" : "none");
    }

    // ---------------------------------------------------------
    // RULE 5 FOR: 'Reason for Buying' (new_reasonforbuying)
    // ---------------------------------------------------------
    var reasonForBuyingAttr = formContext.getAttribute("new_reasonforbuying");
    if (reasonForBuyingAttr) {
        if (oppType === 100000001 && prodType === 100000001 && stage === 100000005) {
            reasonForBuyingAttr.setRequiredLevel("required");
        } else {
            reasonForBuyingAttr.setRequiredLevel("none");
        }
    }
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
        100000012: [100000006, 100000008, 100000009, 100000011, 100000012, 100000020, 100000021, 100000022, 100000023, 100000024, 100000025], //new_producttype = Premium Hospitality
        100000010: [100000013, 100000014, 100000015, 100000016, 100000017, 100000026], //FSE - Partial Plans
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

// Filter Opportunity Type based on the Model-Driven App
OpportunityForm.filterOpportunityTypeByApp = function(formContext) {
    var ctrl = formContext.getControl("new_opportunitytype");
    if (!ctrl || OpportunityForm.allOppTypeOptions.length === 0) return;

    // Get the Global Context to check the App Unique Name
    var globalContext = Xrm.Utility.getGlobalContext();
    
    globalContext.getCurrentAppProperties().then(function (appProperties) {

        console.log("Current App Unique Name: " + appProperties.uniqueName);

        // Check if the current app is not "new_CorporatePartnerships"
        if (appProperties.uniqueName !== "new_CorporatePartnerships") {
            var allowedTypes = [100000000, 100000001,100000004,100000002,100000005,100000007]; // Ticketing - New FSE, Ticketing - Groups, Ticketing - Premium Sales, Ticketing - Service, Ticketing - Premium Service, Ticketing - Membership Renewal
            
            ctrl.clearOptions();
            
            OpportunityForm.allOppTypeOptions.forEach(function (o) {
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

// --- Custom Loading Screen for Sync Close Process ---
OpportunityFormCP.onSave = function(executionContext) {
    var formContext = executionContext.getFormContext();
    var salesStageAttr = formContext.getAttribute("new_salesstage");
    var isClosing = false;

    // Check if the Sales Stage field was modified during this transaction
    if (salesStageAttr && salesStageAttr.getIsDirty()) {
        var salesStage = salesStageAttr.getValue();

        // Check if the stage is being set to a WON scenario (100000017: Current, 100000003: Prospect)
        if (salesStage === 100000017 || salesStage === 100000003) {
            isClosing = true;
            var willClone = false;
            
            // Retrieve fields needed to evaluate the cloning logic
            var oppTypeAttr = formContext.getAttribute("new_opportunitytype");
            var contractLengthAttr = formContext.getAttribute("new_pitchedcontractlength");
            
            if (oppTypeAttr && contractLengthAttr) {
                var oppType = oppTypeAttr.getValue();
                var contractLength = contractLengthAttr.getValue();
                
                // Logic matching the C# Plugin
                if ((oppType === 100000003 || oppType === 100000006) && contractLength > 100000000) {
                    willClone = true;
                }
            }

            // Display dynamic message
            if (willClone) {
                Xrm.Utility.showProgressIndicator("Closing Opportunity & Cloning Future Years. This may take a few seconds...");
            } else {
                Xrm.Utility.showProgressIndicator("Closing Opportunity...");
            }
            
            // Register the post-save event to handle cleanup and safe data refresh
            formContext.data.entity.addOnPostSave(OpportunityFormCP.postSave);
        }
    }

    // IF NOT CLOSING: We run your original 2-second timeout refresh
    // This handles normal saves where we are just waiting for a quick background Flow
    if (!isClosing) {
        setTimeout(function () {
            formContext.data.refresh(false).then(
                function() { console.log("Form refreshed with Flow data (Normal Save)."); },
                function(error) { console.error("Error refreshing: " + error.message); }
            );
        }, 2000); 
    }

    // Always Refresh Timeline
    OpportunityForm.refreshTimeline(formContext);
};

// Callback to hide the progress indicator and refresh data AFTER the server responds
OpportunityFormCP.postSave = function(executionContext) {
    var formContext = executionContext.getFormContext();

    // 1. Hide the loading screen immediately so the user can see any native Plugin errors
    Xrm.Utility.closeProgressIndicator();
    
    // 2. Remove the event listener to avoid duplicate triggers
    formContext.data.entity.removeOnPostSave(OpportunityFormCP.postSave);

    // 3. SAFE REFRESH LOGIC: 
    // If the Sync Plugin throws an exception, the save is aborted and the form remains "Dirty" (unsaved).
    // Attempting to refresh a dirty form triggers the native Dynamics "Unsaved changes" pop-up.
    // We must ONLY refresh if the form is NOT dirty (meaning the save was 100% successful).
    if (!formContext.data.entity.getIsDirty()) {
        formContext.data.refresh(false).then(
            function() { console.log("Form refreshed successfully after Sync Plugin finished."); },
            function(error) { console.error("Error refreshing post-save: " + error.message); }
        );
    } else {
        console.warn("Save aborted (likely due to Plugin Exception). Skipping form refresh to prevent UI conflicts.");
    }
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
            
            // Helper function to safely set requirement levels
            var setReq = function(fieldName, level) {
                var attr = formContext.getAttribute(fieldName);
                if (attr) {
                    attr.setRequiredLevel(level);
                }
            };

            // 1. BASE FIELDS (Always required regardless of stage or type)
            var baseFields = [
                "new_opportunitytype", "parentaccountid", "new_salesstage", 
                "new_leadsource", "new_basketballseason"
            ];
            baseFields.forEach(function(f) { setReq(f, "required"); });

            // 2. CONDITIONAL FIELDS (Clear them first to prevent them from getting stuck if the user moves back a stage)
            // ADDED "new_escalator" so it resets if contract length is changed back to 1 year
            var conditionalFields = [
                "parentcontactid", "campaignid", "budgetstatus", "new_pitchtype", "new_pitchdate",
                "new_pitchedcontractlength", "new_confidencelevel", "estimatedclosedate", "estimatedvalue",
                "new_escalator" 
            ];
            conditionalFields.forEach(function(f) { setReq(f, "none"); });

            // 3. GET CURRENT VALUES
            var oppTypeAttr = formContext.getAttribute("new_opportunitytype");
            var stageAttr = formContext.getAttribute("new_salesstage");
            var contractLengthAttr = formContext.getAttribute("new_pitchedcontractlength"); // Added Contract Length
            
            var oppType = oppTypeAttr ? oppTypeAttr.getValue() : null;
            var stage = stageAttr ? stageAttr.getValue() : null;
            var contractLength = contractLengthAttr ? contractLengthAttr.getValue() : null;

            // 4. MATRIX LOGIC
            
            // --- MATRIX: 100000003 (Corporate Partnership - Prospect) ---
            if (oppType === 100000003) {
                // For Prospect, Contact is only required once a stage is selected (not 'When Creating')
                if (stage !== null) {
                    setReq("parentcontactid", "required");

                    // Budget (Required in stages 06, 07, 08, 09)
                    if ([100000027, 100000001, 100000015, 100000016].indexOf(stage) > -1) {
                        setReq("budgetstatus", "required");
                    }
                    
                    // Pitch Type (Required in stages 06, 07, 08, 09, 10)
                    if ([100000027, 100000001, 100000015, 100000016, 100000003].indexOf(stage) > -1) {
                        setReq("new_pitchtype", "required");
                    }
                    
                    // Pitch Date, Contract Length, Confidence Level, Est. Revenue (Required in stages 07, 08, 09, 10)
                    if ([100000001, 100000015, 100000016, 100000003].indexOf(stage) > -1) {
                        setReq("new_pitchdate", "required");
                        setReq("new_pitchedcontractlength", "required");
                        setReq("new_confidencelevel", "required");
                        setReq("estimatedvalue", "required");
                    }

                    // Est. Close Date (Required in stages 07, 08, 09, 10, 11)
                    if ([100000001, 100000015, 100000016, 100000003, 100000004].indexOf(stage) > -1) {
                        setReq("estimatedclosedate", "required");
                    }
                }
            }
            
            // --- MATRIX: 100000006 (Corporate Partnership - Current) ---
            else if (oppType === 100000006) {
                // For Current, Contact and Source Campaign are ALWAYS required (even 'When Creating')
                setReq("parentcontactid", "required");

                if (stage !== null) {
                    // Stages 07, 08, 09, and 10 require all 7 fields in the matrix
                    // 100000007 (07), 100000025 (08), 100000026 (09), 100000017 (10)
                    if ([100000007, 100000025, 100000026, 100000017].indexOf(stage) > -1) {
                        setReq("budgetstatus", "required");
                        setReq("new_pitchtype", "required");
                        setReq("new_pitchdate", "required");
                        setReq("new_pitchedcontractlength", "required");
                        setReq("new_confidencelevel", "required");
                        setReq("estimatedclosedate", "required");
                        setReq("estimatedvalue", "required");
                    }
                }
            }

            // 5. ESCALATOR LOGIC (Multi-Year Deals)
            // If Stage is Closed (100000003 for Prospect, 100000017 for Current) AND Contract Length > 1 year
            if (stage === 100000003 || stage === 100000017) {
                if (contractLength !== null && contractLength > 100000000) {
                    setReq("new_escalator", "required");
                }
            }
        }
    }, function (error) {
        console.error("Error identifying app for required CP fields logic: " + error.message);
    });
};

//Generates CP Topic Name in the format "[Account Name] - [Season]", only if the form is in the Corporate Partnerships app and when either the Account or Season fields change. It also handles null values gracefully.
OpportunityFormCP.generateTopicName = function(formContext) {
    var accountAttr = formContext.getAttribute("parentaccountid");
    var seasonAttr = formContext.getAttribute("new_basketballseason");
    var topicAttr = formContext.getAttribute("name");

    // Validate that the required fields exist on the form
    if (!accountAttr || !seasonAttr || !topicAttr) return;

    var accountVal = accountAttr.getValue();
    var seasonVal = seasonAttr.getValue();

    var accountName = (accountVal && accountVal.length > 0) ? accountVal[0].name : "";
    var seasonName = (seasonVal && seasonVal.length > 0) ? seasonVal[0].name : "";

    // Build the name "[Account Name] - [Season]"
    var newTopic = "";
    if (accountName && seasonName) {
        newTopic = accountName + " - " + seasonName;
    } else if (accountName) {
        newTopic = accountName;
    } else if (seasonName) {
        newTopic = seasonName;
    }

    // Update the Topic field in real-time (the user will see it change and can still edit it manually)
    if (newTopic !== "") {
        topicAttr.setValue(newTopic);
    }
};