using Microsoft.EntityFrameworkCore;

namespace HooviePack.Files.Api.Infrastructure.Data;

public static class DatabaseInitializer
{
    public static async Task ApplyMigrationsAsync(
        this WebApplication app,
        CancellationToken cancellationToken = default)
    {
        if (!app.Configuration.GetValue("Database:ApplyMigrations", app.Environment.IsDevelopment()))
        {
            return;
        }

        const int maxAttempts = 10;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
                await db.Database.MigrateAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (attempt < maxAttempts && !cancellationToken.IsCancellationRequested)
            {
                app.Logger.LogWarning(
                    exception,
                    "File metadata migration attempt {Attempt}/{MaxAttempts} failed; retrying.",
                    attempt,
                    maxAttempts);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt * 2, 10)), cancellationToken);
            }
        }
    }
}
