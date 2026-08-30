using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HooviePack.Files.Api.Infrastructure.Data;

public sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args)
    {
        var projectDirectory = ResolveProjectDirectory();
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environments.Development;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(projectDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is required for EF Core design-time commands.");
        }

        var options = new DbContextOptionsBuilder<FilesDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "files"))
            .Options;
        return new FilesDbContext(options);
    }

    private static string ResolveProjectDirectory()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        foreach (var candidate in new[]
                 {
                     currentDirectory,
                     Path.Combine(currentDirectory, "src", "HooviePack.Files.Api"),
                     Path.Combine(currentDirectory, "apps", "api", "src", "HooviePack.Files.Api")
                 })
        {
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Run EF Core design-time commands from the repository root, apps/api, or apps/api/src/HooviePack.Files.Api.");
    }
}
