using System;

namespace SimpleIpc;

/// <summary>
/// Provides data for the <see cref="IpcParentConnection.ChildRestarted"/> event.
/// </summary>
public sealed class ChildRestartedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChildRestartedEventArgs"/> class.
    /// </summary>
    public ChildRestartedEventArgs(int childProcessId, int attempts)
    {
        ChildProcessId = childProcessId;
        Attempts = attempts;
    }

    /// <summary>
    /// Gets the process ID of the newly started child process.
    /// </summary>
    public int ChildProcessId { get; }

    /// <summary>
    /// Gets the number of restart attempts it took to successfully reconnect.
    /// </summary>
    public int Attempts { get; }
}

/// <summary>
/// Provides data for the <see cref="IpcParentConnection.ChildRestartFailed"/> event.
/// </summary>
public sealed class ChildRestartFailedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChildRestartFailedEventArgs"/> class.
    /// </summary>
    public ChildRestartFailedEventArgs(int attempts)
    {
        Attempts = attempts;
    }

    /// <summary>
    /// Gets the number of restart attempts made before giving up.
    /// </summary>
    public int Attempts { get; }
}
