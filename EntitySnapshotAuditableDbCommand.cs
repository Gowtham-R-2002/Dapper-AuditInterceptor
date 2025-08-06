using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Dapper;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.IO;

namespace Dapper.AuditInterceptor;

public class EntitySnapshotAuditableDbCommand : DbCommand
{
    private readonly SqlCommand _command;
    private readonly SqlConnection _connection;
    private readonly ILogger _logger;
    private readonly IAuditContextProvider? _auditContextProvider;
    private readonly Dictionary<string, object> _entitySnapshots = new();
    private readonly TSqlStatementParser _sqlParser;
    private readonly IAuditWriter? _auditWriter;
    public EntitySnapshotAuditableDbCommand(DbCommand command, SqlConnection connection, ILogger logger, IAuditContextProvider? auditContextProvider = null, IAuditWriter? auditWriter = null)
    {
        _command = (SqlCommand)command;
        _connection = connection;
        _logger = logger;
        _auditContextProvider = auditContextProvider;
        _sqlParser = new TSqlStatementParser(logger);
        _auditWriter = auditWriter;
    }

    // Standard DbCommand properties remain the same...
    public override string CommandText { get => _command.CommandText; set => _command.CommandText = value; }
    public override int CommandTimeout { get => _command.CommandTimeout; set => _command.CommandTimeout = value; }
    public override CommandType CommandType { get => _command.CommandType; set => _command.CommandType = value; }
    protected override DbConnection DbConnection { get => _command.Connection; set => _command.Connection = (SqlConnection)value; }
    protected override DbParameterCollection DbParameterCollection => _command.Parameters;
    protected override DbTransaction DbTransaction { get => _command.Transaction; set => _command.Transaction = (SqlTransaction)value; }
    public override UpdateRowSource UpdatedRowSource { get => _command.UpdatedRowSource; set => _command.UpdatedRowSource = value; }
    public override bool DesignTimeVisible { get => _command.DesignTimeVisible; set => _command.DesignTimeVisible = value; }

    // Standard DbCommand methods remain the same...
    public override void Cancel() => _command.Cancel();
    protected override DbParameter CreateDbParameter() => _command.CreateParameter();
    protected override void Dispose(bool disposing)
    {
        if (disposing) _command?.Dispose();
        base.Dispose(disposing);
    }
    public override void Prepare() => _command.Prepare();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => _command.ExecuteReader(behavior);

    public override object ExecuteScalar()
    {
        return ExecuteScalarAsync().GetAwaiter().GetResult();
    }

    public new async Task<object> ExecuteScalarAsync()
    {
        var commandText = _command.CommandText.Trim();

        if (_sqlParser.IsAuditableOperation(commandText))
        {
            return await ExecuteScalarWithEntitySnapshotAuditAsync();
        }

        return await _command.ExecuteScalarAsync();
    }

    public override int ExecuteNonQuery()
    {
        return ExecuteNonQueryAsync().GetAwaiter().GetResult();
    }

    public new async Task<int> ExecuteNonQueryAsync()
    {
        var commandText = _command.CommandText.Trim();

        if (_sqlParser.IsAuditableOperation(commandText))
        {
            return await ExecuteWithEntitySnapshotAuditAsync();
        }

        return await _command.ExecuteNonQueryAsync();
    }

    private async Task<object> ExecuteScalarWithEntitySnapshotAuditAsync()
    {
        var commandText = _command.CommandText;
        var parameters = GetParameterValues();

        try
        {
            var sqlStatement = _sqlParser.Parse(commandText);

            Dictionary<string, object> entitySnapshot = new();

            // For INSERT operations, we don't need to load existing entity snapshot
            if (sqlStatement.OperationType != SqlOperationType.Insert)
            {
                entitySnapshot = await LoadEntitySnapshotAsync(sqlStatement, parameters);
            }

            // Execute the actual command
            var result = await _command.ExecuteScalarAsync();

            // Create audit log using snapshot
            await LogAuditEventWithSnapshotAsync(commandText, parameters, entitySnapshot, null, sqlStatement);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing auditable scalar command: {CommandText}", commandText);
            throw;
        }
    }

