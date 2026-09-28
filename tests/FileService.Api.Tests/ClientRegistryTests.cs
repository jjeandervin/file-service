using FileService.Api.Configuration;
using FileService.Api.Infrastructure.Security;
using Microsoft.Extensions.Configuration;

namespace FileService.Api.Tests;

public sealed class ClientRegistryTests
{
    private const string HooviePackKey = "hooviepack-api-key-with-at-least-32-characters";
    private const string AcbfKey = "acbf-api-key-with-at-least-32-characters-too";

    [Fact]
    public void Binds_clients_from_environment_style_configuration_with_global_defaults()
    {
        var registry = FromConfiguration(new Dictionary<string, string?>
        {
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = HooviePackKey,
            ["Clients:0:KeyPrefix"] = "files",
            ["Clients:1:Id"] = "acbf",
            ["Clients:1:ApiKey"] = AcbfKey,
            ["Clients:1:KeyPrefix"] = "acbf",
            ["Clients:1:MaxFileBytes"] = "1024",
            ["Clients:1:AllowedContentTypes:0"] = "image/png",
            ["Clients:1:BucketName"] = "acbf-bucket"
        });

        Assert.False(registry.IsLegacyMode);
        var hooviePack = registry.Clients.Single(x => x.Id == "hooviepack");
        Assert.Equal("files", hooviePack.KeyPrefix);
        Assert.Equal(TestClients.GlobalBucket, hooviePack.BucketName);
        Assert.Equal(10 * 1024 * 1024, hooviePack.MaxFileBytes);
        Assert.Equal(["image/jpeg", "image/png", "image/webp"], hooviePack.AllowedContentTypes.Order());
        var acbf = registry.Clients.Single(x => x.Id == "acbf");
        Assert.Equal("acbf-bucket", acbf.BucketName);
        Assert.Equal(1024, acbf.MaxFileBytes);
        Assert.Equal(["image/png"], acbf.AllowedContentTypes);
    }

    [Fact]
    public void Legacy_mode_synthesizes_hooviepack_with_the_global_key_prefix()
    {
        var registry = FromConfiguration(new Dictionary<string, string?>
        {
            ["InternalApi:ApiKey"] = HooviePackKey
        });

        Assert.True(registry.IsLegacyMode);
        var client = Assert.Single(registry.Clients);
        Assert.Equal("hooviepack", client.Id);
        Assert.Equal("files", client.KeyPrefix);
        Assert.Equal(TestClients.GlobalBucket, client.BucketName);
        Assert.Equal(ClientRegistry.HashApiKey(HooviePackKey), client.ApiKeyHash);
    }

    [Fact]
    public void Empty_legacy_key_is_treated_as_unset()
    {
        var registry = FromConfiguration(new Dictionary<string, string?>
        {
            ["InternalApi:ApiKey"] = "",
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = HooviePackKey,
            ["Clients:0:KeyPrefix"] = "files"
        });

        Assert.False(registry.IsLegacyMode);
    }

