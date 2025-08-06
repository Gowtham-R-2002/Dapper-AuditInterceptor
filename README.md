# Dapper Audit Interceptor

[![NuGet](https://img.shields.io/nuget/v/Dapper.AuditInterceptor.svg)](https://www.nuget.org/packages/GR.Dapper.AuditInterceptor)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

A powerful .NET library that provides transparent before/after auditing for Dapper ORM operations with zero repository modification required.

## 🚀 Features

- **Zero Code Changes**: Works with existing Dapper repositories without modification
- **Automatic Entity Snapshots**: Captures before and after states of entities
- **SQL Parsing**: Intelligent parsing of INSERT, UPDATE, and DELETE operations
- **Context Awareness**: Captures user context, IP address, and custom properties
- **Flexible Storage**: Supports both file-based and database audit logging
- **Performance Optimized**: Minimal overhead with efficient SQL parsing

## 📦 Installation

```bash
dotnet add package Dapper.AuditInterceptor
```

## 🏃‍♂️ Quick Start

### 1. Configure Services

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

### 2. Use in Repository

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

## 🔧 Configuration

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

## 📊 Database Schema

The default audit writer automatically creates an `AuditLogs` table:

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

## 🔍 Sample Queries

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

## 🏗️ Architecture

The library consists of several key components:

- **AuditableDbConnection**: Wraps SQL connections to intercept commands
- **EntitySnapshotAuditableDbCommand**: Intercepts and audits SQL commands
- **TSqlStatementParser**: Parses SQL statements for audit operations
- **AuditContextProvider**: Provides user and request context
- **IAuditWriter**: Interface for audit storage strategies

## 📚 Documentation

For comprehensive documentation, visit the [Documentation](docs/index.html) folder.

## 🛠️ Advanced Usage

### Custom Context Provider
```csharp
public class CustomAuditContextProvider : IAuditContextProvider
{
    public AuditContext GetCurrentContext()
    {
        return new AuditContext
        {
            UserId = "custom-user-id",
            UserName = "Custom User",
            IpAddress = "192.168.1.1",
            UserAgent = "Custom Agent",
            CustomProperties = new Dictionary<string, object>
            {
                ["TenantId"] = "tenant-123",
                ["Environment"] = "production"
            }
        };
    }
}
```

### Selective Auditing
```csharp
public class SelectiveAuditWriter : IAuditWriter
{
    private readonly IAuditWriter _innerWriter;
    private readonly HashSet<string> _auditedTables;

    public SelectiveAuditWriter(IAuditWriter innerWriter)
    {
        _innerWriter = innerWriter;
        _auditedTables = new HashSet<string> { "Users", "Products", "Orders" };
    }

    public async Task WriteAsync(AuditEntry auditEntry)
    {
        if (_auditedTables.Contains(auditEntry.TableName))
        {
            await _innerWriter.WriteAsync(auditEntry);
        }
    }
}
```

## 🔧 Requirements

- .NET 10.0 or later
- Dapper 2.1.35 or later
- Microsoft.Data.SqlClient 5.1.2 or later
- SQL Server database (for database audit storage)

## 🐛 Troubleshooting

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

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🆘 Support

For support, please open an issue on the GitHub repository or contact the maintainers.

## 📈 Roadmap

- [ ] Support for PostgreSQL
- [ ] Support for MySQL
- [ ] Real-time audit streaming
- [ ] Audit data encryption
- [ ] Performance monitoring dashboard
- [ ] Audit data archival strategies

## 🙏 Acknowledgments

- [Dapper](https://github.com/DapperLib/Dapper) - The micro ORM that makes this possible
- [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) - SQL Server client library
- [Audit.NET](https://github.com/thepirat000/Audit.NET) - Inspiration for audit patterns 