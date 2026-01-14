#if NET462
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
#else
using System.Collections.Concurrent;
using System.IO.Pipes;
#endif

namespace SimpleIpc;

/// <summary>
/// Base class for IPC connections providing common messaging functionality.
/// </summary>
public abstract class IpcConnection :
#if NET462
    IDisposable
#else
    IAsyncDisposable, IDisposable
#endif
{
    private readonly PipeStream _pipe;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly IIpcSerializer _serializer;
    private readonly CancellationTokenSource _disconnectCts = new();
    private readonly Dictionary<string, TaskCompletionSource<IpcMessageEnvelope>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Dictionary<Type, Func<object, CancellationToken, Task<object?>>> _requestHandlers = new();
    private readonly Dictionary<Type, Func<object, CancellationToken, Task>> _messageHandlers = new();
    private Task? _messageLoop;
    private bool _disposed;

    /// <summary>
    /// Occurs when the remote process exits or the connection is lost.
    /// </summary>
    public event EventHandler? Disconnected;

    /// <summary>
    /// Gets a cancellation token that is cancelled when disconnected.
    /// </summary>
    public CancellationToken DisconnectedToken => _disconnectCts.Token;

    /// <summary>
    /// Gets a value indicating whether the connection is still active.
    /// </summary>
    public bool IsConnected => !_disposed && !_disconnectCts.IsCancellationRequested && _pipe.IsConnected;

    /// <summary>
    /// Gets the serializer used for message serialization.
    /// </summary>
    protected IIpcSerializer Serializer => _serializer;

    /// <summary>
    /// Initializes a new instance of the <see cref="IpcConnection"/> class.
    /// </summary>
    protected IpcConnection(PipeStream pipe, IIpcSerializer serializer)
    {
        _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _reader = new StreamReader(_pipe);
        _writer = new StreamWriter(_pipe) { AutoFlush = true };
    }

    /// <summary>
    /// Starts the message processing loop.
    /// </summary>
    protected void StartMessageLoop()
    {
        _messageLoop = Task.Run(MessageLoopAsync);
    }

    /// <summary>
    /// Raises the <see cref="Disconnected"/> event and cancels pending requests.
    /// </summary>
    protected void RaiseDisconnected()
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
    /// Registers a handler for request messages of type <typeparamref name="TRequest"/>
    /// that return responses of type <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The response message type.</typeparam>
    /// <param name="handler">The async handler function.</param>
    public void On<TRequest, TResponse>(Func<TRequest, CancellationToken, Task<TResponse>> handler)
        where TRequest : class
        where TResponse : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        _requestHandlers[typeof(TRequest)] = async (obj, ct) =>
        {
            var response = await handler((TRequest)obj, ct);
            return response;
        };
    }

    /// <summary>
    /// Registers a handler for request messages of type <typeparamref name="TRequest"/>
    /// that return responses of type <typeparamref name="TResponse"/>.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The response message type.</typeparam>
    /// <param name="handler">The handler function.</param>
    public void On<TRequest, TResponse>(Func<TRequest, TResponse> handler)
        where TRequest : class
        where TResponse : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        _requestHandlers[typeof(TRequest)] = (obj, _) =>
        {
            var response = handler((TRequest)obj);
            return Task.FromResult<object?>(response);
        };
    }

    /// <summary>
    /// Registers a handler for one-way messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="handler">The async handler function.</param>
    public void On<TMessage>(Func<TMessage, CancellationToken, Task> handler)
        where TMessage : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        _messageHandlers[typeof(TMessage)] = async (obj, ct) =>
        {
            await handler((TMessage)obj, ct);
        };
    }

    /// <summary>
    /// Registers a handler for one-way messages of type <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The message type.</typeparam>
    /// <param name="handler">The handler action.</param>
    public void On<TMessage>(Action<TMessage> handler)
        where TMessage : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));

        _messageHandlers[typeof(TMessage)] = (obj, _) =>
        {
            handler((TMessage)obj);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Sends a one-way message to the remote process.
    /// </summary>
    /// <typeparam name="T">The message type.</typeparam>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task SendAsync<T>(T message, CancellationToken cancellationToken = default)
        where T : class
    {
        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            Type = MessageType.OneWay,
            PayloadType = typeof(T).FullName,
            PayloadJson = _serializer.Serialize(message)
        };

        await SendEnvelopeAsync(envelope, cancellationToken);
    }

    /// <summary>
    /// Sends a request and waits for a response.
    /// </summary>
    /// <typeparam name="TRequest">The request message type.</typeparam>
    /// <typeparam name="TResponse">The expected response type.</typeparam>
    /// <param name="request">The request message.</param>
    /// <param name="timeout">Optional timeout for the request. Defaults to 30 seconds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the remote process.</returns>
    /// <exception cref="IpcRemoteException">Thrown when the remote process returns an error.</exception>
    /// <exception cref="TimeoutException">Thrown when the request times out.</exception>
    public async Task<TResponse> RequestAsync<TRequest, TResponse>(
        TRequest request,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        ThrowIfDisposed();

        var actualTimeout = timeout ?? TimeSpan.FromSeconds(30);
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
                PayloadType = typeof(TRequest).FullName,
                PayloadJson = _serializer.Serialize(request)
            };

            await SendEnvelopeAsync(envelope, cancellationToken);

            using var timeoutCts = new CancellationTokenSource(actualTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _disconnectCts.Token, timeoutCts.Token);

            IpcMessageEnvelope responseEnvelope;
            try
            {
                responseEnvelope = await tcs.Task.WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Request timed out after {actualTimeout.TotalSeconds} seconds.");
            }

            if (responseEnvelope.Type == MessageType.Error)
            {
                var error = _serializer.Deserialize<IpcErrorResponse>(responseEnvelope.PayloadJson);
                throw new IpcRemoteException(error?.Message ?? "Remote error occurred.");
            }

            return _serializer.Deserialize<TResponse>(responseEnvelope.PayloadJson)
                ?? throw new IpcSerializationException("Failed to deserialize response.", responseEnvelope.PayloadJson);
        }
        finally
        {
            lock (_pendingRequests)
            {
                _pendingRequests.Remove(messageId);
            }
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
                    // Pipe closed
                    RaiseDisconnected();
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

                // Check if this is a response to a pending request
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

                // Handle incoming requests and one-way messages
                if (envelope.Type == MessageType.Request)
                {
                    _ = HandleIncomingRequestAsync(envelope);
                }
                else if (envelope.Type == MessageType.OneWay)
                {
                    _ = HandleIncomingMessageAsync(envelope);
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

    private async Task HandleIncomingRequestAsync(IpcMessageEnvelope envelope)
    {
        try
        {
            var payloadType = FindTypeByName(envelope.PayloadType);
            if (payloadType == null || !_requestHandlers.TryGetValue(payloadType, out var handler))
            {
                await SendErrorResponseAsync(envelope.MessageId!, $"No handler registered for request type: {envelope.PayloadType}");
                return;
            }

            var payload = _serializer.Deserialize(envelope.PayloadJson, payloadType);
            if (payload == null)
            {
                await SendErrorResponseAsync(envelope.MessageId!, "Failed to deserialize request payload.");
                return;
            }

            var response = await handler(payload, _disconnectCts.Token);
            if (response != null)
            {
                await SendResponseAsync(envelope.MessageId!, response);
            }
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(envelope.MessageId!, ex.Message);
        }
    }

    private async Task HandleIncomingMessageAsync(IpcMessageEnvelope envelope)
    {
        try
        {
            var payloadType = FindTypeByName(envelope.PayloadType);
            if (payloadType == null || !_messageHandlers.TryGetValue(payloadType, out var handler))
            {
                return; // Silently ignore unhandled one-way messages
            }

            var payload = _serializer.Deserialize(envelope.PayloadJson, payloadType);
            if (payload != null)
            {
                await handler(payload, _disconnectCts.Token);
            }
        }
        catch
        {
            // Silently ignore errors in one-way message handlers
        }
    }

    private static Type? FindTypeByName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // Search in all loaded assemblies
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(typeName);
            if (type != null) return type;
        }

        return Type.GetType(typeName);
    }

    private async Task SendResponseAsync(string correlationId, object response)
    {
        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            Type = MessageType.Response,
            PayloadType = response.GetType().FullName,
            PayloadJson = _serializer.Serialize(response)
        };

        await SendEnvelopeAsync(envelope, CancellationToken.None);
    }

    private async Task SendErrorResponseAsync(string correlationId, string errorMessage)
    {
        var envelope = new IpcMessageEnvelope
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            Type = MessageType.Error,
            PayloadJson = _serializer.Serialize(new IpcErrorResponse { Message = errorMessage })
        };

        await SendEnvelopeAsync(envelope, CancellationToken.None);
    }

    private async Task SendEnvelopeAsync(IpcMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var json = _serializer.Serialize(envelope);

        try
        {
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disconnectCts.Token);
#if NET462
                await Task.Run(() => _writer.WriteLine(json), linkedCts.Token);
#else
                await _writer.WriteLineAsync(json.AsMemory(), linkedCts.Token);
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
            throw new IpcDisconnectedException("Connection was lost.", ex);
        }
        catch (OperationCanceledException) when (_disconnectCts.IsCancellationRequested)
        {
            throw new IpcDisconnectedException("Connection was lost.");
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

    /// <summary>
    /// Throws if the connection has been disposed.
    /// </summary>
    protected void ThrowIfDisposed()
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
    /// Performs cleanup of base connection resources.
    /// </summary>
    protected virtual void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel first to stop message loop and pending operations
        _disconnectCts.Cancel();

        // Wait for message loop to complete (with timeout to avoid deadlock)
        try
        {
            _messageLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Ignore - loop may have been cancelled
        }

        _disconnectCts.Dispose();
        _writeLock.Dispose();
        _writer.Dispose();
        _reader.Dispose();
        _pipe.Dispose();

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
    /// Performs async cleanup of base connection resources.
    /// </summary>
    protected virtual async ValueTask DisposeCoreAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Cancel first to stop message loop and pending operations
        _disconnectCts.Cancel();

        // Wait for message loop to complete
        if (_messageLoop != null)
        {
            try
            {
                await _messageLoop.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch
            {
                // Ignore - loop may have been cancelled
            }
        }

        _disconnectCts.Dispose();
        _writeLock.Dispose();
        await _writer.DisposeAsync();
        _reader.Dispose();
        await _pipe.DisposeAsync();

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

    /// <inheritdoc />
    public abstract void Dispose();

#if !NET462
    /// <inheritdoc />
    public abstract ValueTask DisposeAsync();
#endif
}