    [Fact]
    public void Rejects_clients_and_legacy_key_together()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => FromConfiguration(new Dictionary<string, string?>
        {
            ["InternalApi:ApiKey"] = HooviePackKey,
            ["Clients:0:Id"] = "hooviepack",
            ["Clients:0:ApiKey"] = AcbfKey,
            ["Clients:0:KeyPrefix"] = "files"
        }));

        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
        AssertNoKeys(exception);
    }

    [Fact]
    public void Rejects_no_clients_at_all()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            FromConfiguration(new Dictionary<string, string?>()));

        Assert.Contains("At least one client is required", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_key_must_be_long_enough()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ClientRegistry.Create([], "too-short", TestClients.StorageOptions()));

        Assert.Contains("InternalApi: ApiKey must be at least 32 characters", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("too-short", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("HooviePack", "Id must match")]
    [InlineData("a", "Id must match")]
    [InlineData("1client", "Id must match")]
    [InlineData("client_one", "Id must match")]
    [InlineData("a-client-id-that-is-far-too-long-x", "Id must match")]
    public void Rejects_invalid_client_ids(string id, string expected)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Create(new ClientOptions { Id = id, ApiKey = HooviePackKey, KeyPrefix = "files" }));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/files")]
    [InlineData("files/../other")]
    [InlineData("files with spaces")]
    public void Rejects_invalid_key_prefixes(string keyPrefix)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Create(new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = keyPrefix }));

        Assert.Contains("KeyPrefix must match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_short_keys_invalid_limits_buckets_and_content_types_without_leaking_keys()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(new ClientOptions
        {
            Id = "acbf",
            ApiKey = "short-secret",
            KeyPrefix = "acbf",
            MaxFileBytes = 0,
            BucketName = "Not_A_Bucket",
            AllowedContentTypes = ["image/*"]
        }));

        Assert.Contains("ApiKey must be at least 32 characters", exception.Message, StringComparison.Ordinal);
        Assert.Contains("MaxFileBytes must be at least 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("BucketName must be a valid S3 bucket name", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllowedContentTypes must list at least one valid media type", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("short-secret", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_ids()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(
            new ClientOptions { Id = "acbf", ApiKey = HooviePackKey, KeyPrefix = "files" },
            new ClientOptions { Id = "acbf", ApiKey = AcbfKey, KeyPrefix = "acbf" }));

        Assert.Contains("Client ID 'acbf' is configured more than once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_duplicate_api_keys_without_leaking_them()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(
            new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = "files" },
            new ClientOptions { Id = "acbf", ApiKey = HooviePackKey, KeyPrefix = "acbf" }));

        Assert.Contains("Clients:0, Clients:1 share the same API key", exception.Message, StringComparison.Ordinal);
        AssertNoKeys(exception);
    }

    [Theory]
    [InlineData("acbf", "acbf")]
    [InlineData("acbf", "acbf/x")]
    [InlineData("acbf/x", "acbf")]
    [InlineData("acbf/", "acbf")]
    [InlineData("shared/a", "shared/a/b/c")]
    public void Rejects_overlapping_prefixes_in_the_same_bucket(string first, string second)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(
            new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = first },
            new ClientOptions { Id = "acbf", ApiKey = AcbfKey, KeyPrefix = second }));

        Assert.Contains("overlapping key prefixes", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acbf", "acbfx")]
    [InlineData("shared/a", "shared/b")]
    [InlineData("files", "Files")]
    public void Allows_prefixes_that_only_share_characters(string first, string second)
    {
        var registry = Create(
            new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = first },
            new ClientOptions { Id = "acbf", ApiKey = AcbfKey, KeyPrefix = second });

        Assert.Equal(2, registry.Clients.Count);
    }

    [Fact]
    public void Allows_the_same_prefix_in_different_buckets()
    {
        var registry = Create(
            new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = "files" },
            new ClientOptions { Id = "acbf", ApiKey = AcbfKey, KeyPrefix = "files", BucketName = "acbf-bucket" });

        Assert.Equal(2, registry.Clients.Count);
    }

    [Fact]
    public void Rejects_an_explicit_bucket_that_equals_the_global_bucket_with_overlapping_prefix()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Create(
            new ClientOptions { Id = "hooviepack", ApiKey = HooviePackKey, KeyPrefix = "files" },
            new ClientOptions { Id = "acbf", ApiKey = AcbfKey, KeyPrefix = "files/acbf", BucketName = TestClients.GlobalBucket }));

        Assert.Contains("overlapping key prefixes", exception.Message, StringComparison.Ordinal);
    }

    private static ClientRegistry Create(params ClientOptions[] clients) =>
        ClientRegistry.Create(clients, legacyApiKey: null, TestClients.StorageOptions());

    private static ClientRegistry FromConfiguration(Dictionary<string, string?> values) =>
        ClientRegistry.Create(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            TestClients.StorageOptions());

    private static void AssertNoKeys(Exception exception)
    {
        Assert.DoesNotContain(HooviePackKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(AcbfKey, exception.Message, StringComparison.Ordinal);
    }
}
