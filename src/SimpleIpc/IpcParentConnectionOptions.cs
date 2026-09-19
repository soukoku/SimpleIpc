using System;

namespace SimpleIpc;

/// <summary>
/// Options controlling how <see cref="IpcParentConnection"/> starts and supervises its child process.
/// </summary>
public sealed class IpcParentConnectionOptions
{
    /// <summary>
    /// Path to the child executable to start. Required.
    /// </summary>
    public string ChildExecutablePath { get; set; } = string.Empty;

    /// <summary>
    /// The serializer to use for messages. Defaults to <see cref="SystemTextJsonSerializer.Default"/>.
    /// </summary>
    public IIpcSerializer Serializer { get; set; } = SystemTextJsonSerializer.Default;

    /// <summary>
    /// Timeout for establishing the pipe connection, applied to both the initial connection and every
    /// automatic reconnect attempt. Defaults to 10 seconds.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether to automatically restart the child process and re-establish the pipe connection if the
    /// child exits unexpectedly. Defaults to <see langword="true"/>. Restarting only stops once the
    /// connection is disposed (or <see cref="MaxRestartAttempts"/> is reached).
    /// </summary>
    public bool AutoRestartChild { get; set; } = true;

    /// <summary>
    /// Delay before attempting to restart the child process after it exits unexpectedly.
    /// Defaults to 1 second.
    /// </summary>
    public TimeSpan RestartDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Maximum number of consecutive failed restart attempts before giving up, or <see langword="null"/>
    /// for unlimited attempts. Defaults to <see langword="null"/>.
    /// </summary>
    public int? MaxRestartAttempts { get; set; }
}
