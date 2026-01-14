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
/// Manages the child-side of an IPC connection using named pipes.
/// </summary>
public sealed class IpcChildConnection : IpcConnection
{
    /// <summary>
    /// The command-line argument name used to pass the pipe name.
    /// </summary>
    public const string PipeNameArg = "--ipc-pipe";

    private readonly Process? _parentProcess;
    private bool _disposed;

    /// <summary>
    /// Gets the parent process ID, if provided.
    /// </summary>
    public int? ParentProcessId { get; }

    private IpcChildConnection(NamedPipeServerStream pipeServer, int? parentPid, IIpcSerializer serializer)
        : base(pipeServer, serializer)
    {
        ParentProcessId = parentPid;

        if (parentPid.HasValue)
        {
            try
            {
                _parentProcess = Process.GetProcessById(parentPid.Value);
                _parentProcess.EnableRaisingEvents = true;
                _parentProcess.Exited += OnParentExited;
            }
            catch (ArgumentException)
            {
                RaiseDisconnected();
            }
        }

        StartMessageLoop();
    }

    private void OnParentExited(object? sender, EventArgs e)
    {
        RaiseDisconnected();
    }

    /// <summary>
    /// Creates a new IPC connection using the pipe name from command-line arguments and the default serializer.
    /// </summary>
    /// <param name="args">Command-line arguments containing the pipe name.</param>
    /// <param name="connectionTimeout">Timeout for parent connection. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static Task<IpcChildConnection> ConnectAsync(
        string[] args,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return ConnectAsync(args, SystemTextJsonSerializer.Default, connectionTimeout, cancellationToken);
    }

    /// <summary>
    /// Creates a new IPC connection using the pipe name from command-line arguments and a custom serializer.
    /// </summary>
    /// <param name="args">Command-line arguments containing the pipe name.</param>
    /// <param name="serializer">The serializer to use for messages.</param>
    /// <param name="connectionTimeout">Timeout for parent connection. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    public static async Task<IpcChildConnection> ConnectAsync(
        string[] args,
        IIpcSerializer serializer,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        if (serializer is null)
            throw new ArgumentNullException(nameof(serializer));

        var pipeName = GetPipeNameFromArgs(args)
            ?? throw new ArgumentException($"Missing required argument: {PipeNameArg} <name>", nameof(args));

        var parentPid = GetParentPidFromArgs(args);
        var timeout = connectionTimeout ?? TimeSpan.FromSeconds(30);

        var pipeServer = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
#if NET462
                await Task.Run(() => pipeServer.WaitForConnection(), linkedCts.Token);
#else
                await pipeServer.WaitForConnectionAsync(linkedCts.Token);
#endif
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Parent process did not connect within {timeout.TotalSeconds} seconds.");
            }

            return new IpcChildConnection(pipeServer, parentPid, serializer);
        }
        catch
        {
#if NET462
            pipeServer.Dispose();
#else
            await pipeServer.DisposeAsync();
#endif
            throw;
        }
    }

    /// <summary>
    /// Extracts the pipe name from command-line arguments.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The pipe name, or null if not found.</returns>
    public static string? GetPipeNameFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == PipeNameArg)
            {
                return args[i + 1];
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the parent process ID from command-line arguments.
    /// </summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The parent process ID, or null if not found.</returns>
    public static int? GetParentPidFromArgs(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == IpcParentConnection.ParentPidArg && int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parentProcess != null)
        {
            _parentProcess.Exited -= OnParentExited;
            _parentProcess.Dispose();
        }

        base.DisposeCore();
    }

#if !NET462
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parentProcess != null)
        {
            _parentProcess.Exited -= OnParentExited;
            _parentProcess.Dispose();
        }

        await base.DisposeCoreAsync();
    }
#endif
}
