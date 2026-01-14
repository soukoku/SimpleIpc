using System;

namespace SimpleIpc;

public class IpcRemoteException : Exception
{
    public IpcRemoteException(string message) : base(message)
    {
    }

    public IpcRemoteException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
