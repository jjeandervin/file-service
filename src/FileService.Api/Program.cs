using Amazon.S3;
using FileService.Api.Application;
using FileService.Api.Configuration;
using FileService.Api.Infrastructure.Data;
using FileService.Api.Infrastructure.Security;
using FileService.Api.Infrastructure.Storage;
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
// Validates every client at startup (see the GetRequiredService call below) and throws on any problem.
builder.Services.AddSingleton(services => ClientRegistry.Create(
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<IOptions<FileStorageOptions>>().Value));

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
if (builder.Configuration.GetValue<bool>($"{FileStorageOptions.SectionName}:{nameof(FileStorageOptions.CheckBucketOnReady)}"))
{
    builder.Services.AddHealthChecks().AddCheck<S3BucketHealthCheck>(
        "s3-bucket",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"],
        timeout: TimeSpan.FromSeconds(4));
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAmazonS3>(services =>
    S3ObjectStorage.CreateClient(services.GetRequiredService<IOptions<FileStorageOptions>>().Value));
builder.Services.AddSingleton<IObjectStorage, S3ObjectStorage>();
builder.Services.AddScoped<CallerClientAccessor>();
builder.Services.AddScoped<ICallerClient>(services =>
    services.GetRequiredService<CallerClientAccessor>().Client
    ?? throw new InvalidOperationException("The request has no authenticated file client."));
builder.Services.AddScoped<IFileManager, FileManager>();
builder.Services.AddScoped<PendingUploadCleaner>();
builder.Services.AddHostedService<PendingUploadCleanupService>();

var app = builder.Build();

app.Services.GetRequiredService<ClientRegistry>().LogConfiguration(app.Logger);

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseMiddleware<ClientAuthenticationMiddleware>();

app.MapControllers();

await app.ApplyMigrationsAsync();
await app.RunAsync();

public partial class Program;
