using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace rabotyagi.Core.Response;

public class FileApiResponse : BaseApiResponse<FileApiResponse>
{
    private byte[]? FileBytes { get; set; }
    private string? FileName { get; set; }
    private string ContentType { get; set; } = null!;

    public FileApiResponse WithFile(byte[] bytes, string fileName, string contentType = "application/octet-stream")
    {
        FileBytes = bytes;
        FileName = fileName;
        ContentType = contentType;
        return this;
    }
    
    public static implicit operator ActionResult(FileApiResponse response)
    {
        if (response.StatusCode >= HttpStatusCode.BadRequest)
            return new ObjectResult(new { error = response.ErrorMessage })
            {
                StatusCode = (int)response.StatusCode
            };

        if (response.FileBytes == null)
            return new StatusCodeResult((int)response.StatusCode);

        return new FileContentResult(response.FileBytes, response.ContentType)
        {
            FileDownloadName = response.FileName,
        };
    }
}