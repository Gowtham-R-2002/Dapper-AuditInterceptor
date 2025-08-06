using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Dapper.AuditInterceptor;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDapperAuditInterceptor(
        this IServiceCollection services,
        string connectionString)
    {
        services.AddHttpContextAccessor();
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
