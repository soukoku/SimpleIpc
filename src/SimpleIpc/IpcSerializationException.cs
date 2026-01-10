#if NET462
using System;
#endif

namespace SimpleIpc;

/// <summary>
/// Exception thrown when JSON serialization or deserialization fails during IPC communication.
/// </summary>
public class IpcSerializationException : Exception
{
    /// <summary>
    /// Gets the raw JSON string that failed to deserialize, if available.
    /// </summary>
    public string? RawJson { get; }

    public IpcSerializationException(string message, string? rawJson = null, Exception? innerException = null)
        : base(message, innerException)
    {
        RawJson = rawJson;
    }
}