    private async Task<int> ExecuteWithEntitySnapshotAuditAsync()
    {
        var commandText = _command.CommandText;
        var parameters = GetParameterValues();

        try
        {
            var sqlStatement = _sqlParser.Parse(commandText);

            Dictionary<string, object> beforeSnapshot = new();
            Dictionary<string, object> afterSnapshot = new();

            // For INSERT operations, we don't need to load existing entity snapshot
            if (sqlStatement.OperationType != SqlOperationType.Insert)
            {
                beforeSnapshot = await LoadEntitySnapshotAsync(sqlStatement, parameters);
            }

            // Execute the actual command
            var result = await _command.ExecuteNonQueryAsync();

            // Load after snapshot based on operation type
            switch (sqlStatement.OperationType)
            {
                case SqlOperationType.Insert:
                    // For INSERT, try to load the newly inserted record
                    afterSnapshot = await LoadAfterSnapshotForInsertAsync(sqlStatement, parameters);
                    break;

                case SqlOperationType.Update:
                    // For UPDATE, reload the entity to get actual final values
                    afterSnapshot = await LoadEntitySnapshotAsync(sqlStatement, parameters);
                    break;

                case SqlOperationType.Delete:
                    // For DELETE, after image is empty (entity no longer exists)
                    afterSnapshot = new Dictionary<string, object>();
                    break;
            }

            // Create audit log using actual snapshots
            await LogAuditEventWithSnapshotAsync(commandText, parameters, beforeSnapshot, afterSnapshot, sqlStatement);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing auditable command: {CommandText}", commandText);
            throw;
        }
    }


    private async Task<Dictionary<string, object>> LoadAfterSnapshotForInsertAsync(SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(sqlStatement.TableName))
        {
            _logger.LogWarning("Cannot load after snapshot for INSERT - missing table name");
            return new Dictionary<string, object>();
        }

