# Dapper Audit Interceptor

[![NuGet](https://img.shields.io/nuget/v/GR.Dapper.AuditInterceptor.svg)](https://www.nuget.org/packages/GR.Dapper.AuditInterceptor)

A powerful, lightweight .NET library that provides transparent before/after auditing for Dapper ORM operations with zero repository modification required. Built with minimal dependencies and maximum flexibility.

## 🚀 Features

- **Zero Code Changes**: Works with existing Dapper repositories without modification
- **Automatic Entity Snapshots**: Captures before and after states of entities using SQL Server's OUTPUT clause
- **Intelligent SQL Parsing**: Advanced parsing of INSERT, UPDATE, and DELETE operations
- **Context Awareness**: Captures user context, IP address, and custom properties
- **Framework Agnostic**: Works with ASP.NET Core, console apps, Windows services, and more
- **Flexible Storage**: Multiple audit writer implementations and easy customization
- **Performance Optimized**: Minimal overhead with efficient SQL parsing and caching
- **Lightweight**: Minimal dependencies, no EF Core or ASP.NET Core requirements

## 📦 Installation

```bash
dotnet add package GR.Dapper.AuditInterceptor
```

## 🏃‍♂️ Quick Start

### 1. Basic Configuration (Default Database Storage)

```csharp
// Program.cs or Startup.cs
using Dapper.AuditInterceptor;

var builder = WebApplication.CreateBuilder(args);

// Add Dapper Audit Interceptor with default database storage
builder.Services.AddDapperAuditInterceptor(
    builder.Configuration.GetConnectionString("DefaultConnection")!);

var app = builder.Build();
```

### 2. Use in Repository (No Changes Required!)

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

    public async Task<int> DeleteUserAsync(int id)
    {
        using var connection = _connectionFactory.CreateConnection();
        
        var sql = "DELETE FROM Users WHERE Id = @Id";
        return await connection.ExecuteAsync(sql, new { Id = id });
    }
}
```

That's it! Your Dapper operations are now automatically audited with before/after snapshots.

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

## 🎯 Advanced Usage - Custom Audit Writers

### Option 1: Custom Audit Writer Instance

```csharp
// CustomAuditWriter.cs
public class FileAuditWriter : IAuditWriter
{
    private readonly ILogger<FileAuditWriter> _logger;
    private readonly string _filePath;

    public FileAuditWriter(ILogger<FileAuditWriter> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    public async Task WriteAsync(AuditEntry entry)
    {
        try
        {
            var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            
            await File.AppendAllTextAsync(_filePath, json + Environment.NewLine);
            _logger.LogInformation("Audit written to file: {EventName}", entry.EventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit to file");
        }
    }
}

// Program.cs
builder.Services.AddDapperAuditInterceptor(
    builder.Configuration.GetConnectionString("DefaultConnection")!,
    new FileAuditWriter(
        builder.Services.BuildServiceProvider().GetRequiredService<ILogger<FileAuditWriter>>(),
        "audit.log"
    )
);
```

### Option 2: Factory Pattern

```csharp
// Program.cs
builder.Services.AddDapperAuditInterceptor(
    builder.Configuration.GetConnectionString("DefaultConnection")!,
    provider =>
    {
        var logger = provider.GetRequiredService<ILogger<FileAuditWriter>>();
        var config = provider.GetRequiredService<IConfiguration>();
        var filePath = config.GetValue<string>("AuditLogPath", "audit.log");
        
        return new FileAuditWriter(logger, filePath);
    }
);
```

### Option 3: Manual Registration (Maximum Control)

```csharp
// Program.cs
// Register your custom audit writer
builder.Services.AddSingleton<IAuditWriter>(provider =>
{
    var logger = provider.GetRequiredService<ILogger<FileAuditWriter>>();
    var config = provider.GetRequiredService<IConfiguration>();
    var filePath = config.GetValue<string>("AuditLogPath", "audit.log");
    
    return new FileAuditWriter(logger, filePath);
});

// Register the core interceptor
builder.Services.AddDapperAuditInterceptorCore(
    builder.Configuration.GetConnectionString("DefaultConnection")!
);
```

## 🔗 Chained Audit Writers (Multiple Storage)

```csharp
// ChainedAuditWriter.cs
public class ChainedAuditWriter : IAuditWriter
{
    private readonly IEnumerable<IAuditWriter> _writers;
    private readonly ILogger<ChainedAuditWriter> _logger;

    public ChainedAuditWriter(IEnumerable<IAuditWriter> writers, ILogger<ChainedAuditWriter> logger)
    {
        _writers = writers;
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry)
    {
        var tasks = _writers.Select(writer => 
        {
            try
            {
                return writer.WriteAsync(entry);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write audit entry to one of the writers");
                return Task.CompletedTask;
            }
        });

        await Task.WhenAll(tasks);
    }
}

// Program.cs - Write to both database and file
builder.Services.AddSingleton<IAuditWriter>(provider =>
{
    var writers = new List<IAuditWriter>
    {
        new DefaultAuditWriter(
            builder.Configuration.GetConnectionString("DefaultConnection")!,
            provider.GetRequiredService<ILogger<DefaultAuditWriter>>()
        ),
        new FileAuditWriter(
            provider.GetRequiredService<ILogger<FileAuditWriter>>(),
            "audit.log"
        )
    };

    return new ChainedAuditWriter(
        writers,
        provider.GetRequiredService<ILogger<ChainedAuditWriter>>()
    );
});

builder.Services.AddDapperAuditInterceptorCore(
    builder.Configuration.GetConnectionString("DefaultConnection")!
);
```

## 🎛️ Selective Auditing

```csharp
// SelectiveAuditWriter.cs
public class SelectiveAuditWriter : IAuditWriter
{
    private readonly IAuditWriter _innerWriter;
    private readonly HashSet<string> _auditedTables;
    private readonly ILogger<SelectiveAuditWriter> _logger;

    public SelectiveAuditWriter(IAuditWriter innerWriter, ILogger<SelectiveAuditWriter> logger)
    {
        _innerWriter = innerWriter;
        _logger = logger;
        _auditedTables = new HashSet<string> { "Users", "Products", "Orders" };
    }

    public async Task WriteAsync(AuditEntry entry)
    {
        if (_auditedTables.Contains(entry.TableName))
        {
            await _innerWriter.WriteAsync(entry);
            _logger.LogDebug("Audited table: {TableName}", entry.TableName);
        }
        else
        {
            _logger.LogDebug("Skipped auditing table: {TableName}", entry.TableName);
        }
    }
}
```

## 🔄 Conditional Auditing

```csharp
// ConditionalAuditWriter.cs
public class ConditionalAuditWriter : IAuditWriter
{
    private readonly IAuditWriter _defaultWriter;
    private readonly IAuditWriter _customWriter;
    private readonly IConfiguration _config;
    private readonly ILogger<ConditionalAuditWriter> _logger;

    public ConditionalAuditWriter(
        IAuditWriter defaultWriter,
        IAuditWriter customWriter,
        IConfiguration config,
        ILogger<ConditionalAuditWriter> logger)
    {
        _defaultWriter = defaultWriter;
        _customWriter = customWriter;
        _config = config;
        _logger = logger;
    }

    public async Task WriteAsync(AuditEntry entry)
    {
        var useCustomWriter = _config.GetValue<bool>("UseCustomAuditWriter", false);
        
        if (useCustomWriter)
        {
            await _customWriter.WriteAsync(entry);
        }
        else
        {
            await _defaultWriter.WriteAsync(entry);
        }
    }
}
```

## 🎭 Custom Context Providers

```csharp
// CustomAuditContextProvider.cs
public class CustomAuditContextProvider : IAuditContextProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _config;

    public CustomAuditContextProvider(IHttpContextAccessor httpContextAccessor, IConfiguration config)
    {
        _httpContextAccessor = httpContextAccessor;
        _config = config;
    }

    public AuditContext GetCurrentContext()
    {
        var context = _httpContextAccessor.HttpContext;
        
        return new AuditContext
        {
            UserId = context?.User?.Identity?.Name ?? "anonymous",
            UserName = context?.User?.Identity?.Name ?? "Anonymous User",
            IpAddress = context?.Connection?.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = context?.Request?.Headers["User-Agent"].FirstOrDefault() ?? "unknown",
            CustomProperties = new Dictionary<string, object>
            {
                ["TenantId"] = _config.GetValue<string>("TenantId", "default"),
                ["Environment"] = _config.GetValue<string>("Environment", "development"),
                ["RequestId"] = context?.TraceIdentifier ?? Guid.NewGuid().ToString()
            }
        };
    }
}

// Program.cs
builder.Services.AddSingleton<IAuditContextProvider, CustomAuditContextProvider>();
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

### Get before/after images for updates
```sql
SELECT 
    Timestamp,
    EventName,
    JSON_VALUE(BeforeImage, '$.Name') as OldName,
    JSON_VALUE(AfterImage, '$.Name') as NewName
FROM AuditLogs 
WHERE TableName = 'Users' 
AND EventName LIKE '%Modified%'
ORDER BY Timestamp DESC;
```

## 🏗️ Architecture

The library consists of several key components:

- **AuditableDbConnection**: Wraps SQL connections to intercept commands
- **EntitySnapshotAuditableDbCommand**: Intercepts and audits SQL commands using OUTPUT clause
- **TSqlStatementParser**: Advanced SQL parsing for INSERT, UPDATE, and DELETE operations
- **AuditContextProvider**: Provides user and request context (framework agnostic)
- **IAuditWriter**: Interface for audit storage strategies (highly extensible)
- **DefaultAuditWriter**: Database-based audit storage implementation

## 📦 Dependencies

**Core Dependencies (Minimal):**
- Microsoft.Data.SqlClient (5.1.2) - SQL Server connectivity
- Microsoft.Extensions.DependencyInjection.Abstractions (8.0.0) - DI support
- Microsoft.Extensions.Logging.Abstractions (8.0.0) - Logging support
- Microsoft.SqlServer.TransactSql.ScriptDom (170.53.0) - SQL parsing

**Optional Dependencies:**
- Microsoft.AspNetCore.Http.Abstractions (2.3.0) - ASP.NET Core context support
- Microsoft.AspNetCore.Http (2.3.0) - ASP.NET Core context support

**Removed Dependencies:**
- ❌ Dapper (not required by the interceptor)
- ❌ Newtonsoft.Json (replaced with System.Text.Json)
- ❌ Microsoft.EntityFrameworkCore (removed unnecessary dependencies)
- ❌ Audit.NET (not used)

## 🔧 Requirements

- .NET 10.0 or later
- SQL Server database (for default audit storage)
- Dapper (in consuming applications, not required by the library)

## 🐛 Troubleshooting

### Common Issues

**Audit logs not being generated**
- Check that you're using `IDbConnectionFactory.CreateConnection()` instead of direct SqlConnection
- Verify that the SQL operations are INSERT, UPDATE, or DELETE statements
- Ensure the audit writer is properly registered in DI container
- Check that the connection string is valid and accessible

**Performance issues**
- Monitor the SQL parsing overhead for complex queries
- Check if audit table has proper indexes
- Consider implementing custom audit writer for high-volume scenarios
- Use selective auditing to reduce unnecessary audit records

**SQL parsing errors**
- Ensure SQL statements are valid T-SQL
- Check for unsupported SQL constructs (CTEs, complex subqueries)
- Verify parameter names match between SQL and C# code
- Review logs for specific parsing error details

**Dependency injection errors**
- Ensure only one `IAuditWriter` is registered
- Check that all required services are properly registered
- Verify connection string is provided to the extension method

## 🚀 Performance Tips

1. **Use Selective Auditing**: Only audit critical tables
2. **Implement Caching**: Cache table column information
3. **Batch Processing**: Consider batching audit writes for high-volume scenarios
4. **Index Optimization**: Add proper indexes to the AuditLogs table
5. **Connection Pooling**: Ensure proper connection string configuration

## 🤝 Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## 📄 License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## 🆘 Support

For support, please open an issue on the GitHub repository or contact the maintainers.

## 📈 Roadmap

- [x] Framework agnostic design
- [x] Minimal dependencies
- [x] Custom audit writer support
- [x] Chained audit writers
- [x] Selective auditing
- [ ] Support for PostgreSQL
- [ ] Support for MySQL
- [ ] Real-time audit streaming
- [ ] Audit data encryption
- [ ] Performance monitoring dashboard
- [ ] Audit data archival strategies

## 🙏 Acknowledgments

- [Dapper](https://github.com/DapperLib/Dapper) - The micro ORM that makes this possible
- [Microsoft.Data.SqlClient](https://github.com/dotnet/SqlClient) - SQL Server client library
- [Microsoft.SqlServer.TransactSql.ScriptDom](https://github.com/microsoft/sql-server-samples) - SQL parsing capabilities 