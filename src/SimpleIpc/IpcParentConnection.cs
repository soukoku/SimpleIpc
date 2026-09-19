#if NET462
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
#else
using System.Diagnostics;
using System.IO.Pipes;
#endif

namespace SimpleIpc;

/// <summary>
/// Manages a parent-side IPC connection to a child process via named pipes.
/// Unless <see cref="IpcParentConnectionOptions.AutoRestartChild"/> is disabled, the connection will
/// automatically restart the child process and re-establish the pipe whenever the child exits
/// unexpectedly, until the connection is disposed. Registered handlers and event subscriptions are
/// preserved across restarts since the same <see cref="IpcParentConnection"/> instance keeps running.
/// </summary>
public sealed class IpcParentConnection : IpcConnection
{
    /// <summary>
    /// The command-line argument name used to pass the parent process ID.
    /// </summary>
    public const string ParentPidArg = "--parent-pid";

    private readonly IpcParentConnectionOptions _options;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Process _childProcess;
    private bool _disposed;
    private bool _started;
    private int _restartAttempt;

    /// <summary>
    /// Gets the process ID of the current child process. This changes if the child is
    /// automatically restarted.
    /// </summary>
    public int ChildProcessId { get; private set; }

    /// <summary>
    /// Occurs after the child process has been automatically restarted and a new connection
    /// established. Handlers registered via <see cref="IpcConnection.On{TMessage}(Action{TMessage})"/>
    /// and overloads remain in effect; only <see cref="IpcConnection.DisconnectedToken"/> changes.
    /// </summary>
    public event EventHandler<ChildRestartedEventArgs>? ChildRestarted;

    /// <summary>
    /// Occurs when auto-restart is enabled but the child could not be restarted within
    /// <see cref="IpcParentConnectionOptions.MaxRestartAttempts"/> attempts. The connection is no
    /// longer usable at that point and should be disposed.
    /// </summary>
    public event EventHandler<ChildRestartFailedEventArgs>? ChildRestartFailed;

    private IpcParentConnection(
        Process childProcess,
        NamedPipeClientStream pipeClient,
        IpcParentConnectionOptions options)
        : base(pipeClient, options.Serializer)
    {
        _options = options;
        _childProcess = childProcess;
        ChildProcessId = childProcess.Id;
        AttachChildProcess(childProcess);
    }

    /// <summary>
    /// Starts the message loop to begin processing messages from the child.
    /// Call this after registering all message handlers.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if already started.</exception>
    public void Start()
    {
        ThrowIfDisposed();
        if (_started)
            throw new InvalidOperationException("Connection has already been started.");
        _started = true;
        StartMessageLoop();
    }

    private void AttachChildProcess(Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnChildExited;
    }

    private async void OnChildExited(object? sender, EventArgs e)
    {
        RaiseDisconnected();

        if (_disposed || !_options.AutoRestartChild)
            return;

        try
        {
            await RestartChildLoopAsync().ConfigureAwait(false);
        }
        catch
        {
            // Restart loop already reports failures via ChildRestartFailed; never let an
            // unhandled exception escape this fire-and-forget event handler.
        }
    }

    private async Task RestartChildLoopAsync()
    {
        while (true)
        {
            if (_disposed || _lifetimeCts.IsCancellationRequested)
                return;

            _restartAttempt++;
            if (_options.MaxRestartAttempts.HasValue && _restartAttempt > _options.MaxRestartAttempts.Value)
            {
                ChildRestartFailed?.Invoke(this, new ChildRestartFailedEventArgs(_restartAttempt - 1));
                return;
            }

            try
            {
                await Task.Delay(_options.RestartDelay, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed)
                return;

            Process newProcess;
            NamedPipeClientStream newPipe;
            try
            {
                (newProcess, newPipe) = await LaunchChildAndConnectAsync(
                    _options.ChildExecutablePath, _options.ConnectionTimeout, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch
            {
                // This attempt failed; loop around and try again after the next delay.
                continue;
            }

            await _stateLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed)
                {
                    KillAndDispose(newProcess);
#if NET462
                    newPipe.Dispose();
#else
                    await newPipe.DisposeAsync().ConfigureAwait(false);
#endif
                    return;
                }

                var oldProcess = _childProcess;
                oldProcess.Exited -= OnChildExited;
                oldProcess.Dispose();

                _childProcess = newProcess;
                ChildProcessId = newProcess.Id;
                AttachChildProcess(newProcess);

                Rebind(newPipe);

                var attempts = _restartAttempt;
                _restartAttempt = 0;
                ChildRestarted?.Invoke(this, new ChildRestartedEventArgs(newProcess.Id, attempts));
                return;
            }
            finally
            {
                _stateLock.Release();
            }
        }
    }

    /// <summary>
    /// Starts a child process and establishes an IPC connection using the default serializer.
    /// </summary>
    /// <param name="childExecutablePath">Path to the child executable.</param>
    /// <param name="connectionTimeout">Timeout for establishing connection. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static Task<IpcParentConnection> StartChildAsync(
        string childExecutablePath,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return StartChildAsync(childExecutablePath, SystemTextJsonSerializer.Default, connectionTimeout, cancellationToken);
    }

