using System.Threading.Tasks;

namespace Dapper.AuditInterceptor;

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry auditEntry);
}