        try
        {
            // Try to find the inserted record using inserted values
            var whereConditions = new List<string>();
            var selectCommand = new SqlCommand("", _connection);

            // Build WHERE clause using non-null inserted values
            foreach (var param in parameters.Where(p => p.Value != null && p.Value != DBNull.Value))
            {
                // Skip common auto-generated fields that might not be suitable for WHERE clause
                var paramName = param.Key.Replace("@", "");
                if (!IsAutoGeneratedField(paramName))
                {
                    whereConditions.Add($"[{paramName}] = @{paramName}");
                    selectCommand.Parameters.AddWithValue($"@{paramName}", param.Value);
                }
            }

            if (whereConditions.Count == 0)
            {
                _logger.LogWarning("Cannot build WHERE clause for INSERT after snapshot - no suitable parameters");
                return ExtractInsertValuesAsFallback(sqlStatement, parameters);
            }

            var selectQuery = $"SELECT TOP 1 * FROM {sqlStatement.GetQualifiedTableName()} WHERE {string.Join(" AND ", whereConditions)} ORDER BY 1 DESC";
            selectCommand.CommandText = selectQuery;

            _logger.LogInformation("Loading INSERT after snapshot: {Query}", selectQuery);

            using var reader = await selectCommand.ExecuteReaderAsync();
            var snapshot = new Dictionary<string, object>();

            if (await reader.ReadAsync())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }
            }
            else
            {
                // Fallback to constructed values if we can't find the inserted record
                _logger.LogWarning("Could not locate inserted record, using constructed values");
                snapshot = ExtractInsertValuesAsFallback(sqlStatement, parameters);
            }

            _logger.LogInformation("INSERT after snapshot loaded: {Snapshot}", JsonConvert.SerializeObject(snapshot));
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load after snapshot for INSERT, using fallback");
            return ExtractInsertValuesAsFallback(sqlStatement, parameters);
        }
    }

    private Dictionary<string, object> ExtractInsertValuesAsFallback(SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        var insertValues = new Dictionary<string, object>();

        try
        {
            for (int i = 0; i < sqlStatement.InsertColumns.Count && i < sqlStatement.InsertValues.Count; i++)
            {
                var column = sqlStatement.InsertColumns[i];
                var valueInfo = sqlStatement.InsertValues[i];

                var value = valueInfo.GetParameterValue(parameters);
                if (value != null)
                {
                    insertValues[column] = value;
                }
            }

            if (insertValues.Count == 0)
            {
                _logger.LogWarning("Could not parse INSERT statement structure, using all parameters as fallback");
                insertValues = new Dictionary<string, object>(parameters);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract INSERT values, using all parameters");
            insertValues = new Dictionary<string, object>(parameters);
        }

        return insertValues;
    }

    private bool IsAutoGeneratedField(string fieldName)
    {
        var autoGenFields = new[] { "id", "createdat", "createddate", "timestamp", "rowversion", "modifiedat", "modifieddate" };
        return autoGenFields.Any(field => fieldName.Equals(field, StringComparison.OrdinalIgnoreCase) ||
                                         fieldName.EndsWith(field, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<string, object>> LoadEntitySnapshotAsync(SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        if (string.IsNullOrEmpty(sqlStatement.TableName) || string.IsNullOrEmpty(sqlStatement.WhereClause))
        {
            _logger.LogWarning("Cannot load entity snapshot - missing table name or where clause");
            return new Dictionary<string, object>();
        }

        try
        {
            var selectQuery = $"SELECT * FROM {sqlStatement.GetQualifiedTableName()} WHERE {sqlStatement.WhereClause}";
            _logger.LogInformation("Loading entity snapshot: {Query}", selectQuery);

            using var selectCommand = new SqlCommand(selectQuery, _connection);

            foreach (var param in parameters)
            {
                var paramName = param.Key.StartsWith("@") ? param.Key : "@" + param.Key;
                selectCommand.Parameters.AddWithValue(paramName, param.Value);
            }

            using var reader = await selectCommand.ExecuteReaderAsync();
            var snapshot = new Dictionary<string, object>();

            if (await reader.ReadAsync())
            {
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    snapshot[reader.GetName(i)] = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                }
            }

            _logger.LogInformation("Entity snapshot loaded: {Snapshot}", JsonConvert.SerializeObject(snapshot));
            return snapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load entity snapshot for table {TableName}", sqlStatement.TableName);
            return new Dictionary<string, object>();
        }
    }

    private async Task LogAuditEventWithSnapshotAsync(string commandText, Dictionary<string, object> parameters, Dictionary<string, object> beforeSnapshot, Dictionary<string, object>? afterSnapshot, SqlStatement sqlStatement)
    {
        try
        {
            var auditContext = _auditContextProvider?.GetCurrentContext();

            if(afterSnapshot == null)
            {
                afterSnapshot = CalculateAfterImage(beforeSnapshot, sqlStatement, parameters);
            }

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
                UserId = auditContext?.UserId,
                UserName = auditContext?.UserName,
                IpAddress = auditContext?.IpAddress,
                UserAgent = auditContext?.UserAgent,
                MachineName = Environment.MachineName,
                ProcessId = Environment.ProcessId,
                ThreadId = Environment.CurrentManagedThreadId,
                CustomProperties = auditContext?.CustomProperties ?? new Dictionary<string, object>()
            };


            var auditDir = Path.Combine(Directory.GetCurrentDirectory(), "auditlogs");
            Directory.CreateDirectory(auditDir);

            var fileName = $"audit_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.json";
            var filePath = Path.Combine(auditDir, fileName);

            var json = JsonConvert.SerializeObject(auditData, Formatting.Indented);
            await File.WriteAllTextAsync(filePath, json);
            await _auditWriter.WriteAsync(auditData);

            _logger.LogInformation("Entity snapshot audit logged: {EventName} for table {TableName} by user {UserId}",
                auditData.EventName, sqlStatement.TableName, auditContext?.UserId ?? "unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log entity snapshot audit event");
        }
    }

    private string GetEventName(SqlOperationType operationType)
    {
        return operationType switch
        {
            SqlOperationType.Insert => "Created",
            SqlOperationType.Update => "Modified",
            SqlOperationType.Delete => "Deleted",
            _ => "Changed"
        };
    }

    private Dictionary<string, object> CalculateAfterImage(Dictionary<string, object> beforeSnapshot, SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        var afterSnapshot = new Dictionary<string, object>();

        switch (sqlStatement.OperationType)
        {
            case SqlOperationType.Insert:
                // For INSERT, before image is empty, after image contains inserted values
                afterSnapshot = ExtractInsertValues(sqlStatement, parameters);
                break;

            case SqlOperationType.Update:
                // For UPDATE, start with before image and apply changes
                afterSnapshot = new Dictionary<string, object>(beforeSnapshot);

                // Update only the fields that are being changed
                foreach (var field in sqlStatement.UpdateFields)
                {
                    var value = field.GetParameterValue(parameters);
                    if (value != null)
                    {
                        afterSnapshot[field.ColumnName] = value;
                    }
                }
                break;

            case SqlOperationType.Delete:
                // For DELETE, after image is empty
                afterSnapshot.Clear();
                break;
        }

        return afterSnapshot;
    }

    private Dictionary<string, object> ExtractInsertValues(SqlStatement sqlStatement, Dictionary<string, object> parameters)
    {
        var insertValues = new Dictionary<string, object>();

        try
        {
            for (int i = 0; i < sqlStatement.InsertColumns.Count && i < sqlStatement.InsertValues.Count; i++)
            {
                var column = sqlStatement.InsertColumns[i];
                var valueInfo = sqlStatement.InsertValues[i];

                var value = valueInfo.GetParameterValue(parameters);
                if (value != null)
                {
                    insertValues[column] = value;
                }
            }

            if (insertValues.Count == 0)
            {
                // Fallback: if we can't parse the INSERT structure, include all parameters
                _logger.LogWarning("Could not parse INSERT statement structure, using all parameters as fallback");
                insertValues = new Dictionary<string, object>(parameters);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract INSERT values, using all parameters");
            insertValues = new Dictionary<string, object>(parameters);
        }

        return insertValues;
    }

    private Dictionary<string, object> GetParameterValues()
    {
        var paramValues = new Dictionary<string, object>();
        foreach (SqlParameter param in _command.Parameters)
        {
            paramValues[param.ParameterName] = param.Value ?? DBNull.Value;
        }
        return paramValues;
    }

    private async Task LogAuditToDatabase(string eventName, string query, Dictionary<string, object> parameters,
                                          Dictionary<string, object> beforeSnapshot, Dictionary<string, object> afterSnapshot,
                                          string tableName, string operationType, object? auditContext)
    {
        try
        {
            var insertSql = @"
            INSERT INTO AuditLogs (Timestamp, EventName, Query, Parameters, BeforeImage, AfterImage, 
                                 TableName, UserId, UserName, IpAddress, UserAgent, MachineName, 
                                 ProcessId, ThreadId, CustomProperties, CreatedAt)
            VALUES (@Timestamp, @EventName, @Query, @Parameters, @BeforeImage, @AfterImage, 
                   @TableName, @UserId, @UserName, @IpAddress, @UserAgent, @MachineName, 
                   @ProcessId, @ThreadId, @CustomProperties, @CreatedAt)";

            using var auditCommand = new SqlCommand(insertSql, _connection);

            var timestamp = DateTime.UtcNow;
            var auditCtx = auditContext as dynamic;

            auditCommand.Parameters.AddWithValue("@Timestamp", timestamp);
            auditCommand.Parameters.AddWithValue("@EventName", eventName ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@Query", query ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@Parameters", JsonConvert.SerializeObject(parameters) ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@BeforeImage", JsonConvert.SerializeObject(beforeSnapshot) ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@AfterImage", JsonConvert.SerializeObject(afterSnapshot) ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@TableName", tableName ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@UserId", auditCtx?.UserId ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@UserName", auditCtx?.UserName ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@IpAddress", auditCtx?.IpAddress ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@UserAgent", auditCtx?.UserAgent ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@MachineName", Environment.MachineName ?? (object)DBNull.Value);
            auditCommand.Parameters.AddWithValue("@ProcessId", Environment.ProcessId);
            auditCommand.Parameters.AddWithValue("@ThreadId", Environment.CurrentManagedThreadId);
            auditCommand.Parameters.AddWithValue("@CustomProperties", JsonConvert.SerializeObject(auditCtx?.CustomProperties ?? new Dictionary<string, object>()));
            auditCommand.Parameters.AddWithValue("@CreatedAt", timestamp);

            await auditCommand.ExecuteNonQueryAsync();

            _logger.LogInformation("Audit logged to database: {EventName} for table {TableName}", eventName, tableName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to log audit to database");
        }
    }
}

// Enhanced SQL Parser using Microsoft.SqlServer.TransactSql.ScriptDom
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

    public string GetQualifiedTableName()
    {
        if (!string.IsNullOrEmpty(SchemaName))
        {
            return $"[{SchemaName}].[{TableName}]";
        }
        return $"[{TableName}]";
    }
}

public class TSqlStatementParser
{
    private readonly ILogger _logger;
    private readonly TSqlParser _parser;

    public TSqlStatementParser(ILogger logger)
    {
        _logger = logger;
        // Use SQL Server 2019 parser (you can adjust this based on your SQL Server version)
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

            if (fragment is TSqlScript script && script.Batches?.Count > 0)
            {
                var firstStatement = script.Batches[0].Statements?.FirstOrDefault();
                return firstStatement is InsertStatement or UpdateStatement or DeleteStatement;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to determine if operation is auditable: {CommandText}", commandText);
        }

        return false;
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
                        statement = ParseInsert(insertStmt, commandText);
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

    private SqlStatement ParseInsert(InsertStatement insertStmt, string originalSql)
    {
        var statement = new SqlStatement { OperationType = SqlOperationType.Insert };

        try
        {
            // Extract table name
            if (insertStmt.InsertSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }

            // Extract columns
            if (insertStmt.InsertSpecification?.Columns != null)
            {
                foreach (var column in insertStmt.InsertSpecification.Columns)
                {
                    if (column.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value != null)
                    {
                        statement.InsertColumns.Add(column.MultiPartIdentifier.Identifiers.Last().Value);
                    }
                }
            }

            // Extract values
            if (insertStmt.InsertSpecification?.InsertSource is ValuesInsertSource valuesSource)
            {
                var firstRowValues = valuesSource.RowValues?.FirstOrDefault();
                if (firstRowValues?.ColumnValues != null)
                {
                    foreach (var value in firstRowValues.ColumnValues)
                    {
                        statement.InsertValues.Add(ExtractScalarValue(value));
                    }
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
            // Extract table name
            if (updateStmt.UpdateSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }

            // Extract SET clause
            if (updateStmt.UpdateSpecification?.SetClauses != null)
            {
                foreach (var setClause in updateStmt.UpdateSpecification.SetClauses)
                {
                    if (setClause is AssignmentSetClause assignmentClause)
                    {
                        var field = new UpdateField();

                        // Get column name
                        if (assignmentClause.Column?.MultiPartIdentifier?.Identifiers?.LastOrDefault()?.Value != null)
                        {
                            field.ColumnName = assignmentClause.Column.MultiPartIdentifier.Identifiers.Last().Value;
                        }

                        // Get value (parameter or literal)
                        var valueInfo = ExtractScalarValue(assignmentClause.NewValue);
                        field.ParameterName = valueInfo.ParameterName;
                        field.LiteralValue = valueInfo.LiteralValue;

                        statement.UpdateFields.Add(field);
                    }
                }
            }

            // Extract WHERE clause
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
            // Extract table name
            if (deleteStmt.DeleteSpecification?.Target is NamedTableReference tableRef)
            {
                ExtractTableInfo(tableRef, statement);
            }
            else if (deleteStmt.DeleteSpecification?.FromClause?.TableReferences?.FirstOrDefault() is NamedTableReference fromTableRef)
            {
                ExtractTableInfo(fromTableRef, statement);
            }

            // Extract WHERE clause
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
                // Database.Schema.Table format
                statement.SchemaName = identifiers[identifiers.Count - 2].Value;
                statement.TableName = identifiers[identifiers.Count - 1].Value;
            }
        }
    }

    private InsertValue ExtractScalarValue(ScalarExpression expression)
    {
        return expression switch
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
    }

    private string ExtractWhereClause(WhereClause whereClause, string originalSql)
    {
        try
        {
            // Get the position of the WHERE clause in the original SQL
            var whereStartIndex = originalSql.ToUpperInvariant().LastIndexOf("WHERE", StringComparison.OrdinalIgnoreCase);
            if (whereStartIndex >= 0)
            {
                var whereClausePart = originalSql.Substring(whereStartIndex + 5).Trim();
                return whereClausePart;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to extract WHERE clause, using fallback");
        }

        // Fallback: return string representation of the where condition
        return whereClause.SearchCondition?.ToString() ?? "";
    }
}