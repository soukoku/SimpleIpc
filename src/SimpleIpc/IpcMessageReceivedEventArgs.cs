#if NET462
using System;
#endif

namespace SimpleIpc;

public class IpcMessageReceivedEventArgs : EventArgs
{
    private readonly IpcMessageEnvelope _envelope;
    private readonly IIpcSerializer _serializer;

    internal IpcMessageReceivedEventArgs(IpcMessageEnvelope envelope, IpcChildConnection connection)
    {
        _envelope = envelope;
        _serializer = connection.GetSerializer();
        MessageId = envelope.MessageId;
        IsRequest = envelope.Type == MessageType.Request;
    }

    internal IpcMessageReceivedEventArgs(IpcMessageEnvelope envelope, IpcParentConnection connection)
    {
        _envelope = envelope;
        _serializer = connection.GetSerializer();
        MessageId = envelope.MessageId;
        IsRequest = envelope.Type == MessageType.Request;
    }

    public string? MessageId { get; }
    public bool IsRequest { get; }

    public T? GetPayload<T>()
    {
        return _serializer.Deserialize<T>(_envelope.PayloadJson);
    }
}
