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
    private readonly Dictionary<string, TaskCompletionSource<IpcMessageEnvelope>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _messageLoop;
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
    /// Occurs when an unrequested message is received (not a response to a request).
    /// </summary>
    public event EventHandler<IpcMessageReceivedEventArgs>? MessageReceived;

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

        _messageLoop = Task.Run(MessageLoopAsync);
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

    internal IIpcSerializer GetSerializer() => _serializer;

    /// <summary>
    /// Serializes and sends an object as a message to the parent process.
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
    /// Sends a request and waits for a response from the parent process with correlation.
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

        _disconnectCts.Cancel();
        _disconnectCts.Dispose();
        _writeLock.Dispose();
        _writer.Dispose();
        _reader.Dispose();
        _pipeServer.Dispose();

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

        _disconnectCts.Cancel();
        _disconnectCts.Dispose();
        _writeLock.Dispose();
        await _writer.DisposeAsync();
        _reader.Dispose();
        await _pipeServer.DisposeAsync();

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
