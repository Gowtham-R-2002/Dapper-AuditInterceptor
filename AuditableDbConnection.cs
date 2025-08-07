using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Dapper.AuditInterceptor;

public class AuditableDbConnection : DbConnection
{
    private readonly SqlConnection _connection;
    private readonly ILogger<AuditableDbConnection> _logger;
    private readonly IAuditContextProvider? _auditContextProvider;
    private readonly IAuditWriter? _auditWriter;

    public AuditableDbConnection(SqlConnection connection, ILogger<AuditableDbConnection> logger, IAuditContextProvider? auditContextProvider = null, IAuditWriter? auditWriter = null)
    {
        _connection = connection;
        _logger = logger;
        _auditContextProvider = auditContextProvider;
        _auditWriter = auditWriter;
    }

    public override string ConnectionString 
    { 
        get => _connection.ConnectionString; 
        set => _connection.ConnectionString = value; 
    }

    public override int ConnectionTimeout => _connection.ConnectionTimeout;
    public override string Database => _connection.Database;
    public override ConnectionState State => _connection.State;
    public override string DataSource => _connection.DataSource;
    public override string ServerVersion => _connection.ServerVersion;

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => _connection.BeginTransaction(isolationLevel);

    public override void ChangeDatabase(string databaseName) => _connection.ChangeDatabase(databaseName);

    public override void Close() => _connection.Close();

    protected override DbCommand CreateDbCommand()
    {
        var command = _connection.CreateCommand();
        return new EntitySnapshotAuditableDbCommand(command, _connection, _logger, _auditWriter, _auditContextProvider);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _connection?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override void Open() => _connection.Open();
} 