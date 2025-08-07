using System.Collections.Generic;

namespace Dapper.AuditInterceptor;

public class AuditContext
{
    public string UserId { get; set; } = "system";
    public string UserName { get; set; } = "System User";
    public string IpAddress { get; set; } = string.Empty;
    public string UserAgent { get; set; } = string.Empty;
    public Dictionary<string, object> CustomProperties { get; set; } = new();
}

public interface IAuditContextProvider
{
    AuditContext GetCurrentContext();
}

// Default implementation (no ASP.NET Core dependency)
public class AuditContextProvider : IAuditContextProvider
{
    public AuditContext GetCurrentContext()
    {
        return new AuditContext
        {
            UserId = "system",
            UserName = "System User",
            IpAddress = "unknown",
            UserAgent = "unknown"
        };
    }
}