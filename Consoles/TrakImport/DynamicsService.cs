using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;

namespace TrakImport;

/// <summary>
/// All Dynamics 365 CRUD operations using IOrganizationService (ServiceClient).
/// Lookups are cached in-memory to avoid redundant queries.
/// Types per metadata.xlsx:
///   Currency fields  → new Money(value)
///   Whole number     → int
///   Decimal          → decimal
/// </summary>
public class DynamicsService
{
	private readonly IOrganizationService _svc;
	private readonly AppConfig _cfg;

	// ── In-memory caches ─────────────────────────────────────────────────────
	private readonly Dictionary<string, Guid?> _accountCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<Guid, Guid?> _contactCache = new();
	private readonly Dictionary<string, Guid?> _seasonCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Guid?> _dealStatusCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, Guid?> _userCache = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, (Guid invId, Guid? prodId)?> _inventoryCache
																 = new(StringComparer.OrdinalIgnoreCase);

	public DynamicsService(ServiceClient client, AppConfig config)
	{
		_svc = client;
		_cfg = config;
	}

	// ── Lookups ───────────────────────────────────────────────────────────────

	public Guid? GetAccountId(string name)
	{
		if (_accountCache.TryGetValue(name, out var cached)) return cached;
		var id = GetSingleId("account", "name", name, "accountid");
		_accountCache[name] = id;
		return id;
	}

	/// <summary>Contact linked to account where new_corporatepartnercontact = true</summary>
	public Guid? GetCorporatePartnerContact(Guid accountId)
	{
		if (_contactCache.TryGetValue(accountId, out var cached)) return cached;
		var qe = new QueryExpression("contact")
		{
			ColumnSet = new ColumnSet("contactid"),
			TopCount = 1
		};
		qe.Criteria.AddCondition("parentcustomerid", ConditionOperator.Equal, accountId);
		qe.Criteria.AddCondition("new_corporatepartnercontact", ConditionOperator.Equal, true);
		var id = _svc.RetrieveMultiple(qe).Entities.FirstOrDefault()?.Id;
		_contactCache[accountId] = id;
		return id;
	}

	public Guid? GetSeasonId(string name)
	{
		if (_seasonCache.TryGetValue(name, out var cached)) return cached;
		var id = GetSingleId(_cfg.SeasonEntity, "new_name", name, $"{_cfg.SeasonEntity}id");
		_seasonCache[name] = id;
		return id;
	}

	public Guid? GetDealStatusId(string name)
	{
		if (_dealStatusCache.TryGetValue(name, out var cached)) return cached;
		var id = GetSingleId(_cfg.DealStatusEntity, "new_name", name, $"{_cfg.DealStatusEntity}id");
		_dealStatusCache[name] = id;
		return id;
	}

	public Guid? GetUserId(string fullName)
	{
		if (_userCache.TryGetValue(fullName, out var cached)) return cached;
		var id = GetSingleId("systemuser", "fullname", fullName, "systemuserid");
		_userCache[fullName] = id;
		return id;
	}

	/// <summary>Returns (inventoryId, productId?) from new_inventory by name</summary>
	public (Guid invId, Guid? prodId)? GetInventory(string name)
	{
		if (_inventoryCache.TryGetValue(name, out var cached)) return cached;
		var qe = new QueryExpression(_cfg.InventoryEntity)
		{
			ColumnSet = new ColumnSet($"{_cfg.InventoryEntity}id", "new_productid"),
			TopCount = 1
		};
		qe.Criteria.AddCondition("new_name", ConditionOperator.Equal, name);
		var entity = _svc.RetrieveMultiple(qe).Entities.FirstOrDefault();
		if (entity == null) { _inventoryCache[name] = null; return null; }

		var invId = entity.Id;
		Guid? prodId = entity.Contains("new_productid") && entity["new_productid"] is EntityReference r
					   ? r.Id : (Guid?)null;
		var result = (invId, prodId);
		_inventoryCache[name] = result;
		return result;
	}

	// ── Create: Opportunity ───────────────────────────────────────────────────

	public Guid CreateOpportunity(
		string name, Guid accountId, Guid? contactId, Guid seasonId,
		int opportunityType = 100000006,
		int salesStage = 100000019,
		int leadSource = 100000000)
	{
		var opp = new Entity("opportunity");
		opp["name"] = name;
		opp["new_opportunitytype"] = new OptionSetValue(opportunityType);
		opp["new_salesstage"] = new OptionSetValue(salesStage);
		opp["new_leadsource"] = new OptionSetValue(leadSource);
		opp["parentaccountid"] = new EntityReference("account", accountId);
		opp["new_basketballseason"] = new EntityReference(_cfg.SeasonEntity, seasonId);

		if (contactId.HasValue)
			opp["parentcontactid"] = new EntityReference("contact", contactId.Value);

		return _svc.Create(opp);
	}

