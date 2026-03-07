using Microsoft.Extensions.Configuration;

namespace TrakImport;

public class AppConfig
{
    public string CrmBaseUrl   { get; private set; } = "";
    public string CsvFilePath  { get; private set; } = "";
    public string LogFilePath  { get; private set; } = "";

    // Logical names of custom entities (NOT entity set names)
    // These are used directly with ServiceClient / IOrganizationService
    public string DealEntity        { get; private set; } = "new_deals";
    public string DealLineEntity    { get; private set; } = "new_deallines";
    public string DealStatusEntity  { get; private set; } = "new_dealstatus";
    public string SeasonEntity      { get; private set; } = "new_season";
    public string InventoryEntity   { get; private set; } = "new_inventory";

    public static AppConfig Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var appConfig = new AppConfig
        {
            CrmBaseUrl  = config["CrmBaseUrl"]  ?? throw new Exception("CrmBaseUrl missing in appsettings.json"),
            CsvFilePath = config["CsvFilePath"] ?? "traksoftware_deal_lines_2026_all_deals.csv",
            LogFilePath = config["LogFilePath"] ?? "import_log.csv"
        };

        var sets = config.GetSection("EntitySets");
        appConfig.DealEntity       = sets["Deal"]       ?? appConfig.DealEntity;
        appConfig.DealLineEntity   = sets["DealLine"]   ?? appConfig.DealLineEntity;
        appConfig.DealStatusEntity = sets["DealStatus"] ?? appConfig.DealStatusEntity;
        appConfig.SeasonEntity     = sets["Season"]     ?? appConfig.SeasonEntity;
        appConfig.InventoryEntity  = sets["Inventory"]  ?? appConfig.InventoryEntity;

        return appConfig;
    }
}
