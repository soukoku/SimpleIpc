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
/// </summary>
public sealed class IpcParentConnection : IpcConnection
{
    /// <summary>
    /// The command-line argument name used to pass the parent process ID.
    /// </summary>
    public const string ParentPidArg = "--parent-pid";

    private readonly Process _childProcess;
    private bool _disposed;
    private bool _started;

    /// <summary>
    /// Gets the process ID of the child process.
    /// </summary>
    public int ChildProcessId { get; }

    private IpcParentConnection(
        Process childProcess,
        NamedPipeClientStream pipeClient,
        IIpcSerializer serializer)
        : base(pipeClient, serializer)
    {
        _childProcess = childProcess;
        ChildProcessId = childProcess.Id;

        _childProcess.EnableRaisingEvents = true;
        _childProcess.Exited += OnChildExited;
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

    private void OnChildExited(object? sender, EventArgs e)
    {
        RaiseDisconnected();
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
    /// </summary>
    /// <param name="childExecutablePath">Path to the child executable.</param>
    /// <param name="serializer">The serializer to use for messages.</param>
    /// <param name="connectionTimeout">Timeout for establishing connection. Defaults to 10 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static async Task<IpcParentConnection> StartChildAsync(
        string childExecutablePath,
        IIpcSerializer serializer,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (serializer is null)
            throw new ArgumentNullException(nameof(serializer));

        if (!File.Exists(childExecutablePath))
        {
            throw new FileNotFoundException(
                $"Child executable not found: {childExecutablePath}",
                childExecutablePath);
        }

        var timeout = connectionTimeout ?? TimeSpan.FromSeconds(10);
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            childProcess.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    Console.WriteLine(e.Data);
            };
            childProcess.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                    Console.Error.WriteLine(e.Data);
            };

            childProcess.Start();
            childProcess.BeginOutputReadLine();
            childProcess.BeginErrorReadLine();

            pipeClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

#if NET462
            using (var timeoutCts = new CancellationTokenSource(timeout))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
            {
                await Task.Run(() => pipeClient.Connect((int)timeout.TotalMilliseconds), linkedCts.Token);
            }
#else
            await pipeClient.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);
#endif

            return new IpcParentConnection(childProcess, pipeClient, serializer);
        }
        catch
        {
#if NET462
            pipeClient?.Dispose();
#else
            if (pipeClient != null)
            {
                await pipeClient.DisposeAsync();
            }
#endif

            if (childProcess != null)
            {
                if (!childProcess.HasExited)
                {
                    childProcess.Kill();
                }
                childProcess.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Waits for the child process to exit.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
#if NET462
        await Task.Run(() => _childProcess.WaitForExit(), cancellationToken);
#else
        await _childProcess.WaitForExitAsync(cancellationToken);
#endif
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;

        base.DisposeCore();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();
    }

#if !NET462
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;

        await base.DisposeCoreAsync();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();
    }
#endif
}
