#if NET462
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
#else
using System.Collections.Generic;
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
public sealed class IpcParentConnection : IAsyncDisposable, IDisposable
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
    private readonly Dictionary<string, TaskCompletionSource<IpcMessageEnvelope>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _messageLoop;
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
    /// Occurs when an unrequested message is received (not a response to a request).
    /// </summary>
    public event EventHandler<IpcMessageReceivedEventArgs>? MessageReceived;

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

        _messageLoop = Task.Run(MessageLoopAsync);
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

            lock (_pendingRequests)
            {
                foreach (var tcs in _pendingRequests.Values)
                {
                    tcs.TrySetCanceled();
                }
                _pendingRequests.Clear();
            }
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
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            // Forward child output to parent console
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

    private async Task MessageLoopAsync()
    {
        try
        {
            while (!_disconnectCts.Token.IsCancellationRequested)
            {
                var rawMessage = await ReadRawMessageAsync(_disconnectCts.Token);
                if (rawMessage == null)
                {
                    break;
                }

                IpcMessageEnvelope? envelope;
                try
                {
                    envelope = _serializer.Deserialize<IpcMessageEnvelope>(rawMessage);
                    if (envelope == null)
                    {
                        continue;
                    }
                }
                catch
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(envelope.CorrelationId))
                {
                    TaskCompletionSource<IpcMessageEnvelope>? tcs;
                    lock (_pendingRequests)
                    {
                        _pendingRequests.TryGetValue(envelope.CorrelationId, out tcs);
                        _pendingRequests.Remove(envelope.CorrelationId);
                    }

                    if (tcs != null)
                    {
                        tcs.TrySetResult(envelope);
                        continue;
                    }
                }

                if (envelope.Type == MessageType.Request || envelope.Type == MessageType.OneWay)
                {
                    var eventArgs = new IpcMessageReceivedEventArgs(envelope, this);
                    MessageReceived?.Invoke(this, eventArgs);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            RaiseDisconnected();
        }
    }

    private async Task<string?> ReadRawMessageAsync(CancellationToken cancellationToken)
    {
        try
        {
#if NET462
            return await Task.Run(() => _reader.ReadLine(), cancellationToken);
#else
            return await _reader.ReadLineAsync(cancellationToken);
#endif
        }
        catch (IOException)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
#if NET462
                await Task.Run(() => _writer.WriteLine(message), linkedCts.Token);
#else
                await _writer.WriteLineAsync(message.AsMemory(), linkedCts.Token);
#endif
            }
            finally
            {
                _writeLock.Release();
            }
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

    internal IIpcSerializer GetSerializer() => _serializer;

    /// <summary>
    /// Serializes and sends an object as a message to the child process.
    /// </summary>
    public async Task SendAsync<T>(T value, CancellationToken cancellationToken = default)
    {
        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Type = MessageType.OneWay,
            PayloadType = typeof(T).AssemblyQualifiedName,
            PayloadJson = _serializer.Serialize(value)
        };

        var envelopeJson = _serializer.Serialize(envelope);
        await SendMessageAsync(envelopeJson, cancellationToken);
    }

    /// <summary>
    /// Sends a request and waits for a response from the child process with correlation.
    /// </summary>
    public async Task<TResponse?> RequestAsync<TRequest, TResponse>(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        var messageId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<IpcMessageEnvelope>();

        lock (_pendingRequests)
        {
            _pendingRequests[messageId] = tcs;
        }

        try
        {
            var envelope = new IpcMessageEnvelope
            {
                MessageId = messageId,
                Type = MessageType.Request,
                PayloadType = typeof(TRequest).AssemblyQualifiedName,
                PayloadJson = _serializer.Serialize(request)
            };

            var envelopeJson = _serializer.Serialize(envelope);
            await SendMessageAsync(envelopeJson, cancellationToken);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
            var responseEnvelope = await tcs.Task.WaitAsync(linkedCts.Token);

            if (responseEnvelope.Type == MessageType.Error)
            {
                var error = _serializer.Deserialize<IpcErrorResponse>(responseEnvelope.PayloadJson);
                throw new IpcRemoteException(error?.Message ?? "Remote error occurred.");
            }

            return _serializer.Deserialize<TResponse>(responseEnvelope.PayloadJson);
        }
        finally
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(messageId);
            }
        }
    }

    /// <summary>
    /// Sends a response to a received request message.
    /// </summary>
    public async Task RespondAsync<T>(IpcMessageReceivedEventArgs requestEvent, T response, CancellationToken cancellationToken = default)
    {
        if (requestEvent == null)
            throw new ArgumentNullException(nameof(requestEvent));

        if (string.IsNullOrEmpty(requestEvent.MessageId))
            throw new ArgumentException("Cannot respond to a message without an ID.", nameof(requestEvent));

        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = requestEvent.MessageId,
            Type = MessageType.Response,
            PayloadType = typeof(T).AssemblyQualifiedName,
            PayloadJson = _serializer.Serialize(response)
        };

        var envelopeJson = _serializer.Serialize(envelope);
        await SendMessageAsync(envelopeJson, cancellationToken);
    }

    /// <summary>
    /// Sends an error response to a received request message.
    /// </summary>
    public async Task RespondWithErrorAsync(IpcMessageReceivedEventArgs requestEvent, string errorMessage, CancellationToken cancellationToken = default)
    {
        if (requestEvent == null)
            throw new ArgumentNullException(nameof(requestEvent));

        if (string.IsNullOrEmpty(requestEvent.MessageId))
            throw new ArgumentException("Cannot respond to a message without an ID.", nameof(requestEvent));

        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = requestEvent.MessageId,
            Type = MessageType.Error,
            PayloadJson = _serializer.Serialize(new IpcErrorResponse { Message = errorMessage })
        };

        var envelopeJson = _serializer.Serialize(envelope);
        await SendMessageAsync(envelopeJson, cancellationToken);
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

    /// <summary>
    /// Releases all resources used by the connection and terminates the child process if running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;
        _disconnectCts.Cancel();
        _disconnectCts.Dispose();
        _writeLock.Dispose();

        _writer.Dispose();
        _reader.Dispose();
        _pipeClient.Dispose();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();

        lock (_pendingRequests)
        {
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }
#if !NET462
    /// <summary>
    /// Releases all resources used by the connection and terminates the child process if running.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _childProcess.Exited -= OnChildExited;
        _disconnectCts.Cancel();
        _disconnectCts.Dispose();
        _writeLock.Dispose();

        await _writer.DisposeAsync();
        _reader.Dispose();
        await _pipeClient.DisposeAsync();

        if (!_childProcess.HasExited)
        {
            _childProcess.Kill();
        }
        _childProcess.Dispose();

        lock (_pendingRequests)
        {
            foreach (var tcs in _pendingRequests.Values)
            {
                tcs.TrySetCanceled();
            }
            _pendingRequests.Clear();
        }
    }
#endif
}
