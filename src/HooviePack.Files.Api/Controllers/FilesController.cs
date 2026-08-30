using HooviePack.Files.Api.Application;
using HooviePack.Files.Domain;
using Microsoft.AspNetCore.Mvc;

namespace HooviePack.Files.Api.Controllers;

[ApiController]
[Route("files")]
public sealed class FilesController(IFileManager fileManager) : ControllerBase
{
    [HttpPost("uploads")]
    [ProducesResponseType<UploadResponse>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UploadResponse>> CreateUpload(
        [FromBody] CreateUploadRequest request,
        CancellationToken cancellationToken)
    {
        var response = await fileManager.CreateUploadAsync(request, cancellationToken);
        return Created($"/files/{response.FileId}", response);
    }

    [HttpPost("{fileId:guid}/complete")]
    [ProducesResponseType<FileMetadataResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<FileMetadataResponse>> CompleteUpload(
        Guid fileId,
        [FromBody] CompleteUploadRequest request,
        CancellationToken cancellationToken) =>
        Ok(await fileManager.CompleteUploadAsync(fileId, request, cancellationToken));

    [HttpGet("{fileId:guid}/download")]
    [ProducesResponseType<DownloadResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<DownloadResponse>> Download(
        Guid fileId,
        CancellationToken cancellationToken) =>
        Ok(await fileManager.CreateDownloadAsync(fileId, cancellationToken));

    [HttpDelete("{fileId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(Guid fileId, CancellationToken cancellationToken)
    {
        await fileManager.DeleteAsync(fileId, cancellationToken);
        return NoContent();
    }
}