    /// <summary>
    /// Starts a child process and establishes an IPC connection using a custom serializer.
    /// The child will be automatically restarted if it exits unexpectedly. Use the
    /// <see cref="StartChildAsync(IpcParentConnectionOptions, CancellationToken)"/> overload to
    /// customize or disable this behavior.
    /// </summary>
    /// <param name="childExecutablePath">Path to the child executable.</param>
    /// <param name="serializer">The serializer to use for messages.</param>
    /// <param name="connectionTimeout">Timeout for establishing connection. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static Task<IpcParentConnection> StartChildAsync(
        string childExecutablePath,
        IIpcSerializer serializer,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (serializer is null)
            throw new ArgumentNullException(nameof(serializer));

        var options = new IpcParentConnectionOptions
        {
            ChildExecutablePath = childExecutablePath,
            Serializer = serializer,
            ConnectionTimeout = connectionTimeout ?? TimeSpan.FromSeconds(10),
        };

        return StartChildAsync(options, cancellationToken);
    }

    /// <summary>
    /// Starts a child process and establishes an IPC connection using the supplied options, including
    /// control over automatic restart behavior.
    /// </summary>
    /// <param name="options">Options describing the child process and reconnect behavior.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static async Task<IpcParentConnection> StartChildAsync(
        IpcParentConnectionOptions options,
        CancellationToken cancellationToken = default)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        if (options.Serializer is null)
            throw new ArgumentNullException(nameof(options), "Options.Serializer must not be null.");
        if (string.IsNullOrEmpty(options.ChildExecutablePath))
            throw new ArgumentException("Options.ChildExecutablePath must be set.", nameof(options));

        var (process, pipe) = await LaunchChildAndConnectAsync(
            options.ChildExecutablePath, options.ConnectionTimeout, cancellationToken).ConfigureAwait(false);

        return new IpcParentConnection(process, pipe, options);
    }

    private static async Task<(Process Process, NamedPipeClientStream Pipe)> LaunchChildAndConnectAsync(
        string childExecutablePath,
        TimeSpan connectionTimeout,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(childExecutablePath))
        {
            throw new FileNotFoundException(
                $"Child executable not found: {childExecutablePath}",
                childExecutablePath);
        }

        var pipeName = $"Ipc_{Guid.NewGuid():N}";
#if NET462
        var parentPid = Process.GetCurrentProcess().Id;
#else
        var parentPid = Environment.ProcessId;
#endif

        Process? childProcess = null;
        NamedPipeClientStream? pipeClient = null;

        try
        {
            childProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = childExecutablePath,
                    Arguments = $"{IpcChildConnection.PipeNameArg} {pipeName} {ParentPidArg} {parentPid}",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            childProcess.Start();

            pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

#if NET462
            using (var timeoutCts = new CancellationTokenSource(connectionTimeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                await Task.Run(() => pipeClient.Connect((int)connectionTimeout.TotalMilliseconds), linkedCts.Token).ConfigureAwait(false);
            }
#else
            await pipeClient.ConnectAsync((int)connectionTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
#endif

            return (childProcess, pipeClient);
        }
        catch
        {
#if NET462
            pipeClient?.Dispose();
#else
            if (pipeClient != null)
            {
                await pipeClient.DisposeAsync().ConfigureAwait(false);
            }
#endif

            if (childProcess != null)
            {
                KillAndDispose(childProcess);
            }

            throw;
        }
    }

    private static void KillAndDispose(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Process may have exited concurrently or already be inaccessible; nothing more to do.
        }
        process.Dispose();
    }

    /// <summary>
    /// Waits for the current child process to exit. Note that if auto-restart is enabled, a new
    /// child process may be started immediately afterward.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var process = _childProcess;
#if NET462
        await Task.Run(() => process.WaitForExit(), cancellationToken).ConfigureAwait(false);
#else
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#endif
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed) return;

        _stateLock.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCts.Cancel();

            _childProcess.Exited -= OnChildExited;

            base.DisposeCore();

            KillAndDispose(_childProcess);
        }
        finally
        {
            _stateLock.Release();
        }

        _lifetimeCts.Dispose();
    }

#if !NET462
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        await _stateLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            _lifetimeCts.Cancel();

            _childProcess.Exited -= OnChildExited;

            await base.DisposeCoreAsync().ConfigureAwait(false);

            KillAndDispose(_childProcess);
        }
        finally
        {
            _stateLock.Release();
        }

        _lifetimeCts.Dispose();
    }
#endif
}
