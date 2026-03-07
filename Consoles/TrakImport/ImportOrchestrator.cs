
namespace TrakImport;

/// <summary>
/// Orchestrates the full import flow:
///   CSV rows  →  group by DealId  →  Opportunity  →  Deal  →  Deal Lines
/// </summary>
public class ImportOrchestrator
{
    private readonly DynamicsService _crm;
    private readonly ImportLogger    _log;

    public ImportOrchestrator(DynamicsService crm, ImportLogger log)
    {
        _crm = crm;
        _log = log;
    }

    public void Run(List<CsvRecord> records)
    {
        // Group by DealId — one Opportunity + Deal per group
        var dealGroups = records
            .GroupBy(r => r.DealId)
            .OrderBy(g => g.Key)
            .ToList();

        Console.WriteLine($"Processing {dealGroups.Count} deal groups ({records.Count} total lines)...\n");

        int dealsDone = 0, dealsError = 0;

        foreach (var group in dealGroups)
        {
            bool ok = ProcessDealGroup(group.First(), group.ToList());
            if (ok) dealsDone++; else dealsError++;
        }

        Console.WriteLine($"\n--- Deals processed: {dealsDone} OK, {dealsError} with errors ---");
    }

    // ── Per-deal ──────────────────────────────────────────────────────────────

    private bool ProcessDealGroup(CsvRecord deal, List<CsvRecord> lines)
    {
        Console.WriteLine($"\n[DEAL {deal.DealId}] {deal.DealName} | Account: {deal.Account}");

        // 1. Account
        var accountId = _crm.GetAccountId(deal.Account);
        if (accountId == null)
        {
            var err = $"Account not found in Dynamics: '{deal.Account}'";
            foreach (var l in lines)
                _log.Add(LogEntry.Fail(l.LineId, deal.DealId, deal.DealName, deal.Account, ImportEntity.Opportunity, err));
            return false;
        }

        // 2. Contact (new_corporatepartnercontact = true) — optional, warning only
        var contactId = _crm.GetCorporatePartnerContact(accountId.Value);
        if (contactId == null)
            _log.Add(LogEntry.Warn(lines[0].LineId, deal.DealId, deal.DealName, deal.Account,
                ImportEntity.Opportunity,
                "No corporate partner contact found — Opportunity created without contact."));

        // 3. Season — required
        var seasonId = _crm.GetSeasonId(deal.Season);
        if (seasonId == null)
        {
            var err = $"Season not found: '{deal.Season}'";
            foreach (var l in lines)
                _log.Add(LogEntry.Fail(l.LineId, deal.DealId, deal.DealName, deal.Account, ImportEntity.Opportunity, err));
            return false;
        }

        // 4. Deal Status — optional, warning only
        var dealStatusId = _crm.GetDealStatusId(deal.DealStage);
        if (dealStatusId == null)
            _log.Add(LogEntry.Warn(lines[0].LineId, deal.DealId, deal.DealName, deal.Account,
                ImportEntity.Deal, $"Deal Status not found: '{deal.DealStage}' — Deal created without status."));

        // 5. Sales Person — optional, warning only
        var salesPersonId = _crm.GetUserId(deal.DealSalesPerson);
        if (salesPersonId == null && !string.IsNullOrWhiteSpace(deal.DealSalesPerson))
            _log.Add(LogEntry.Warn(lines[0].LineId, deal.DealId, deal.DealName, deal.Account,
                ImportEntity.Deal, $"Sales Person not found: '{deal.DealSalesPerson}'"));

        // 6. Service Person — optional, warning only
        var servicePersonId = _crm.GetUserId(deal.DealServicePerson);
        if (servicePersonId == null && !string.IsNullOrWhiteSpace(deal.DealServicePerson))
            _log.Add(LogEntry.Warn(lines[0].LineId, deal.DealId, deal.DealName, deal.Account,
                ImportEntity.Deal, $"Service Person not found: '{deal.DealServicePerson}'"));

        // 7. Create Opportunity
        Guid oppId;
        try
        {
            oppId = _crm.CreateOpportunity(deal.DealName, accountId.Value, contactId, seasonId.Value);
            _log.Add(LogEntry.Ok(lines[0].LineId, deal.DealId, deal.DealName,
                deal.Account, ImportEntity.Opportunity, oppId.ToString()));
        }
        catch (Exception ex)
        {
            var err = $"Failed to create Opportunity: {ex.Message}";
            foreach (var l in lines)
                _log.Add(LogEntry.Fail(l.LineId, deal.DealId, deal.DealName, deal.Account, ImportEntity.Opportunity, err));
            return false;
        }

        // 8. Create Deal
        decimal dealTotal = lines.Sum(l => l.Total);
        decimal dealYield = ComputeWeightedYield(lines);

        Guid dealRecordId;
        try
        {
            dealRecordId = _crm.CreateDeal(
                dealName       : deal.DealName,
                trakDealId     : deal.DealId,
                opportunityId  : oppId,
                accountId      : accountId.Value,
                dealStatusId   : dealStatusId,
                seasonId       : seasonId,
                salesPersonId  : salesPersonId,
                servicePersonId: servicePersonId,
                total          : dealTotal,
                yield          : dealYield);

            _log.Add(LogEntry.Ok(lines[0].LineId, deal.DealId, deal.DealName,
                deal.Account, ImportEntity.Deal, dealRecordId.ToString()));
        }
        catch (Exception ex)
        {
            var err = $"Failed to create Deal: {ex.Message}";
            foreach (var l in lines)
                _log.Add(LogEntry.Fail(l.LineId, deal.DealId, deal.DealName, deal.Account, ImportEntity.Deal, err));
            return false;
        }

        // 9. Create Deal Lines (each one individually — errors don't stop the others)
        foreach (var line in lines)
            ProcessDealLine(line, dealRecordId);

        return true;
    }

