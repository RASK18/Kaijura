using System.Net;

namespace Kaijura.App.Services;

public sealed class JiraClientException : Exception
{
    public JiraClientException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode? StatusCode { get; }
}
