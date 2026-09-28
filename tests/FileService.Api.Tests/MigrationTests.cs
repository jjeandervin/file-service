using FileService.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FileService.Api.Tests;

public sealed class MigrationTests
{
    private const string InitialMigration = "20260830034505_InitialFileMetadata";

    [PostgresFact]
    public async Task Existing_rows_read_as_hooviepack_after_AddClientId_and_old_image_inserts_still_work()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        await using (var db = database.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(InitialMigration);
        }

        var existingId = Guid.CreateVersion7();
        await database.ExecuteAsync(InsertWithoutClientId(existingId));

        await using (var db = database.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }

        // The pre-extraction image doesn't know ClientId or BucketName; the column default keeps its inserts working.
        var rollbackInsertId = Guid.CreateVersion7();
        await database.ExecuteAsync(InsertWithoutClientId(rollbackInsertId));

        await using (var db = database.CreateDbContext())
        {
            var rows = await db.Files.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync();
            Assert.Equal([existingId, rollbackInsertId], rows.Select(x => x.Id).Order());
            Assert.All(rows, row =>
            {
                Assert.Equal(FileRecord.DefaultClientId, row.ClientId);
                Assert.Null(row.BucketName);
                Assert.Equal($"files/{row.Id:N}/original", row.StorageKey);
            });
            Assert.Equal(
                [existingId, rollbackInsertId],
                (await db.Files.Where(x => x.ClientId == "hooviepack" && x.Status == FileStatus.Ready)
                    .Select(x => x.Id)
                    .ToListAsync()).Order());
        }

        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
              (SELECT column_default FROM information_schema.columns
                WHERE table_schema = 'files' AND table_name = 'Files' AND column_name = 'ClientId'),
              (SELECT indexdef FROM pg_indexes
                WHERE schemaname = 'files' AND indexname = 'IX_Files_ClientId_Status_CreatedAt'),
              (SELECT string_agg("MigrationId", ',' ORDER BY "MigrationId") FROM files."__EFMigrationsHistory")
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.StartsWith("'hooviepack'", reader.GetString(0), StringComparison.Ordinal);
        Assert.Contains("(\"ClientId\", \"Status\", \"CreatedAt\")", reader.GetString(1), StringComparison.Ordinal);
        Assert.StartsWith($"{InitialMigration},", reader.GetString(2), StringComparison.Ordinal);
        Assert.EndsWith("_AddClientId", reader.GetString(2), StringComparison.Ordinal);
    }

    private static string InsertWithoutClientId(Guid id) =>
        $"""
        INSERT INTO files."Files"
          ("Id", "StorageKey", "OriginalFileName", "ContentType", "DeclaredSize", "ActualSize",
           "UploadTokenHash", "Status", "CreatedAt", "UploadedAt")
        VALUES
          ('{id}', 'files/{id:N}/original', 'poster.jpg', 'image/jpeg', 1234, 1234,
           decode(repeat('00', 32), 'hex'), 'Ready', now(), now())
        """;
}