    // ── Per-line ──────────────────────────────────────────────────────────────

    private void ProcessDealLine(CsvRecord line, Guid dealRecordId)
    {
        Guid? inventoryId = null;
        Guid? productId   = null;

        if (!string.IsNullOrWhiteSpace(line.Inventory))
        {
            var inv = _crm.GetInventory(line.Inventory);
            if (inv == null)
                _log.Add(LogEntry.Warn(line.LineId, line.DealId, line.DealName, line.Account,
                    ImportEntity.DealLine,
                    $"Inventory not found: '{line.Inventory}' — line created without inventory/product."));
            else
            {
                inventoryId = inv.Value.invId;
                productId   = inv.Value.prodId;
            }
        }

        var lineSeasonId = _crm.GetSeasonId(line.Season);
        if (lineSeasonId == null)
            _log.Add(LogEntry.Warn(line.LineId, line.DealId, line.DealName, line.Account,
                ImportEntity.DealLine, $"Season not found for line: '{line.Season}'"));

        try
        {
            var lineId = _crm.CreateDealLine(
                name       : line.Name,
                trakLineId : line.LineId,
                dealId     : dealRecordId,
                inventoryId: inventoryId,
                productId  : productId,
                seasonId   : lineSeasonId,
                quantity   : line.Quantity,
                rate       : line.Rate,
                gainLoss   : line.GainLoss,
                yield      : line.Yield,
                rateCard   : line.RateCard,
                listRate   : line.ListRate,
                notes      : string.IsNullOrWhiteSpace(line.Description) ? null : line.Description,
                total      : line.Total);

            _log.Add(LogEntry.Ok(line.LineId, line.DealId, line.DealName,
                line.Account, ImportEntity.DealLine, lineId.ToString()));
        }
        catch (Exception ex)
        {
            _log.Add(LogEntry.Fail(line.LineId, line.DealId, line.DealName,
                line.Account, ImportEntity.DealLine, $"Failed to create Deal Line: {ex.Message}"));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static decimal ComputeWeightedYield(List<CsvRecord> lines)
    {
        decimal totalWeight = lines.Sum(l => Math.Abs(l.Total));
        if (totalWeight == 0)
            return lines.Count > 0 ? lines.Average(l => l.Yield) : 0;
        return lines.Sum(l => l.Yield * Math.Abs(l.Total)) / totalWeight;
    }
}
