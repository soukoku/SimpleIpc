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
#if NET462
public sealed class IpcParentConnection : IDisposable
#else
public sealed class IpcParentConnection : IAsyncDisposable
#endif
{
    /// <summary>
    /// The command-line argument name used to pass the parent process ID.
    /// </summary>
    public const string ParentPidArg = "--parent-pid";

    private readonly Process _childProcess;
    private readonly NamedPipeClientStream _pipeClient;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly IIpcSerializer _serializer;
    private readonly CancellationTokenSource _disconnectCts = new();
    private bool _disposed;

    /// <summary>
    /// Gets the process ID of the child process.
    /// </summary>
    public int ChildProcessId { get; }

    /// <summary>
    /// Occurs when the child process exits or the connection is lost.
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Gets a cancellation token that is cancelled when disconnected from the child.
    /// </summary>
    public CancellationToken DisconnectedToken => _disconnectCts.Token;

    /// <summary>
    /// Gets a value indicating whether the connection is still active.
    /// </summary>
    public bool IsConnected => !_disposed && !_disconnectCts.IsCancellationRequested 
        && _pipeClient.IsConnected && !_childProcess.HasExited;

    private IpcParentConnection(
        Process childProcess,
        NamedPipeClientStream pipeClient,
        IIpcSerializer serializer)
    {
        _childProcess = childProcess;
        _pipeClient = pipeClient;
        ChildProcessId = childProcess.Id;
        _reader = new StreamReader(_pipeClient);
        _writer = new StreamWriter(_pipeClient) { AutoFlush = true };
        _serializer = serializer;

        // Monitor child process for exit
        _childProcess.EnableRaisingEvents = true;
        _childProcess.Exited += OnChildExited;
    }

    private void OnChildExited(object? sender, EventArgs e)
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
    /// Starts a child process and establishes an IPC connection using the default serializer.
    /// </summary>
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
                    //RedirectStandardOutput = true,
                    //RedirectStandardError = true,
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
            throw new IpcDisconnectedException("Connection to child process was lost.", ex);
        }
        catch (OperationCanceledException) when (_disconnectCts.IsCancellationRequested)
        {
            throw new IpcDisconnectedException("Connection to child process was lost.");
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
    /// Reads and deserializes a message from the child process.
    /// </summary>
    public async Task<T?> ReadAsync<T>(CancellationToken cancellationToken = default)
    {
        var data = await ReadMessageAsync(cancellationToken);
        return _serializer.Deserialize<T>(data);
    }

    /// <summary>
    /// Serializes and sends an object as a message to the child process.
    /// </summary>
    public async Task SendAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        var data = _serializer.Serialize(value);
        await SendMessageAsync(data, cancellationToken);
    }

    /// <summary>
    /// Waits for the child process to exit.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
#if NET462
        await Task.Run(() => _childProcess.WaitForExit(), cancellationToken);
#else
        await _childProcess.WaitForExitAsync(cancellationToken);
#endif
    }

#if NET462
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;
        _disconnectCts.Dispose();

        _writer.Dispose();
        _reader.Dispose();
        _pipeClient.Dispose();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();
    }
#else
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;
        _disconnectCts.Dispose();

        await _writer.DisposeAsync();
        _reader.Dispose();
        await _pipeClient.DisposeAsync();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();
    }
#endif
}
