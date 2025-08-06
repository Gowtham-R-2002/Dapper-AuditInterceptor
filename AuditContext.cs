using Microsoft.AspNetCore.Http;

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

public class AuditContextProvider : IAuditContextProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AuditContextProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public AuditContext GetCurrentContext()
    {
        var context = _httpContextAccessor.HttpContext;
        
        return new AuditContext
        {
            UserId = context?.User?.Identity?.Name ?? "anonymous",
            UserName = context?.User?.Identity?.Name ?? "Anonymous User",
            IpAddress = context?.Connection?.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = context?.Request?.Headers["User-Agent"].FirstOrDefault() ?? "unknown"
        };
    }
} 