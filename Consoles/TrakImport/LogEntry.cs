namespace TrakImport;

public enum ImportStatus { Success, Warning, Error, Skipped }

public enum ImportEntity { Opportunity, Deal, DealLine }

public class LogEntry
{
    public string Timestamp { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    public string LineId { get; set; } = "";
    public string DealId { get; set; } = "";
    public string DealName { get; set; } = "";
    public string Account { get; set; } = "";
    public string Entity { get; set; } = "";
    public string Status { get; set; } = "";
    public string RecordId { get; set; } = "";
    public string ErrorMessage { get; set; } = "";

    public static LogEntry Ok(string lineId, string dealId, string dealName,
        string account, ImportEntity entity, string recordId) => new()
    {
        LineId = lineId, DealId = dealId, DealName = dealName,
        Account = account, Entity = entity.ToString(),
        Status = ImportStatus.Success.ToString(), RecordId = recordId
    };

    public static LogEntry Fail(string lineId, string dealId, string dealName,
        string account, ImportEntity entity, string error) => new()
    {
        LineId = lineId, DealId = dealId, DealName = dealName,
        Account = account, Entity = entity.ToString(),
        Status = ImportStatus.Error.ToString(), ErrorMessage = error
    };

    public static LogEntry Warn(string lineId, string dealId, string dealName,
        string account, ImportEntity entity, string message) => new()
    {
        LineId = lineId, DealId = dealId, DealName = dealName,
        Account = account, Entity = entity.ToString(),
        Status = ImportStatus.Warning.ToString(), ErrorMessage = message
    };

    public static LogEntry Skip(string lineId, string dealId, string dealName,
        string account, ImportEntity entity, string reason) => new()
    {
        LineId = lineId, DealId = dealId, DealName = dealName,
        Account = account, Entity = entity.ToString(),
        Status = ImportStatus.Skipped.ToString(), ErrorMessage = reason
    };
}
