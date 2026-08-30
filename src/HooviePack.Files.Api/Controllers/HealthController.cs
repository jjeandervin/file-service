using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HooviePack.Files.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(HealthCheckService healthCheckService, IHostEnvironment environment) : ControllerBase
{
    [HttpGet]
    [HttpGet("ready")]
    public Task<IActionResult> Ready(CancellationToken cancellationToken) =>
        CheckAsync(readiness: true, cancellationToken);

    [HttpGet("live")]
    public Task<IActionResult> Live(CancellationToken cancellationToken) =>
        CheckAsync(readiness: false, cancellationToken);

    private async Task<IActionResult> CheckAsync(bool readiness, CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(
            registration => readiness
                ? registration.Tags.Contains("ready") || registration.Tags.Contains("live")
                : registration.Tags.Contains("live"),
            cancellationToken);
        object response = environment.IsDevelopment()
            ? new
            {
                status = report.Status.ToString().ToLowerInvariant(),
                checks = report.Entries.ToDictionary(
                    x => x.Key,
                    x => new
                    {
                        status = x.Value.Status.ToString().ToLowerInvariant(),
                        description = x.Value.Description
                    })
            }
            : new { status = report.Status.ToString().ToLowerInvariant() };
        return report.Status == HealthStatus.Healthy
            ? Ok(response)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, response);
    }
}
