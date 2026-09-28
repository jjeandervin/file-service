using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FileService.Api.Application;
using FileService.Api.Configuration;
using FileService.Api.Domain;

namespace FileService.Api.Infrastructure.Security;

public sealed class FileClient(
    string id,
    string keyPrefix,
    string bucketName,
    long maxFileBytes,
    IReadOnlySet<string> allowedContentTypes,
    byte[] apiKeyHash) : ICallerClient
{
    public string Id { get; } = id;
    public string KeyPrefix { get; } = keyPrefix;
    public string BucketName { get; } = bucketName;
    public long MaxFileBytes { get; } = maxFileBytes;
    public IReadOnlySet<string> AllowedContentTypes { get; } = allowedContentTypes;
    public byte[] ApiKeyHash { get; } = apiKeyHash;
}

public sealed class CallerClientAccessor
{
    public FileClient? Client { get; set; }
}

public sealed partial class ClientRegistry
{
    public const int MinApiKeyLength = 32;

    private ClientRegistry(IReadOnlyList<FileClient> clients, bool isLegacyMode)
    {
        Clients = clients;
        IsLegacyMode = isLegacyMode;
    }

    public IReadOnlyList<FileClient> Clients { get; }

    public bool IsLegacyMode { get; }

    public static byte[] HashApiKey(string? apiKey) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? string.Empty));

    // Reads Clients (e.g. Clients__0__Id from the environment) and the legacy InternalApi:ApiKey.
    public static ClientRegistry Create(IConfiguration configuration, FileStorageOptions storage) => Create(
        configuration.GetSection(ClientOptions.SectionName).Get<List<ClientOptions>>() ?? [],
        configuration[$"{InternalApiOptions.SectionName}:{nameof(InternalApiOptions.ApiKey)}"],
        storage);

    // Validates the whole client set and throws with every problem found. Messages never include keys.
    public static ClientRegistry Create(
        IReadOnlyList<ClientOptions> configuredClients,
        string? legacyApiKey,
        FileStorageOptions storage)
    {
        var errors = new List<string>();
        var hasClients = configuredClients.Count > 0;
        var isLegacyMode = !hasClients && !string.IsNullOrEmpty(legacyApiKey);
        if (hasClients && !string.IsNullOrEmpty(legacyApiKey))
        {
            errors.Add("Configure either Clients or InternalApi:ApiKey (legacy single-key mode), not both.");
        }
        else if (!hasClients && !isLegacyMode)
        {
            errors.Add("At least one client is required. Configure Clients, or InternalApi:ApiKey for legacy single-key mode.");
        }

        IReadOnlyList<(string Label, ClientOptions Options)> candidates = isLegacyMode
            ? [("InternalApi", new ClientOptions
            {
                Id = FileRecord.DefaultClientId,
                ApiKey = legacyApiKey!,
                KeyPrefix = storage.KeyPrefix
            })]
            : configuredClients.Select((client, index) => ($"Clients:{index}", client)).ToList();

        var clients = new List<FileClient>();
        foreach (var (label, options) in candidates)
        {
            if (TryCreateClient(label, options, storage, errors) is { } client)
            {
                clients.Add(client);
            }
        }

        foreach (var duplicate in candidates.GroupBy(x => x.Options.Id, StringComparer.Ordinal).Where(x => x.Count() > 1))
        {
            errors.Add($"Client ID '{duplicate.Key}' is configured more than once.");
        }

        foreach (var duplicate in candidates
                     .Where(x => !string.IsNullOrEmpty(x.Options.ApiKey))
                     .GroupBy(x => x.Options.ApiKey, StringComparer.Ordinal)
                     .Where(x => x.Count() > 1))
        {
            errors.Add($"{string.Join(", ", duplicate.Select(x => x.Label))} share the same API key; each client needs its own.");
        }

        foreach (var bucket in clients.GroupBy(x => x.BucketName, StringComparer.Ordinal))
        {
            var inBucket = bucket.ToList();
            for (var i = 0; i < inBucket.Count; i++)
            {
                for (var j = i + 1; j < inBucket.Count; j++)
                {
                    if (PrefixesOverlap(inBucket[i].KeyPrefix, inBucket[j].KeyPrefix))
                    {
                        errors.Add(
                            $"Clients '{inBucket[i].Id}' and '{inBucket[j].Id}' have overlapping key prefixes " +
                            $"'{inBucket[i].KeyPrefix}' and '{inBucket[j].KeyPrefix}' in bucket '{bucket.Key}'.");
                    }
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid file client configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
        }

        return new ClientRegistry(clients, isLegacyMode);
    }

    public void LogConfiguration(ILogger logger)
    {
        if (IsLegacyMode)
        {
            logger.LogWarning(
                "Legacy single-key mode: InternalApi:ApiKey is set and Clients is empty, so one client '{ClientId}' was " +
                "synthesized. Configure Clients instead; this mode will be removed in 2.0.",
                FileRecord.DefaultClientId);
        }

        foreach (var client in Clients)
        {
            logger.LogInformation(
                "File client {ClientId} uses key prefix {KeyPrefix} in bucket {BucketName}.",
                client.Id,
                client.KeyPrefix,
                client.BucketName);
        }
    }

    // True when the prefixes are equal or one is a whole-segment prefix of the other ("acbf" and "acbf/x").
    public static bool PrefixesOverlap(string first, string second)
    {
        var a = PrefixSegments(first);
        var b = PrefixSegments(second);
        var shorter = Math.Min(a.Length, b.Length);
        for (var i = 0; i < shorter; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static FileClient? TryCreateClient(
        string label,
        ClientOptions options,
        FileStorageOptions storage,
        List<string> errors)
    {
        var errorCount = errors.Count;
        if (!ClientIdRegex().IsMatch(options.Id ?? string.Empty))
        {
            errors.Add($"{label}: Id must match ^[a-z][a-z0-9-]{{1,31}}$.");
        }

        if ((options.ApiKey ?? string.Empty).Length < MinApiKeyLength)
        {
            errors.Add($"{label}: ApiKey must be at least {MinApiKeyLength} characters.");
        }

        var keyPrefix = options.KeyPrefix?.Trim() ?? string.Empty;
        if (!KeyPrefixRegex().IsMatch(keyPrefix) ||
            keyPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
        {
            errors.Add($"{label}: KeyPrefix must match {FileStorageOptions.KeyPrefixPattern}.");
        }

        if (options.MaxFileBytes is < 1)
        {
            errors.Add($"{label}: MaxFileBytes must be at least 1.");
        }

        if (options.BucketName is not null &&
            (options.BucketName.Length is < 3 or > 63 || !BucketNameRegex().IsMatch(options.BucketName)))
        {
            errors.Add($"{label}: BucketName must be a valid S3 bucket name.");
        }

        var contentTypes = options.AllowedContentTypes ?? storage.AllowedContentTypes;
        var normalizedContentTypes = contentTypes.Select(MediaTypes.TryNormalize).ToList();
        if (normalizedContentTypes.Count == 0 || normalizedContentTypes.Any(x => x is null))
        {
            errors.Add($"{label}: AllowedContentTypes must list at least one valid media type.");
        }

        if (errors.Count > errorCount)
        {
            return null;
        }

        return new FileClient(
            options.Id!,
            keyPrefix,
            options.BucketName ?? storage.BucketName,
            options.MaxFileBytes ?? storage.MaxFileBytes,
            new HashSet<string>(normalizedContentTypes!, StringComparer.OrdinalIgnoreCase),
            HashApiKey(options.ApiKey));
    }

    private static string[] PrefixSegments(string prefix) =>
        prefix.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,31}$")]
    private static partial Regex ClientIdRegex();

    [GeneratedRegex(FileStorageOptions.KeyPrefixPattern)]
    private static partial Regex KeyPrefixRegex();

    [GeneratedRegex(FileStorageOptions.BucketNamePattern)]
    private static partial Regex BucketNameRegex();
}
