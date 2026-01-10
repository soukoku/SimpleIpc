#if NET462
using System;
#endif

namespace SimpleIpc;

/// <summary>
/// Exception thrown when an IPC connection is unexpectedly lost.
/// </summary>
public class IpcDisconnectedException : Exception
{
    /// <summary>
    /// Initializes a new instance with a default message.
    /// </summary>
    public IpcDisconnectedException()
        : base("The IPC connection was lost.")
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message.
    /// </summary>
    /// <param name="message">The error message.</param>
    public IpcDisconnectedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with the specified message and inner exception.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public IpcDisconnectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
