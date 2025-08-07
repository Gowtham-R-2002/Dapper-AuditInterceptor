using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Dapper.AuditInterceptor;

public class EntitySnapshotAuditableDbCommand : DbCommand
{
    private readonly SqlCommand _command;
    private readonly SqlConnection _connection;
    private readonly ILogger _logger;
    private readonly IAuditContextProvider? _auditContextProvider;
    private readonly IAuditWriter? _auditWriter;
    private readonly TSqlStatementParser _sqlParser;
    private static readonly ConcurrentDictionary<string, string[]> _tableColumnsCache = new();

    public EntitySnapshotAuditableDbCommand(
        DbCommand command,
        SqlConnection connection,
        ILogger logger,
        IAuditWriter? auditWriter,
        IAuditContextProvider? auditContextProvider = null)
    {
        _command = (SqlCommand)command;
        _connection = connection;
        _logger = logger;
        _auditContextProvider = auditContextProvider;
        _sqlParser = new TSqlStatementParser(logger);
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
    }

    // Standard DbCommand properties
    public override string CommandText { get => _command.CommandText; set => _command.CommandText = value; }
    public override int CommandTimeout { get => _command.CommandTimeout; set => _command.CommandTimeout = value; }
    public override CommandType CommandType { get => _command.CommandType; set => _command.CommandType = value; }
    protected override DbConnection DbConnection { get => _command.Connection; set => _command.Connection = (SqlConnection)value; }
    protected override DbParameterCollection DbParameterCollection => _command.Parameters;
    protected override DbTransaction DbTransaction { get => _command.Transaction; set => _command.Transaction = (SqlTransaction)value; }
    public override UpdateRowSource UpdatedRowSource { get => _command.UpdatedRowSource; set => _command.UpdatedRowSource = value; }
    public override bool DesignTimeVisible { get => _command.DesignTimeVisible; set => _command.DesignTimeVisible = value; }

