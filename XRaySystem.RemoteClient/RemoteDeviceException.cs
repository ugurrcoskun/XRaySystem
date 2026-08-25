using System.Net;

namespace XRaySystem.RemoteClient;

public sealed class RemoteDeviceException : Exception
{
    public RemoteDeviceException(HttpStatusCode statusCode, string errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
}
