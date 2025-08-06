public interface IAuditWriter
{
    Task WriteAsync(AuditEntry auditEntry);
}
