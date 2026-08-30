using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using HooviePack.Files.Api.Configuration;
using HooviePack.Files.Api.Domain;
using HooviePack.Files.Api.Infrastructure.Data;
using HooviePack.Files.Api.Infrastructure.Storage;
using HooviePack.Files.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HooviePack.Files.Api.Application;

public interface IFileManager
{
    Task<UploadResponse> CreateUploadAsync(
        CreateUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<FileMetadataResponse> CompleteUploadAsync(
        Guid fileId,
        CompleteUploadRequest request,
        CancellationToken cancellationToken = default);

    Task<DownloadResponse> CreateDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default);
}

public sealed class FileManager(
    FilesDbContext db,
    IObjectStorage objectStorage,
    IOptions<FileStorageOptions> options,
    TimeProvider timeProvider,
    ILogger<FileManager> logger) : IFileManager
{
    private readonly FileStorageOptions _options = options.Value;
    private readonly HashSet<string> _allowedContentTypes = new(
        options.Value.AllowedContentTypes.Select(NormalizeContentType),
        StringComparer.OrdinalIgnoreCase);

    public async Task<UploadResponse> CreateUploadAsync(
        CreateUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var originalFileName = NormalizeFileName(request.FileName);
        var contentType = NormalizeContentType(request.ContentType);
        if (!_allowedContentTypes.Contains(contentType))
        {
            throw FileApiException.BadRequest("The file content type is not supported.");
        }

        if (request.Size is <= 0 || request.Size > _options.MaxFileBytes)
        {
            throw FileApiException.BadRequest(
                $"Files must be non-empty and no larger than {_options.MaxFileBytes} bytes.");
        }

        var now = timeProvider.GetUtcNow();
        var fileId = Guid.CreateVersion7(now);
        var uploadToken = CreateUploadToken();
        var record = new FileRecord
        {
            Id = fileId,
            StorageKey = BuildStorageKey(_options.KeyPrefix, fileId),
            OriginalFileName = originalFileName,
            ContentType = contentType,
            DeclaredSize = request.Size,
            UploadTokenHash = HashUploadToken(uploadToken),
            CreatedAt = now
        };
        db.Files.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        var expiresAt = now.AddMinutes(_options.UploadUrlLifetimeMinutes);
        try
        {
            var presigned = await objectStorage.CreateUploadRequestAsync(
                record.StorageKey,
                contentType,
                request.Size,
                expiresAt,
                cancellationToken);
            return new UploadResponse(
                record.Id,
                presigned.Url,
                expiresAt,
                presigned.RequiredHeaders,
                uploadToken);
        }
        catch
        {
            db.Files.Remove(record);
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                logger.LogWarning(
                    cleanupException,
                    "Could not remove metadata for failed upload initialization {FileId}.",
                    record.Id);
            }

            throw;
        }
    }

    public async Task<FileMetadataResponse> CompleteUploadAsync(
        Guid fileId,
        CompleteUploadRequest request,
        CancellationToken cancellationToken = default)
    {
        var record = await db.Files.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken)
            ?? throw FileApiException.NotFound();
        if (!UploadTokenMatches(request.UploadToken, record.UploadTokenHash))
        {
            // Do not reveal whether a guessed FileId exists.
            throw FileApiException.NotFound();
        }

        if (record.Status == FileStatus.Ready)
        {
            return MapMetadata(record);
        }

        var metadata = await objectStorage.GetMetadataAsync(record.StorageKey, cancellationToken);
        if (metadata is null)
        {
            throw FileApiException.Conflict("The object has not been uploaded or is no longer available.");
        }

        var actualContentType = TryNormalizeContentType(metadata.ContentType);
        if (metadata.Size != record.DeclaredSize ||
            !string.Equals(actualContentType, record.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            await objectStorage.DeleteAsync(record.StorageKey, cancellationToken);
            db.Files.Remove(record);
            await db.SaveChangesAsync(cancellationToken);
            throw FileApiException.BadRequest("The uploaded object does not match its declared size and content type.");
        }

        record.ActualSize = metadata.Size;
        record.Status = FileStatus.Ready;
        record.UploadedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return MapMetadata(record);
    }

    public async Task<DownloadResponse> CreateDownloadAsync(
        Guid fileId,
        CancellationToken cancellationToken = default)
    {
        var record = await db.Files.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fileId && x.Status == FileStatus.Ready, cancellationToken)
            ?? throw FileApiException.NotFound();
        var metadata = await objectStorage.GetMetadataAsync(record.StorageKey, cancellationToken);
        if (metadata is null)
        {
            logger.LogWarning("S3 object is missing for ready file {FileId}.", record.Id);
            throw FileApiException.NotFound();
        }

        if (metadata.Size != record.ActualSize ||
            !string.Equals(TryNormalizeContentType(metadata.ContentType), record.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("S3 object metadata no longer matches file {FileId}.", record.Id);
            throw FileApiException.NotFound();
        }

        var expiresAt = timeProvider.GetUtcNow().AddMinutes(_options.DownloadUrlLifetimeMinutes);
        var url = await objectStorage.CreateDownloadUrlAsync(
            record.StorageKey,
            record.OriginalFileName,
            record.ContentType,
            expiresAt,
            cancellationToken);
        return new DownloadResponse(record.Id, url, expiresAt);
    }

    public async Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var record = await db.Files.SingleOrDefaultAsync(x => x.Id == fileId, cancellationToken)
            ?? throw FileApiException.NotFound();

        // S3 deletion is idempotent. Deleting the object first makes a metadata-save retry safe.
        await objectStorage.DeleteAsync(record.StorageKey, cancellationToken);
        db.Files.Remove(record);
        await db.SaveChangesAsync(cancellationToken);
    }

    public static string BuildStorageKey(string prefix, Guid fileId) =>
        $"{prefix.Trim().TrimEnd('/')}/{fileId:N}/original";

    private static FileMetadataResponse MapMetadata(FileRecord record) => new(
        record.Id,
        record.OriginalFileName,
        record.ContentType,
        record.ActualSize ?? record.DeclaredSize,
        record.CreatedAt);

    private static string NormalizeFileName(string fileName)
    {
        var normalizedSeparators = (fileName ?? string.Empty).Replace('\\', '/');
        var safeName = normalizedSeparators.Split('/').LastOrDefault()?.Trim() ?? string.Empty;
        if (safeName.Length is < 1 or > 255 || safeName is "." or ".." || safeName.Any(char.IsControl))
        {
            throw FileApiException.BadRequest("The file name is invalid.");
        }

        return safeName;
    }

    private static string NormalizeContentType(string contentType) =>
        TryNormalizeContentType(contentType)
        ?? throw FileApiException.BadRequest("The content type is invalid.");

    private static string? TryNormalizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 100 ||
            !MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            parsed.MediaType is null || parsed.Parameters.Count > 0 || parsed.MediaType.Contains('*'))
        {
            return null;
        }

        return parsed.MediaType.ToLowerInvariant();
    }

    private static string CreateUploadToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] HashUploadToken(string uploadToken) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(uploadToken ?? string.Empty));

    private static bool UploadTokenMatches(string uploadToken, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(HashUploadToken(uploadToken), expectedHash);
}
