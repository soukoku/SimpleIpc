#if NET462
using System;
#endif

namespace SimpleIpc;

internal class IpcMessageEnvelope
{
    public string? MessageId { get; set; }
    public string? CorrelationId { get; set; }
    public MessageType Type { get; set; }
    public string? PayloadJson { get; set; }
    public string? PayloadType { get; set; }
}

internal enum MessageType
{
    OneWay = 0,
    Request = 1,
    Response = 2,
    Error = 3
}

internal class IpcErrorResponse
{
    public string? Message { get; set; }
}