    // Standard DbCommand methods
    public override void Cancel() => _command.Cancel();
    protected override DbParameter CreateDbParameter() => _command.CreateParameter();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _command?.Dispose();
        base.Dispose(disposing);
    }
    public override void Prepare() => _command.Prepare();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _command.ExecuteReader(behavior);

    public override object ExecuteScalar() => ExecuteScalarAsync().GetAwaiter().GetResult();

    public new async Task<object> ExecuteScalarAsync()
    {
        var commandText = _command.CommandText.Trim();
        if (_sqlParser.IsAuditableOperation(commandText))
        {
            return await ExecuteWithEntitySnapshotAuditAsync();
        }
        return await _command.ExecuteScalarAsync();
    }

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync().GetAwaiter().GetResult();

    public new async Task<int> ExecuteNonQueryAsync()
    {
        var commandText = _command.CommandText.Trim();
        if (_sqlParser.IsAuditableOperation(commandText))
        {
            return await ExecuteWithEntitySnapshotAuditAsync();
        }
        return await _command.ExecuteNonQueryAsync();
    }

    private async Task<int> ExecuteWithEntitySnapshotAuditAsync()
    {
        var commandText = _command.CommandText;
        var parameters = GetParameterValues();
        try
        {
            var sqlStatement = _sqlParser.Parse(commandText);
            var columns = await GetTableColumnsAsync(sqlStatement);
            var modifiedCommand = InjectOutputClause(commandText, sqlStatement, columns);
            _command.CommandText = modifiedCommand;

            Dictionary<string, object> beforeSnapshot = new();
            Dictionary<string, object> afterSnapshot = new();

            // Capture OUTPUT clause results
            using var reader = await _command.ExecuteReaderAsync();
            int rowsAffected = 0;

            while (await reader.ReadAsync())
            {
                rowsAffected++;

                foreach (var column in columns)
                {
                    if (sqlStatement.OperationType == SqlOperationType.Insert)
                    {
                        var insertedValue = reader[$"INSERTED_{column}"];
                        if (insertedValue != DBNull.Value)
                            afterSnapshot[column] = insertedValue;
                    }
                    else if (sqlStatement.OperationType == SqlOperationType.Update)
                    {
                        var beforeValue = reader[$"DELETED_{column}"];
                        if (beforeValue != DBNull.Value)
                            beforeSnapshot[column] = beforeValue;

                        var afterValue = reader[$"INSERTED_{column}"];
                        if (afterValue != DBNull.Value)
                            afterSnapshot[column] = afterValue;
                    }
                    else if (sqlStatement.OperationType == SqlOperationType.Delete)
                    {
                        var deletedValue = reader[$"DELETED_{column}"];
                        if (deletedValue != DBNull.Value)
                            beforeSnapshot[column] = deletedValue;
                    }
                }
            }

            await reader.CloseAsync();

            // Restore original command to avoid OUTPUT clause on actual execution
            _command.CommandText = commandText;

            await LogAuditEventAsync(commandText, parameters, beforeSnapshot, afterSnapshot, sqlStatement);
            return rowsAffected ;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing auditable command: {CommandText}", commandText);
            throw;
        }
    }


    private async Task<string[]> GetTableColumnsAsync(SqlStatement sqlStatement)
    {
        if (string.IsNullOrEmpty(sqlStatement.TableName))
        {
            _logger.LogWarning("Cannot get table columns - missing table name");
            return Array.Empty<string>();
        }

        var cacheKey = $"{sqlStatement.SchemaName}.{sqlStatement.TableName}".ToLowerInvariant();
        if (_tableColumnsCache.TryGetValue(cacheKey, out var columns))
        {
            return columns;
        }

        try
        {
            var query = $"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @tableName";
            if (!string.IsNullOrEmpty(sqlStatement.SchemaName))
            {
                query += " AND TABLE_SCHEMA = @schemaName";
            }

            using var command = _connection.CreateCommand();
            command.CommandText = query;
            command.Parameters.AddWithValue("@tableName", sqlStatement.TableName);
            if (!string.IsNullOrEmpty(sqlStatement.SchemaName))
            {
                command.Parameters.AddWithValue("@schemaName", sqlStatement.SchemaName);
            }

            var columnList = new List<string>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columnList.Add(reader.GetString(0));
            }
            
            columns = columnList.ToArray();
            _tableColumnsCache.TryAdd(cacheKey, columns);
            return columns;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch columns for table {TableName}", sqlStatement.TableName);
            return Array.Empty<string>();
        }
    }

    private string InjectOutputClause(string commandText, SqlStatement sqlStatement, string[] columns)
    {
        if (columns.Length == 0)
        {
            return commandText;
        }

        string outputColumns;
        string outputClause = "";

        switch (sqlStatement.OperationType)
        {
            case SqlOperationType.Insert:
                outputColumns = string.Join(", ", columns.Select(c =>
                    $"INSERTED.[{c}] AS [INSERTED_{c}]"));          
                outputClause = $" OUTPUT {outputColumns} ";
                return commandText.Insert(commandText.IndexOf("VALUES", StringComparison.OrdinalIgnoreCase), outputClause);

            case SqlOperationType.Update:
                outputColumns = string.Join(", ", columns.Select(c =>
                    $"DELETED.[{c}] AS [DELETED_{c}], INSERTED.[{c}] AS [INSERTED_{c}]"));

                var setIndex = commandText.IndexOf("SET", StringComparison.OrdinalIgnoreCase);
                if (setIndex == -1)
                    throw new InvalidOperationException("Invalid UPDATE statement: SET clause not found.");

                var whereIndex = commandText.IndexOf("WHERE", setIndex, StringComparison.OrdinalIgnoreCase);
                if (whereIndex == -1)
                    whereIndex = commandText.Length;

                var modifiedCommand = $"{commandText.Substring(0, whereIndex)} OUTPUT {outputColumns} {commandText.Substring(whereIndex)}";
                return modifiedCommand;


            case SqlOperationType.Delete:
                outputColumns = string.Join(", ", columns.Select(c =>
                     $"DELETED.[{c}] AS [DELETED_{c}]"));

                // For DELETE, insert OUTPUT after DELETE FROM {TableName}
                var fromIndex = commandText.IndexOf("FROM", StringComparison.OrdinalIgnoreCase);
                if (fromIndex == -1)
                {
                    throw new InvalidOperationException("Invalid DELETE statement: FROM clause not found.");
                }

                var deleteWhereIndex = commandText.IndexOf("WHERE", fromIndex, StringComparison.OrdinalIgnoreCase);
                if (deleteWhereIndex == -1)
                {
                    deleteWhereIndex = commandText.Length; // No WHERE clause
                }

                var deleteModifiedCommand = $"{commandText.Substring(0, deleteWhereIndex)} OUTPUT {outputColumns} {commandText.Substring(deleteWhereIndex)}";
                return deleteModifiedCommand;

            default:
                return commandText; // Return unchanged for unsupported operations
        }
    }


    private Dictionary<string, object> ExtractSnapshotFromParameters(SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        var snapshot = new Dictionary<string, object>();
        if (sqlStatement.OperationType == SqlOperationType.Update)
        {
            foreach (var field in sqlStatement.UpdateFields)
            {
                var value = field.GetParameterValue(parameters);
                if (value != null)
                {
                    snapshot[field.ColumnName] = value;
                }
            }
        }
        else if (sqlStatement.OperationType == SqlOperationType.Delete)
        {
            foreach (var param in parameters)
            {
                snapshot[param.Key.TrimStart('@')] = param.Value;
            }
        }
        return snapshot;
    }

    private Dictionary<string, object> ReadSnapshotFromReader(DbDataReader reader)
    {
        var snapshot = new Dictionary<string, object>();
        for (int i = 0; i < reader.FieldCount; i++)
        {
            snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
        }
        return snapshot;
    }

    private async Task LogAuditEventAsync(string commandText, Dictionary<string, object> parameters,
        Dictionary<string, object> beforeSnapshot, Dictionary<string, object> afterSnapshot, SqlStatement sqlStatement)
    {
        try
        {
            var auditContext = _auditContextProvider?.GetCurrentContext();
            var auditData = new AuditEntry
            {
                Timestamp = DateTime.UtcNow,
                EventName = $"{sqlStatement.TableName}_{GetEventName(sqlStatement.OperationType)}",
                Query = commandText,
                Parameters = parameters,
                BeforeImage = beforeSnapshot,
                AfterImage = afterSnapshot,
                TableName = sqlStatement.TableName,
                OperationType = sqlStatement.OperationType.ToString(),
                UserId = (auditContext as dynamic)?.UserId,
                UserName = (auditContext as dynamic)?.UserName,
                IpAddress = (auditContext as dynamic)?.IpAddress,
                UserAgent = (auditContext as dynamic)?.UserAgent,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                CustomProperties = (auditContext as dynamic)?.CustomProperties ?? new Dictionary<string, object>()
            };

            await _auditWriter.WriteAsync(auditData);
            _logger.LogInformation("Entity snapshot audit logged: {EventName} for table {TableName} by user {UserId}",
                auditData.EventName, sqlStatement.TableName, auditData.UserId ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log entity snapshot audit event");
        }
    }

    private string GetEventName(SqlOperationType operationType) =>
        operationType switch
        {
            SqlOperationType.Insert => "Created",
            SqlOperationType.Update => "Modified",
            SqlOperationType.Delete => "Deleted",
            _ => "Changed"
        };

    private Dictionary<string, object> GetParameterValues()
    {
        var paramValues = new Dictionary<string, object>();
        foreach (SqlParameter param in _command.Parameters)
        {
            paramValues[param.ParameterName] = param.Value ?? DBNull.Value;
        }
        return paramValues;
    }
}

