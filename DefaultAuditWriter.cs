using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Dapper.AuditInterceptor;

public class DefaultAuditWriter : IAuditWriter
{
    private readonly string _connectionString;
    private readonly ILogger<DefaultAuditWriter> _logger;
     private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DefaultAuditWriter(string connectionString, ILogger<DefaultAuditWriter> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry)
    {
        try
        {
            EnsureAuditTableExists();

            using var conn = new SqlConnection(_connectionString);
            await conn.OpenAsync();

            var sql = @"
                INSERT INTO AuditLogs (
                    Timestamp, EventName, Query, Parameters,
                    BeforeImage, AfterImage, TableName,
                    UserId, UserName, IpAddress, UserAgent,
                    MachineName, ProcessId, ThreadId, CustomProperties
                ) VALUES (
                    @Timestamp, @EventName, @Query, @Parameters,
                    @BeforeImage, @AfterImage, @TableName,
                    @UserId, @UserName, @IpAddress, @UserAgent,
                    @MachineName, @ProcessId, @ThreadId, @CustomProperties
                )";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;

            cmd.Parameters.AddWithValue("@Timestamp", entry.Timestamp);
            cmd.Parameters.AddWithValue("@EventName", entry.EventName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Query", entry.Query ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Parameters", JsonSerializer.Serialize(entry.Parameters ?? new Dictionary<string, object>()));
            cmd.Parameters.AddWithValue("@BeforeImage", JsonSerializer.Serialize(entry.BeforeImage ?? new Dictionary<string, object>()));
            cmd.Parameters.AddWithValue("@AfterImage", JsonSerializer.Serialize(entry.AfterImage ?? new Dictionary<string, object>()));
            cmd.Parameters.AddWithValue("@TableName", entry.TableName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@UserId", entry?.UserId ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@UserName", entry?.UserName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@IpAddress", entry?.IpAddress ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@UserAgent", entry?.UserAgent ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@MachineName", entry.MachineName ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ProcessId", entry.ProcessId);
            cmd.Parameters.AddWithValue("@ThreadId", entry.ThreadId);
            cmd.Parameters.AddWithValue("@CustomProperties", JsonSerializer.Serialize(entry.CustomProperties ?? new Dictionary<string, object>()));

            await cmd.ExecuteNonQueryAsync();

            _logger.LogInformation("Audit record inserted for {EventName}", entry.EventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit entry.");
        }
    }

    private void EnsureAuditTableExists()
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        var checkTableSql = @"
            IF NOT EXISTS (
                SELECT * FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = 'AuditLogs'
            )
            BEGIN
                CREATE TABLE AuditLogs (
                    Id INT IDENTITY(1,1) PRIMARY KEY,
                    Timestamp DATETIME2 NOT NULL,
                    EventName NVARCHAR(100),
                    Query NVARCHAR(MAX),
                    Parameters NVARCHAR(MAX),
                    BeforeImage NVARCHAR(MAX),
                    AfterImage NVARCHAR(MAX),
                    TableName NVARCHAR(128),
                    UserId NVARCHAR(64),
                    UserName NVARCHAR(128),
                    IpAddress NVARCHAR(45),
                    UserAgent NVARCHAR(512),
                    MachineName NVARCHAR(128),
                    ProcessId INT,
                    ThreadId INT,
                    CustomProperties NVARCHAR(MAX),
                    CreatedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME()
                );
            END";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = checkTableSql;
        cmd.ExecuteNonQuery();
    }
}
