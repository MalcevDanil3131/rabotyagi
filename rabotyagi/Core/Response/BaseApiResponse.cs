using System.Net;

namespace rabotyagi.Core.Response;

public class BaseApiResponse<TResponse> where TResponse : BaseApiResponse<TResponse>
{
    protected HttpStatusCode StatusCode { get; set; }
    protected string? ErrorMessage { get; set; }

    public TResponse WithStatusCode(HttpStatusCode statusCode)
    {
        StatusCode = statusCode;
        return (TResponse)this;
    }
    
    public TResponse WithErrorMsg(string errorMsg)
    {
        ErrorMessage = errorMsg;
        return (TResponse)this;
    }
}