using FileService.Api.Application;
using FileService.Api.Configuration;
using FileService.Api.Domain;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Security;
using FileService.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace FileService.Api.Tests;

internal static class TestClients
{
    public const string GlobalBucket = "private-bucket";

    public static FileClient HooviePack { get; } = Create("hooviepack", "files");

    public static FileClient Acbf { get; } = Create("acbf", "acbf");

    public static FileClient Create(
        string id,
        string keyPrefix,
        string bucketName = GlobalBucket,
        long maxFileBytes = 10 * 1024 * 1024,
        string[]? allowedContentTypes = null) => new(
        id,
        keyPrefix,
        bucketName,
        maxFileBytes,
        new HashSet<string>(allowedContentTypes ?? ["image/jpeg", "image/png", "image/webp"], StringComparer.OrdinalIgnoreCase),
        ClientRegistry.HashApiKey($"{id}-test-api-key-with-at-least-32-characters"));

    public static FileStorageOptions StorageOptions(Action<FileStorageOptions>? configure = null)
    {
        var options = new FileStorageOptions
        {
            BucketName = GlobalBucket,
            Region = "us-east-1",
            KeyPrefix = "files",
            MaxFileBytes = 10 * 1024 * 1024,
            UploadUrlLifetimeMinutes = 5,
            DownloadUrlLifetimeMinutes = 5,
            AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"]
        };
        configure?.Invoke(options);
        return options;
    }

    public static FilesDbContext CreateInMemoryDb(string? name = null)
    {
        var options = new DbContextOptionsBuilder<FilesDbContext>()
            .UseInMemoryDatabase(name ?? Guid.NewGuid().ToString())
            .Options;
        return new FilesDbContext(options);
    }

    public static FileManager CreateManager(
        FilesDbContext db,
        IObjectStorage storage,
        ICallerClient? caller = null,
        DateTimeOffset? now = null,
        Action<FileStorageOptions>? configure = null) => new(
        db,
        storage,
        caller ?? HooviePack,
        Options.Create(StorageOptions(configure)),
        new FakeTimeProvider(now ?? DateTimeOffset.UtcNow),
        NullLogger<FileManager>.Instance);

    public static FileRecord PendingRecord(
        string clientId,
        DateTimeOffset createdAt,
        string? bucketName = null,
        FileStatus status = FileStatus.Pending,
        string? legacySourcePath = null)
    {
        var id = Guid.CreateVersion7(createdAt);
        return new FileRecord
        {
            Id = id,
            ClientId = clientId,
            BucketName = bucketName,
            LegacySourcePath = legacySourcePath,
            StorageKey = $"{clientId}/{id:N}/original",
            OriginalFileName = "poster.jpg",
            ContentType = "image/jpeg",
            DeclaredSize = 1234,
            ActualSize = status == FileStatus.Ready ? 1234 : null,
            UploadTokenHash = new byte[32],
            Status = status,
            CreatedAt = createdAt
        };
    }
}

internal static class TestFiles
{
    public static byte[] Read(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name));

    // A JPEG SOI/APP0 header, enough to pass signature verification in tests that don't care about content.
    public static readonly byte[] JpegHeader =
        [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01];
}

internal sealed class FakeObjectStorage : IObjectStorage
{
    public ObjectMetadata? Metadata { get; set; }
    public byte[]? Content { get; set; } = TestFiles.JpegHeader;
    public string? UploadBucket { get; private set; }
    public string? UploadKey { get; private set; }
    public long UploadSize { get; private set; }
    public string? DownloadBucket { get; private set; }
    public string? DownloadKey { get; private set; }
    public List<string> DownloadKeys { get; } = [];
    public int MetadataRequests { get; private set; }
    public int PrefixReads { get; private set; }
    public List<string> DeletedKeys { get; } = [];
    public List<(string Bucket, string Key)> DeletedObjects { get; } = [];
    public Func<string, bool> FailDeleteFor { get; set; } = _ => false;
    public HashSet<string> MissingBuckets { get; } = [];

    public Task<PresignedObjectRequest> CreateUploadRequestAsync(
        string bucketName,
        string storageKey,
        string contentType,
        long size,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        UploadBucket = bucketName;
        UploadKey = storageKey;
        UploadSize = size;
        return Task.FromResult(new PresignedObjectRequest(
            "https://s3.example/upload",
            new Dictionary<string, string> { ["Content-Type"] = contentType }));
    }

    public Task<string> CreateDownloadUrlAsync(
        string bucketName,
        string storageKey,
        string originalFileName,
        string contentType,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken = default)
    {
        DownloadBucket = bucketName;
        DownloadKey = storageKey;
        DownloadKeys.Add(storageKey);
        return Task.FromResult("https://s3.example/download");
    }

    public Task<ObjectMetadata?> GetMetadataAsync(
        string bucketName,
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        MetadataRequests++;
        return Task.FromResult(Metadata);
    }

    public Task<byte[]?> ReadPrefixAsync(
        string bucketName,
        string storageKey,
        int count,
        CancellationToken cancellationToken = default)
    {
        PrefixReads++;
        return Task.FromResult(Content?.Take(count).ToArray());
    }

    public Task DeleteAsync(string bucketName, string storageKey, CancellationToken cancellationToken = default)
    {
        if (FailDeleteFor(storageKey))
        {
            throw new ObjectStorageUnavailableException("Simulated S3 failure.");
        }

        DeletedKeys.Add(storageKey);
        DeletedObjects.Add((bucketName, storageKey));
        return Task.CompletedTask;
    }

    public Task<bool> BucketExistsAsync(string bucketName, CancellationToken cancellationToken = default) =>
        Task.FromResult(!MissingBuckets.Contains(bucketName));
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionStringVariable = "FILESERVICE_TEST_POSTGRES";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionStringVariable)))
        {
            Skip = $"Set {ConnectionStringVariable} to run PostgreSQL integration tests.";
        }
    }
}

// A throwaway database per test. The files schema name is fixed, so schemas can't isolate tests.
internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _name;

    private TemporaryDatabase(string adminConnectionString, string name, string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _name = name;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public static async Task<TemporaryDatabase> CreateAsync()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionStringVariable)
            ?? throw new InvalidOperationException("PostgreSQL test connection string is unavailable.");
        var name = $"fs_test_{Guid.NewGuid():N}";
        await ExecuteAsync(adminConnectionString, $"CREATE DATABASE \"{name}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = name }.ConnectionString;
        return new TemporaryDatabase(adminConnectionString, name, connectionString);
    }

    public FilesDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<FilesDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "files"))
            .Options);

    public Task ExecuteAsync(string commandText) => ExecuteAsync(ConnectionString, commandText);

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAsync(_adminConnectionString, $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");
    }

    private static async Task ExecuteAsync(string connectionString, string commandText)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(commandText, connection);
        await command.ExecuteNonQueryAsync();
    }
}
