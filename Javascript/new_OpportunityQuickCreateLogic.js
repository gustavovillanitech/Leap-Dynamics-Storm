var OppQuickCreate = OppQuickCreate || {};

// Caches for the Quick Create form fields
OppQuickCreate.allTicketOptions = [];
OppQuickCreate.allProductTypeOptions = [];
OppQuickCreate.allProductDetailOptions = [];
OppQuickCreate.allSalesSourceOptions = [];

/**
 * Form OnLoad handler
 */
OppQuickCreate.onLoad = function(executionContext) {
    var formContext = executionContext.getFormContext();

    // Helper to cache options safely
    var cacheOptions = function(attributeName) {
        var attr = formContext.getAttribute(attributeName);
        if (attr) {
            var options = attr.getOptions();
            return (options && options.length > 0) ? options.map(function(opt) {
                return { value: Number(opt.value), text: opt.text };
            }) : [];
        }
        return [];
    };

    // 1. Cache all relevant fields
    OppQuickCreate.allTicketOptions = cacheOptions("new_ticketingstage");
    OppQuickCreate.allProductTypeOptions = cacheOptions("new_producttype");
    OppQuickCreate.allProductDetailOptions = cacheOptions("new_producttypedetail");
    OppQuickCreate.allSalesSourceOptions = cacheOptions("new_salessource");

    // 2. Apply all filters immediately
    OppQuickCreate.applyFilters(formContext);
};

/**
 * OnChange handler for Opportunity Type and Product Type
 */
OppQuickCreate.onControllingFieldChange = function(executionContext) {
    var formContext = executionContext.getFormContext();
    OppQuickCreate.applyFilters(formContext);
};

/**
 * Master function to run all filtering logic
 */
OppQuickCreate.applyFilters = function(formContext) {
    OppQuickCreate.filterProductType(formContext);
    OppQuickCreate.filterSalesSource(formContext);
    OppQuickCreate.filterTicketingStage(formContext);
    OppQuickCreate.filterProductDetail(formContext);
};

/**
 * Filter Sales Source
 */
OppQuickCreate.filterSalesSource = function(formContext) {
    var ctrl = formContext.getControl("new_salessource");
    if (!ctrl) return;

    var toHide = [100000010, 100000000, 100000001, 100000008];
    ctrl.clearOptions();

    OppQuickCreate.allSalesSourceOptions.forEach(function(opt) {
        if (toHide.indexOf(opt.value) === -1) {
            ctrl.addOption(opt);
        }
    });
};

/**
 * Filter Product Type
 */
OppQuickCreate.filterProductType = function(formContext) {
    var ctrl = formContext.getControl("new_producttype");
    if (!ctrl) return;

    var toHide = [100000000, 100000003, 100000006, 100000004, 100000005, 100000009];
    ctrl.clearOptions();

    OppQuickCreate.allProductTypeOptions.forEach(function(opt) {
        if (toHide.indexOf(opt.value) === -1) {
            ctrl.addOption(opt);
        }
    });
};

/**
 * Filter Ticketing Stage based on Opportunity Type
 */
OppQuickCreate.filterTicketingStage = function(formContext) {
    var oppAttr = formContext.getAttribute("new_opportunitytype");
    var ctrl = formContext.getControl("new_ticketingstage");
    if (!oppAttr || !ctrl) return;

    var type = oppAttr.getValue();
    var map = {
        100000000: [100000000, 100000001, 100000002, 100000003, 100000004, 100000005, 100000006],
        100000001: [100000000, 100000001, 100000002, 100000007, 100000005, 100000006],
        100000004: [100000000, 100000010, 100000011, 100000012, 100000013, 100000005, 100000006],
        100000005: [100000000, 100000014, 100000015, 100000016, 100000017, 100000018, 100000019, 100000020, 100000021, 100000022, 100000006],
        
        // UPDATED MAPPING FOR 100000002
        // Showing only options 0 - 12 as requested
        100000002: [
            100000000, 100000001, 100000002, 100000008, 100000009, 
            100000023, 100000024, 100000025, 100000026, 100000027, 
            100000028, 100000029, 100000030
        ]
    };

    ctrl.clearOptions();
    var allowed = map[type] || [];

    OppQuickCreate.allTicketOptions.forEach(function(opt) {
        if (type === null || allowed.indexOf(opt.value) > -1) {
            ctrl.addOption(opt);
        }
    });
};

/**
 * Logic to set mandatory fields based on the three rules
 */
OppQuickCreate.setRequiredFields = function(formContext) {
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
    // Mandatory if Product is Deposit (100000008), Partial (100000010), or Flex (100000011)
    // And Stage is 3-Presented (100000003), 4-Awaiting (100000004), or Closed Won (100000005)
    if (oppType === 100000000) {
        var prodTypesR1 = [100000008, 100000010, 100000011]; 
        var stagesR1 = [100000003, 100000004, 100000005];
        if (prodTypesR1.indexOf(prodType) > -1 && stagesR1.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    // RULE 2: Ticketing - Groups (100000001)
    // Mandatory if Product is Groups (100000001)
    // And Stage is 3-Seats Reserved (100000007) or Closed Won (100000005)
    if (!isRequired && oppType === 100000001) {
        var stagesR2 = [100000007, 100000005];
        if (prodType === 100000001 && stagesR2.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    // RULE 3: Ticketing - New FSE (100000000) + Premium Hospitality (100000012)
    // Mandatory if Stage is 2-Pitched (100000011), 3-Internal (100000012), 4-Post Mtg (100000013), or Closed Won (100000005)
    if (!isRequired && oppType === 100000000 && prodType === 100000012) {
        var stagesR3 = [100000011, 100000012, 100000013, 100000005];
        if (stagesR3.indexOf(stage) > -1) {
            isRequired = true;
        }
    }

    detailAttr.setRequiredLevel(isRequired ? "required" : "none");
};

/**
 * Filter Product Type Detail based on Product Type and apply requirement logic
 */
OppQuickCreate.filterProductDetail = function(formContext) {
    var prodAttr = formContext.getAttribute("new_producttype");
    var detailAttr = formContext.getAttribute("new_producttypedetail");
    var ctrl = formContext.getControl("new_producttypedetail");
    if (!prodAttr || !ctrl || OppQuickCreate.allProductDetailOptions.length === 0) return;

    var type = prodAttr.getValue();
    
    // Updated mapping including Partial and Flex Plans
    var map = {
        100000008: [100000000, 100000001, 100000002], 
        100000001: [100000003, 100000004, 100000005], 
        100000012: [100000006, 100000008, 100000009, 100000011, 100000012, 100000020, 100000021, 100000022, 100000023, 100000024], //new_producttype = Premium Hospitality
        100000010: [100000013, 100000014, 100000015, 100000016, 100000017], 
        100000011: [100000018, 100000019] 
    };

    ctrl.clearOptions();
    var allowed = map[type] || [];

    OppQuickCreate.allProductDetailOptions.forEach(function(opt) {
        if (allowed.indexOf(opt.value) > -1) {
            ctrl.addOption(opt);
        }
    });

    // Reset value if it's no longer valid for the selected product type
    if (detailAttr.getValue() !== null && allowed.indexOf(detailAttr.getValue()) === -1) {
        detailAttr.setValue(null);
    }

    // Call mandatory logic
    OppQuickCreate.setRequiredFields(formContext);
};