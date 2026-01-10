#if NET462
using System;
#endif

namespace SimpleIpc;

/// <summary>
/// Exception thrown when an IPC connection is unexpectedly lost.
/// </summary>
public class IpcDisconnectedException : Exception
{
    public IpcDisconnectedException()
        : base("The IPC connection was lost.")
    {
    }

    public IpcDisconnectedException(string message)
        : base(message)
    {
    }

    public IpcDisconnectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
