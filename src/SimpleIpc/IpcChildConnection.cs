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
#if NET462
public sealed class IpcChildConnection : IDisposable
#else
public sealed class IpcChildConnection : IAsyncDisposable, IDisposable
#endif
{
    /// <summary>
    /// The command-line argument name used to pass the pipe name.
    /// </summary>
    public const string PipeNameArg = "--ipc-pipe";

    private readonly NamedPipeServerStream _pipeServer;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly IIpcSerializer _serializer;
    private readonly Process? _parentProcess;
    private readonly CancellationTokenSource _disconnectCts = new();
    private bool _disposed;

    /// <summary>
    /// Gets the parent process ID, if provided.
    /// </summary>
    public int? ParentProcessId { get; }

    /// <summary>
    /// Occurs when the parent process exits or the connection is lost.
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Gets a cancellation token that is cancelled when disconnected from the parent.
    /// </summary>
    public CancellationToken DisconnectedToken => _disconnectCts.Token;

    /// <summary>
    /// Gets a value indicating whether the connection is still active.
    /// </summary>
    public bool IsConnected => !_disposed && !_disconnectCts.IsCancellationRequested
        && _pipeServer.IsConnected;

    private IpcChildConnection(NamedPipeServerStream pipeServer, int? parentPid, IIpcSerializer serializer)
    {
        ParentProcessId = parentPid;
        _pipeServer = pipeServer;
        _reader = new StreamReader(_pipeServer);
        _writer = new StreamWriter(_pipeServer) { AutoFlush = true };
        _serializer = serializer;

        // Start monitoring parent process if PID was provided
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
                // Parent process already exited
                RaiseDisconnected();
            }
        }
    }

    private void OnParentExited(object? sender, EventArgs e)
    {
        RaiseDisconnected();
    }

    private void RaiseDisconnected()
    {
        if (!_disconnectCts.IsCancellationRequested)
        {
            _disconnectCts.Cancel();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Creates a new IPC connection using the pipe name from command-line arguments and the default serializer.
    /// </summary>
    public static Task<IpcChildConnection> CreateAndWaitForConnectionAsync(
        string[] args,
        TimeSpan? connectionTimeout = null,
        CancellationToken cancellationToken = default)
    {
        return CreateAndWaitForConnectionAsync(args, SystemTextJsonSerializer.Default, connectionTimeout, cancellationToken);
    }

    /// <summary>
    /// Creates a new IPC connection using the pipe name from command-line arguments and a custom serializer.
    /// </summary>
    public static async Task<IpcChildConnection> CreateAndWaitForConnectionAsync(
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

    private async Task<string?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
#if NET462
            return await Task.Run(() => _reader.ReadLine(), linkedCts.Token);
#else
            return await _reader.ReadLineAsync(linkedCts.Token);
#endif
        }
        catch (IOException)
        {
            RaiseDisconnected();
            return null;
        }
        catch (OperationCanceledException) when (_disconnectCts.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
#if NET462
            await Task.Run(() => _writer.WriteLine(message), linkedCts.Token);
#else
            await _writer.WriteLineAsync(message.AsMemory(), linkedCts.Token);
#endif
        }
        catch (IOException ex)
        {
            RaiseDisconnected();
            throw new IpcDisconnectedException("Connection to parent process was lost.", ex);
        }
        catch (OperationCanceledException) when (_disconnectCts.IsCancellationRequested)
        {
            throw new IpcDisconnectedException("Connection to parent process was lost.");
        }
    }

    private void ThrowIfDisposed()
    {
#if NET462
        if (_disposed)
        {
            throw new ObjectDisposedException(GetType().FullName);
        }
#else
        ObjectDisposedException.ThrowIf(_disposed, this);
#endif
    }

    /// <summary>
    /// Reads and deserializes a message from the parent process.
    /// </summary>
    public async Task<T?> ReadAsync<T>(CancellationToken cancellationToken = default)
    {
        var data = await ReadMessageAsync(cancellationToken);
        return _serializer.Deserialize<T>(data);
    }

    /// <summary>
    /// Serializes and sends an object as a message to the parent process.
    /// </summary>
    public async Task SendAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        var data = _serializer.Serialize(value);
        await SendMessageAsync(data, cancellationToken);
    }

    /// <summary>
    /// Releases all resources used by the connection.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parentProcess != null)
        {
            _parentProcess.Exited -= OnParentExited;
            _parentProcess.Dispose();
        }

        _disconnectCts.Dispose();
        _writer.Dispose();
        _reader.Dispose();
        _pipeServer.Dispose();
    }
#if !NET462
    /// <summary>
    /// Releases all resources used by the connection.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_parentProcess != null)
        {
            _parentProcess.Exited -= OnParentExited;
            _parentProcess.Dispose();
        }

        _disconnectCts.Dispose();
        await _writer.DisposeAsync();
        _reader.Dispose();
        await _pipeServer.DisposeAsync();
    }
#endif
}
