using Amazon.S3;
using HooviePack.Files.Api.Application;
using HooviePack.Files.Api.Configuration;
using HooviePack.Files.Api.Infrastructure.Data;
using HooviePack.Files.Api.Infrastructure.Security;
using HooviePack.Files.Api.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<FileStorageOptions>()
    .Bind(builder.Configuration.GetSection(FileStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => options.AllowedContentTypes.Length > 0 &&
                   options.AllowedContentTypes.All(x => !string.IsNullOrWhiteSpace(x)),
        "At least one allowed content type is required.")
    .Validate(
        options => !options.KeyPrefix.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is "." or ".."),
        "FileStorage:KeyPrefix cannot contain relative path segments.")
    .Validate(
        options => string.IsNullOrWhiteSpace(options.ServiceUrl) ||
                   Uri.TryCreate(options.ServiceUrl, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
        "FileStorage:ServiceUrl must be an absolute HTTP(S) URL when provided.")
    .ValidateOnStart();
builder.Services.AddOptions<InternalApiOptions>()
    .Bind(builder.Configuration.GetSection(InternalApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required.");
}

builder.Services.AddDbContext<FilesDbContext>(options =>
    options.UseNpgsql(
        connectionString,
        npgsql =>
        {
            npgsql.EnableRetryOnFailure();
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "files");
        }));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<FileApiExceptionHandler>();
builder.Services.AddControllers();
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("File Service process is running."), tags: ["live"])
    .AddCheck<PostgresHealthCheck>(
        "postgres",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(4));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAmazonS3>(services =>
    S3ObjectStorage.CreateClient(services.GetRequiredService<IOptions<FileStorageOptions>>().Value));
builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
builder.Services.AddScoped<IFileManager, FileManager>();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<InternalApiKeyMiddleware>();

app.MapControllers();

await app.ApplyMigrationsAsync();
await app.RunAsync();

public partial class Program;
