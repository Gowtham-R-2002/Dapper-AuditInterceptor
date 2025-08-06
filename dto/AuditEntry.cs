public class AuditEntry
{
    public DateTime Timestamp { get; set; }
    public string EventName { get; set; } = "";
    public string Query { get; set; } = "";
    public Dictionary<string, object> Parameters { get; set; } = new();
    public Dictionary<string, object> BeforeImage { get; set; } = new();
    public Dictionary<string, object> AfterImage { get; set; } = new();
    public string TableName { get; set; } = "";
    public string OperationType { get; set; } = "";

    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }

    public string MachineName { get; set; } = "";
    public int ProcessId { get; set; }
    public int ThreadId { get; set; }

    public Dictionary<string, object> CustomProperties { get; set; } = new();
}
