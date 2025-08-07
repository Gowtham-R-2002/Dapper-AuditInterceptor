using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dapper.AuditInterceptor;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDapperAuditInterceptor(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IAuditContextProvider, AuditContextProvider>();
        
        // Register DefaultAuditWriter with the connection string
        services.AddSingleton<IAuditWriter>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<DefaultAuditWriter>>();
            return new DefaultAuditWriter(connectionString, logger);
        });
        
        services.AddSingleton<IDbConnectionFactory>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<AuditableDbConnection>>();
            var contextProvider = provider.GetRequiredService<IAuditContextProvider>();
            var auditWriter = provider.GetRequiredService<IAuditWriter>();

            return new AuditableConnectionFactory(connectionString, logger, contextProvider, auditWriter);
        });

        return services;
    }

    public static IServiceCollection AddDapperAuditInterceptor(
        this IServiceCollection services,
        string connectionString,
        IAuditWriter auditWriter)
    {
        services.AddSingleton<IAuditContextProvider, AuditContextProvider>();
        
        // Register the provided custom audit writer
        services.AddSingleton<IAuditWriter>(auditWriter);
        
        services.AddSingleton<IDbConnectionFactory>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<AuditableDbConnection>>();
            var contextProvider = provider.GetRequiredService<IAuditContextProvider>();

            return new AuditableConnectionFactory(connectionString, logger, contextProvider, auditWriter);
        });

        return services;
    }

    public static IServiceCollection AddDapperAuditInterceptor(
        this IServiceCollection services,
        string connectionString,
        Func<IServiceProvider, IAuditWriter> auditWriterFactory)
    {
        services.AddSingleton<IAuditContextProvider, AuditContextProvider>();
        
        // Register the custom audit writer factory
        services.AddSingleton<IAuditWriter>(auditWriterFactory);
        
        services.AddSingleton<IDbConnectionFactory>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<AuditableDbConnection>>();
            var contextProvider = provider.GetRequiredService<IAuditContextProvider>();
            var auditWriter = provider.GetRequiredService<IAuditWriter>();

            return new AuditableConnectionFactory(connectionString, logger, contextProvider, auditWriter);
        });

        return services;
    }

    public static IServiceCollection AddDapperAuditInterceptorCore(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddSingleton<IAuditContextProvider, AuditContextProvider>();
        
        services.AddSingleton<IDbConnectionFactory>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<AuditableDbConnection>>();
            var contextProvider = provider.GetRequiredService<IAuditContextProvider>();
            var auditWriter = provider.GetRequiredService<IAuditWriter>();

            return new AuditableConnectionFactory(connectionString, logger, contextProvider, auditWriter);
        });

        return services;
    }
}