	// ── Create: Deal (new_deals) ──────────────────────────────────────────────
	// new_total    → Currency  → Money
	// new_yield    → Decimal   → decimal
	// new_trakdealid → Whole number → int

	public Guid CreateDeal(
		string dealName, string trakDealId,
		Guid opportunityId, Guid accountId,
		Guid? dealStatusId, Guid? seasonId,
		Guid? salesPersonId, Guid? servicePersonId,
		decimal total, decimal yield)
	{
		var deal = new Entity(_cfg.DealEntity);
		deal["new_name"] = dealName;
		deal["new_trakdealid"] = ParseInt(trakDealId);       // Whole number
		deal["new_opportunity"] = new EntityReference("opportunity", opportunityId);
		deal["new_accountid"] = new EntityReference("account", accountId);
		deal["new_total"] = new Money(total);           // Currency
		deal["new_yield"] = yield;                      // Decimal

		if (dealStatusId.HasValue)
			deal["new_dealstatus"] = new EntityReference(_cfg.DealStatusEntity, dealStatusId.Value);
		if (seasonId.HasValue)
			deal["new_season"] = new EntityReference(_cfg.SeasonEntity, seasonId.Value);
		if (salesPersonId.HasValue)
			deal["new_salesperson"] = new EntityReference("systemuser", salesPersonId.Value);
		if (servicePersonId.HasValue)
			deal["new_serviceperson"] = new EntityReference("systemuser", servicePersonId.Value);

		return _svc.Create(deal);
	}

	// ── Create: Deal Line (new_deallines) ─────────────────────────────────────
	// Currency fields : new_gainloss, new_listrate, new_rate, new_ratecard, new_total → Money
	// Whole number    : new_quantity → int, new_traklineid → int
	// Decimal         : new_yield → decimal

	public Guid CreateDealLine(
		string name, string trakLineId,
		Guid dealId, Guid? inventoryId,
		Guid? productId, Guid? seasonId,
		decimal quantity, decimal rate, decimal gainLoss,
		decimal yield, string rateCard, decimal listRate,
		string? notes, decimal total)
	{
		var line = new Entity(_cfg.DealLineEntity);
		line["new_name"] = name;
		line["new_traklineid"] = ParseInt(trakLineId);        // Whole number
		line["new_dealid"] = new EntityReference(_cfg.DealEntity, dealId);
		line["new_quantity"] = (int)quantity;               // Whole number
		line["new_rate"] = new Money(rate);             // Currency
		line["new_gainloss"] = new Money(gainLoss);         // Currency
		line["new_yield"] = yield;                       // Decimal
		line["new_ratecard"] = new Money(ParseRateCard(rateCard)); // Currency
		line["new_listrate"] = new Money(listRate);         // Currency
		line["new_total"] = new Money(total);            // Currency

		if (!string.IsNullOrWhiteSpace(notes))
			line["new_notes"] = notes;
		if (inventoryId.HasValue)
			line["new_inventory"] = new EntityReference(_cfg.InventoryEntity, inventoryId.Value);
		if (productId.HasValue)
			line["new_productid"] = new EntityReference("new_product", productId.Value);
		if (seasonId.HasValue)
			line["new_seasonid"] = new EntityReference(_cfg.SeasonEntity, seasonId.Value);

		return _svc.Create(line);
	}

	// ── Private helpers ───────────────────────────────────────────────────────

	private Guid? GetSingleId(string entity, string filterField, string filterValue, string idField)
	{
		var qe = new QueryExpression(entity)
		{
			ColumnSet = new ColumnSet(idField),
			TopCount = 1
		};
		qe.Criteria.AddCondition(filterField, ConditionOperator.Equal, filterValue);
		return _svc.RetrieveMultiple(qe).Entities.FirstOrDefault()?.Id;
	}

	/// <summary>Parses a string like "101" or "101.0" to int safely</summary>
	private static int ParseInt(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return 0;
		if (decimal.TryParse(value, out var d)) return (int)d;
		return 0;
	}

	/// <summary>Rate Card in CSV may come as text (e.g. "Standard") or number — store 0 if not numeric</summary>
	private static decimal ParseRateCard(string value)
	{
		if (string.IsNullOrWhiteSpace(value)) return 0;
		value = value.Replace(",", "").Trim();
		return decimal.TryParse(value, out var d) ? d : 0;
	}
}