public class TSqlStatementParser
{
    private readonly ILogger _logger;
    private readonly TSqlParser _parser;

    public TSqlStatementParser(ILogger logger)
    {
        _logger = logger;
        _parser = new TSql160Parser(true);
    }

    public bool IsAuditableOperation(string commandText)
    {
        if (string.IsNullOrWhiteSpace(commandText)) return false;
        try
        {
            using var reader = new StringReader(commandText);
            var fragment = _parser.Parse(reader, out var errors);
            if (errors?.Count > 0)
            {
                foreach (var error in errors)
                {
                    _logger.LogWarning("SQL parsing error: {Error} at line {Line}, column {Column}",
                        error.Message, error.Line, error.Column);
                }
            }
            return fragment is TSqlScript script && script.Batches?.Count > 0 &&
                   script.Batches[0].Statements?.FirstOrDefault() is InsertStatement or UpdateStatement or DeleteStatement;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine if operation is auditable: {CommandText}", commandText);
            return false;
        }
    }

    public SqlStatement Parse(string commandText)
    {
        var statement = new SqlStatement();
        try
        {
            using var reader = new StringReader(commandText);
            var fragment = _parser.Parse(reader, out var errors);
            if (errors?.Count > 0)
            {
                foreach (var error in errors)
                {
                    _logger.LogWarning("SQL parsing error: {Error} at line {Line}, column {Column}",
                        error.Message, error.Line, error.Column);
                }
            }
            if (fragment is TSqlScript script && script.Batches?.Count > 0)
            {
                var firstStatement = script.Batches[0].Statements?.FirstOrDefault();
                switch (firstStatement)
                {
                    case InsertStatement insertStmt:
                        statement = ParseInsert(insertStmt);
                        break;
                    case UpdateStatement updateStmt:
                        statement = ParseUpdate(updateStmt, commandText);
                        break;
                    case DeleteStatement deleteStmt:
                        statement = ParseDelete(deleteStmt, commandText);
                        break;
                    default:
                        _logger.LogWarning("Unsupported statement type: {StatementType}", firstStatement?.GetType().Name);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse SQL statement: {CommandText}", commandText);
        }
        return statement;
    }

    private SqlStatement ParseInsert(InsertStatement insertStmt)
    {
        var statement = new SqlStatement { OperationType = SqlOperationType.Insert };
        try
        {
            if (insertStmt.InsertSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }
            if (insertStmt.InsertSpecification?.Columns != null)
            {
                statement.InsertColumns.AddRange(
                    insertStmt.InsertSpecification.Columns
                        .Select(c => c.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value)
                        .Where(v => v != null));
            }
            if (insertStmt.InsertSpecification?.InsertSource is ValuesInsertSource valuesSource)
            {
                var firstRowValues = valuesSource.RowValues?.FirstOrDefault();
                if (firstRowValues?.ColumnValues != null)
                {
                    statement.InsertValues.AddRange(firstRowValues.ColumnValues.Select(ExtractScalarValue));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse INSERT statement details");
        }
        return statement;
    }

    private SqlStatement ParseUpdate(UpdateStatement updateStmt, string originalSql)
    {
        var statement = new SqlStatement { OperationType = SqlOperationType.Update };
        try
        {
            if (updateStmt.UpdateSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }
            if (updateStmt.UpdateSpecification?.SetClauses != null)
            {
                foreach (var setClause in updateStmt.UpdateSpecification.SetClauses)
                {
                    if (setClause is AssignmentSetClause assignmentClause)
                    {
                        var field = new UpdateField
                        {
                            ColumnName = assignmentClause.Column?.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value ?? "",
                            ParameterName = (assignmentClause.NewValue as VariableReference)?.Name,
                            LiteralValue = (assignmentClause.NewValue as StringLiteral)?.Value
                        };
                        statement.UpdateFields.Add(field);
                    }
                }
            }
            if (updateStmt.UpdateSpecification?.WhereClause != null)
            {
                statement.WhereClause = ExtractWhereClause(updateStmt.UpdateSpecification.WhereClause, originalSql);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse UPDATE statement details");
        }
        return statement;
    }

    private SqlStatement ParseDelete(DeleteStatement deleteStmt, string originalSql)
    {
        var statement = new SqlStatement { OperationType = SqlOperationType.Delete };
        try
        {
            if (deleteStmt.DeleteSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }
            else if (deleteStmt.DeleteSpecification?.FromClause?.TableReferences?.FirstOrDefault() is NamedTableReference fromTableRef)
            {
                ExtractTableInfo(fromTableRef, statement);
            }
            if (deleteStmt.DeleteSpecification?.WhereClause != null)
            {
                statement.WhereClause = ExtractWhereClause(deleteStmt.DeleteSpecification.WhereClause, originalSql);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse DELETE statement details");
        }
        return statement;
    }

    private void ExtractTableInfo(NamedTableReference tableRef, SqlStatement statement)
    {
        var identifiers = tableRef.SchemaObject?.Identifiers;
        if (identifiers?.Count > 0)
        {
            if (identifiers.Count == 1)
            {
                statement.TableName = identifiers[0].Value;
            }
            else if (identifiers.Count == 2)
            {
                statement.SchemaName = identifiers[0].Value;
                statement.TableName = identifiers[1].Value;
            }
            else if (identifiers.Count >= 3)
            {
                statement.SchemaName = identifiers[identifiers.Count - 2].Value;
                statement.TableName = identifiers[identifiers.Count - 1].Value;
            }
        }
    }

    private InsertValue ExtractScalarValue(ScalarExpression expression) =>
        expression switch
        {
            VariableReference varRef => new InsertValue { ParameterName = varRef.Name },
            StringLiteral stringLit => new InsertValue { LiteralValue = stringLit.Value },
            IntegerLiteral intLit => new InsertValue { LiteralValue = intLit.Value },
            NumericLiteral numLit => new InsertValue { LiteralValue = numLit.Value },
            RealLiteral realLit => new InsertValue { LiteralValue = realLit.Value },
            NullLiteral => new InsertValue { LiteralValue = null },
            DefaultLiteral => new InsertValue { LiteralValue = "DEFAULT" },
            _ => new InsertValue { LiteralValue = expression?.ToString() }
        };

    private string ExtractWhereClause(WhereClause whereClause, string originalSql)
    {
        try
        {
            var whereStartIndex = originalSql.ToUpperInvariant().LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            if (whereStartIndex >= 0)
            {
                return originalSql.Substring(whereStartIndex + 5).Trim();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract WHERE clause, using fallback");
        }
        return whereClause.SearchCondition?.ToString() ?? "";
    }
}

public enum SqlOperationType
{
    Insert,
    Update,
    Delete,
    Unknown
}

public class UpdateField
{
    public string ColumnName { get; set; } = "";
    public string? LiteralValue { get; set; }
    public string? ParameterName { get; set; }
    public bool IsParameter => !string.IsNullOrEmpty(ParameterName);

    public object? GetParameterValue(Dictionary<string, object> parameters)
    {
        if (IsParameter && ParameterName != null)
        {
            if (parameters.ContainsKey(ParameterName))
                return parameters[ParameterName];
            var paramWithAt = ParameterName.StartsWith("@") ? ParameterName : "@" + ParameterName;
            if (parameters.ContainsKey(paramWithAt))
                return parameters[paramWithAt];
        }
        return LiteralValue;
    }
}

public class InsertValue
{
    public string? LiteralValue { get; set; }
    public string? ParameterName { get; set; }
    public bool IsParameter => !string.IsNullOrEmpty(ParameterName);

    public object? GetParameterValue(Dictionary<string, object> parameters)
    {
        if (IsParameter && ParameterName != null)
        {
            if (parameters.ContainsKey(ParameterName))
                return parameters[ParameterName];
            var paramWithAt = ParameterName.StartsWith("@") ? ParameterName : "@" + ParameterName;
            if (parameters.ContainsKey(paramWithAt))
                return parameters[paramWithAt];
        }
        return LiteralValue;
    }
}

public class SqlStatement
{
    public SqlOperationType OperationType { get; set; } = SqlOperationType.Unknown;
    public string SchemaName { get; set; } = "";
    public string TableName { get; set; } = "";
    public string WhereClause { get; set; } = "";
    public List<string> InsertColumns { get; set; } = new();
    public List<InsertValue> InsertValues { get; set; } = new();
    public List<UpdateField> UpdateFields { get; set; } = new();

    public string GetQualifiedTableName() =>
        !string.IsNullOrEmpty(SchemaName) ? $"[{SchemaName}].[{TableName}]" : $"[{TableName}]";
}