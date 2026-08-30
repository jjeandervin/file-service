using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace HooviePack.Files.Api.Infrastructure.Data;

public sealed class PostgresHealthCheck(IConfiguration configuration) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return HealthCheckResult.Unhealthy("Database connection string is not configured.");
        }

        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(configuredConnectionString)
            {
                Timeout = 3,
                CommandTimeout = 3,
                Pooling = false
            }.ConnectionString;
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = 3 };
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("PostgreSQL is reachable.");
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL is unavailable.", exception);
        }
    }
}
