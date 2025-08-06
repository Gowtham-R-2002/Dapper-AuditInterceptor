using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Dapper.AuditInterceptor;

public interface IDbConnectionFactory
{
    DbConnection CreateConnection();
}

public class AuditableConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;
    private readonly ILogger<AuditableDbConnection> _logger;
    private readonly IAuditContextProvider? _auditContextProvider;
    private readonly IAuditWriter? _auditWriter;

    public AuditableConnectionFactory(string connectionString, ILogger<AuditableDbConnection> logger, IAuditContextProvider? auditContextProvider = null, IAuditWriter? auditWriter = null)
    {
        _connectionString = connectionString;
        _logger = logger;
        _auditContextProvider = auditContextProvider;
        _auditWriter = auditWriter;
    }

    public DbConnection CreateConnection()
    {
        var sqlConnection = new SqlConnection(_connectionString);
        return new AuditableDbConnection(sqlConnection, _logger, _auditContextProvider, _auditWriter);
    }
} 