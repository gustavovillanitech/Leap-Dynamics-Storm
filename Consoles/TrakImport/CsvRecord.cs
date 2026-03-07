namespace TrakImport;

/// <summary>
/// Maps each row in traksoftware_deal_lines_2026_all_deals.csv
/// </summary>
public class CsvRecord
{
    // Deal Line fields
    public string LineId { get; set; } = "";        // "Line  ID"
    public string ExternalId { get; set; } = "";
    public string DealId { get; set; } = "";
    public string DealName { get; set; } = "";
    public string DealDescription { get; set; } = "";
    public string DealStage { get; set; } = "";
    public string DealType { get; set; } = "";
    public string DealServicePerson { get; set; } = "";
    public string DealSalesPerson { get; set; } = "";

    // Deal Line detail
    public string Name { get; set; } = "";           // line item name
    public string Season { get; set; } = "";
    public string Account { get; set; } = "";
    public string AccountIndustry { get; set; } = "";
    public string Collection { get; set; } = "";
    public string Description { get; set; } = "";    // new_notes
    public string Categories { get; set; } = "";
    public string Inventory { get; set; } = "";
    public string RateCard { get; set; } = "";
    public decimal Rate { get; set; }
    public decimal Expense { get; set; }
    public decimal Quantity { get; set; }
    public decimal Total { get; set; }
    public decimal ListRate { get; set; }
    public decimal GainLoss { get; set; }
    public decimal Yield { get; set; }               // stored as decimal 0.336 (parsed from "33.6%")
    public string Published { get; set; } = "";
}
