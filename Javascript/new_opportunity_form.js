var OpportunityForm = OpportunityForm || {};

// Caches for the field options
OpportunityForm.allTicketOptions = [];
OpportunityForm.allProductTypeOptions = [];
OpportunityForm.allProductDetailOptions = [];
OpportunityForm.allSalesSourceOptions = [];
OpportunityForm.allLostReasonOptions = [];

OpportunityForm.onLoad = function(executionContext) {
    var formContext = executionContext.getFormContext();

    // SAFETY FIX: Ensure the ticketing stage is unlocked on load 
    // (In case it was stuck from the old script)
    var ticketingCtrl = formContext.getControl("new_ticketingstage");
    if (ticketingCtrl) {
        ticketingCtrl.setDisabled(false);
    }

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

    // 1. Cache the master lists of options
    OpportunityForm.allTicketOptions = cacheOptions("new_ticketingstage");
    OpportunityForm.allProductTypeOptions = cacheOptions("new_producttype");
    OpportunityForm.allProductDetailOptions = cacheOptions("new_producttypedetail");
    OpportunityForm.allSalesSourceOptions = cacheOptions("new_salessource");
    OpportunityForm.allLostReasonOptions = cacheOptions("new_lostreason");

    // 2. Validate cache and run filters
    if (OpportunityForm.allProductTypeOptions.length === 0) {
        setTimeout(function() {
            OpportunityForm.onLoad(executionContext);
        }, 300);
    } else {
        OpportunityForm.applyFilters(formContext);
    }
};

OpportunityForm.onControllingFieldChange = function(executionContext) {
    var formContext = executionContext.getFormContext();
    OpportunityForm.applyFilters(formContext);
};

OpportunityForm.applyFilters = function(formContext) {
    OpportunityForm.filterProductType(formContext);
    OpportunityForm.filterSalesSource(formContext);
    OpportunityForm.filterLostReason(formContext);
    OpportunityForm.filterTicketingStage(formContext);
    OpportunityForm.filterProductDetail(formContext);
};

OpportunityForm.filterLostReason = function(formContext) {
    var ctrl = formContext.getControl("new_lostreason");
    if (!ctrl || OpportunityForm.allLostReasonOptions.length === 0) return;
    var toHide = [100000015, 100000013, 100000012, 100000006, 100000011, 100000014];
    ctrl.clearOptions();
    OpportunityForm.allLostReasonOptions.forEach(function(o) {
        if (toHide.indexOf(o.value) === -1) ctrl.addOption(o);
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

OpportunityForm.filterProductDetail = function(formContext) {
    var attr = formContext.getAttribute("new_producttype");
    var detAttr = formContext.getAttribute("new_producttypedetail");
    var ctrl = formContext.getControl("new_producttypedetail");
    if (!attr || !ctrl || OpportunityForm.allProductDetailOptions.length === 0) return;
    
    var type = attr.getValue();
    var map = {
        100000008: [100000000, 100000001, 100000002],
        100000001: [100000003, 100000004, 100000005],
        100000012: [100000006, 100000007, 100000008, 100000009, 100000010, 100000011, 100000012]
    };
    
    ctrl.clearOptions();
    var allowed = map[type] || [];
    OpportunityForm.allProductDetailOptions.forEach(function(o) {
        if (allowed.indexOf(o.value) > -1) ctrl.addOption(o);
    });
    
    if (detAttr.getValue() !== null && allowed.indexOf(detAttr.getValue()) === -1) {
        detAttr.setValue(null);
    }
};