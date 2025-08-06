# Dapper Audit Interceptor - Developer Documentation

## Overview

Dapper Audit Interceptor is a powerful .NET library that provides transparent before/after auditing for Dapper ORM operations with zero repository modification required. This package automatically captures entity snapshots before and after database operations, providing comprehensive audit trails for compliance and debugging purposes.

## Key Features

- **Zero Code Changes**: Works with existing Dapper repositories without modification
- **Automatic Entity Snapshots**: Captures before and after states of entities
- **SQL Parsing**: Intelligent parsing of INSERT, UPDATE, and DELETE operations
- **Context Awareness**: Captures user context, IP address, and custom properties
- **Flexible Storage**: Supports both file-based and database audit logging
- **Performance Optimized**: Minimal overhead with efficient SQL parsing

## Quick Start

### 1. Install the Package

```bash
dotnet add package Dapper.AuditInterceptor
```

### 2. Configure Services

```csharp
// Program.cs or Startup.cs
using Dapper.AuditInterceptor;

var builder = WebApplication.CreateBuilder(args);

// Add Dapper Audit Interceptor
builder.Services.AddDapperAuditInterceptor(
    builder.Configuration.GetConnectionString("DefaultConnection")!);

// Register audit writer
builder.Services.AddSingleton<IAuditWriter, DefaultAuditWriter>();

var app = builder.Build();
```

### 3. Use in Repository

```csharp
public class UserRepository
{
    private readonly IDbConnectionFactory _connectionFactory;

    public UserRepository(IDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<int> CreateUserAsync(User user)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        var sql = "INSERT INTO Users (Name, Email) VALUES (@Name, @Email)";
        return await connection.ExecuteAsync(sql, user);
    }

    public async Task<int> UpdateUserAsync(User user)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        var sql = "UPDATE Users SET Name = @Name, Email = @Email WHERE Id = @Id";
        return await connection.ExecuteAsync(sql, user);
    }
}
```

That's it! Your Dapper operations are now automatically audited.

## Core Components

### AuditableDbConnection
Wraps SQL connections to intercept commands and provide audit capabilities.

### EntitySnapshotAuditableDbCommand
Intercepts SQL commands, parses them, and captures entity snapshots before and after operations.

### TSqlStatementParser
Intelligently parses SQL statements to identify auditable operations (INSERT, UPDATE, DELETE).

### AuditContextProvider
Provides user and request context information for audit logs.

### IAuditWriter
Interface for implementing custom audit storage strategies.

## Supported Operations

- **INSERT**: Captures the new entity state
- **UPDATE**: Captures before and after states
- **DELETE**: Captures the entity before deletion

## Database Schema

The default audit writer automatically creates an `AuditLogs` table with the following structure:

```sql
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
```

## Configuration

### appsettings.json
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=localhost;Database=MyApp;Trusted_Connection=true;"
  },
  "Logging": {
    "LogLevel": {
      "Dapper.AuditInterceptor": "Information"
    }
  }
}
```

### Custom Audit Writer
```csharp
public class CustomAuditWriter : IAuditWriter
{
    private readonly ILogger<CustomAuditWriter> _logger;

    public CustomAuditWriter(ILogger<CustomAuditWriter> logger)
    {
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry auditEntry)
    {
        // Custom audit storage logic
        _logger.LogInformation("Custom audit: {EventName}", auditEntry.EventName);
        await Task.CompletedTask;
    }
}
```

## Best Practices

### Performance Optimization
- Use connection pooling for optimal performance
- Consider implementing selective auditing for high-volume scenarios
- Always use async methods for database operations

### Security Considerations
- Be careful with sensitive data in audit logs
- Implement proper access controls for audit data
- Establish clear data retention policies

### Monitoring and Maintenance
- Monitor audit log generation for performance issues
- Regularly clean up old audit records
- Test audit functionality in your CI/CD pipeline

## Troubleshooting

### Common Issues

**Audit logs not being generated**
- Check that you're using `IDbConnectionFactory.CreateConnection()` instead of direct SqlConnection
- Verify that the SQL operations are INSERT, UPDATE, or DELETE statements
- Ensure the audit writer is properly registered in DI container

**Performance issues**
- Monitor the SQL parsing overhead for complex queries
- Check if audit table has proper indexes
- Consider implementing custom audit writer for high-volume scenarios

**SQL parsing errors**
- Ensure SQL statements are valid T-SQL
- Check for unsupported SQL constructs (CTEs, complex subqueries)
- Verify parameter names match between SQL and C# code

## API Reference

### AuditEntry
Represents an audit log entry with complete before/after information.

**Properties:**
- `Timestamp`: When the audit event occurred
- `EventName`: Name of the audit event
- `Query`: The SQL query that was executed
- `Parameters`: Query parameters
- `BeforeImage`: Entity state before the operation
- `AfterImage`: Entity state after the operation
- `TableName`: Affected table name
- `OperationType`: Type of operation (Insert/Update/Delete)
- `UserId`: User who performed the operation
- `UserName`: Display name of the user
- `IpAddress`: IP address of the request
- `UserAgent`: User agent string
- `MachineName`: Machine where the operation occurred
- `ProcessId`: Process ID
- `ThreadId`: Thread ID
- `CustomProperties`: Additional custom properties

### IAuditWriter
Interface for implementing custom audit storage strategies.

```csharp
public interface IAuditWriter
{
    Task WriteAsync(AuditEntry auditEntry);
}
```

### IAuditContextProvider
Interface for providing audit context information.

```csharp
public interface IAuditContextProvider
{
    AuditContext GetCurrentContext();
}
```

## Sample Queries

### Get all audit entries for a specific table
```sql
SELECT * FROM AuditLogs 
WHERE TableName = 'Users' 
ORDER BY Timestamp DESC;
```

### Get audit entries for a specific user
```sql
SELECT * FROM AuditLogs 
WHERE UserId = 'john.doe@example.com' 
ORDER BY Timestamp DESC;
```

### Get recent changes to a specific record
```sql
SELECT * FROM AuditLogs 
WHERE TableName = 'Products' 
AND JSON_VALUE(Parameters, '$.Id') = '123'
ORDER BY Timestamp DESC;
```

### Get audit summary by operation type
```sql
SELECT 
    OperationType,
    COUNT(*) as Count,
    MIN(Timestamp) as FirstOccurrence,
    MAX(Timestamp) as LastOccurrence
FROM AuditLogs 
WHERE Timestamp >= DATEADD(day, -30, GETDATE())
GROUP BY OperationType;
```

## Requirements

- .NET 10.0 or later
- Dapper 2.1.35 or later
- Microsoft.Data.SqlClient 5.1.2 or later
- SQL Server database (for database audit storage)

## License

This project is licensed under the MIT License.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Support

For support, please open an issue on the GitHub repository or contact the maintainers. 