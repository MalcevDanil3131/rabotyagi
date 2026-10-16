using System.Net;
using Microsoft.AspNetCore.Mvc;

namespace rabotyagi.Core.Response;

public class ApiResponse : BaseApiResponse<ApiResponse>
{
    public static implicit operator ActionResult(ApiResponse response)
    {
        if (response.StatusCode >= HttpStatusCode.BadRequest)
            return new ObjectResult(new { error = response.ErrorMessage })
            {
                StatusCode = (int)response.StatusCode
            };
        
        return new StatusCodeResult((int)response.StatusCode);
    }
}

public class ApiResponse<T> : BaseApiResponse<ApiResponse<T>>
{
    private T? Data { get; set; }

    public ApiResponse<T> WithData(T data)
    {
        Data = data;
        return this;
    }
    
    public static implicit operator ActionResult(ApiResponse<T> response)
    {
        if (response.StatusCode >= HttpStatusCode.BadRequest)
            return new ObjectResult(new { error = response.ErrorMessage })
            {
                StatusCode = (int)response.StatusCode
            };
        
        if (response.Data == null)
            return new StatusCodeResult((int)response.StatusCode);
        
        return new ObjectResult(response.Data)
        {
            StatusCode = (int)response.StatusCode
        };
    }
}