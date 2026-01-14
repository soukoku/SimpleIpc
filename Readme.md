# SimpleIpc

A lightweight IPC library for bidirectional communication between parent and child processes using named pipes. Supports .NET Framework 4.6.2+ and .NET 8.0+.

## Features

- Request-response pattern with automatic correlation
- Fire-and-forget messages
- Typed handler registration
- Bidirectional communication
- Concurrent request support
- Timeout handling
- Custom serializer support

## Installation

```bash
dotnet add package Soukoku.SimpleIpc
```

## Usage

### Parent Process

```csharp
using SimpleIpc;

await using var connection = await IpcParentConnection.StartChildAsync("child.exe");

// Register handlers for messages from child
connection.On<InfoRequest, InfoResponse>(req => 
    new InfoResponse($"Parent PID: {Environment.ProcessId}"));

connection.On<StatusNotification>(msg => 
    Console.WriteLine($"Child says: {msg.Message}"));

// Send request to child
var result = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
    new CalculateRequest(10, 5, "+"));

Console.WriteLine($"Result: {result.Value}");

// Fire-and-forget
await connection.SendAsync(new StatusNotification("Hello"));
```

### Child Process

```csharp
using SimpleIpc;

await using var connection = await IpcChildConnection.ConnectAsync(args);

// Register request handler
connection.On<CalculateRequest, CalculateResponse>(req =>
{
    int result = req.Operation switch
    {
        "+" => req.A + req.B,
        "-" => req.A - req.B,
        _ => throw new NotSupportedException()
    };
    return new CalculateResponse(result);
});

// Handle one-way messages
connection.On<StatusNotification>(msg => 
    Console.WriteLine($"Parent says: {msg.Message}"));

// Child can also request from parent
var info = await connection.RequestAsync<InfoRequest, InfoResponse>(
    new InfoRequest("version"));

// Wait until disconnected
await Task.Delay(-1, connection.DisconnectedToken);
```

## Handler Registration

### Synchronous handlers

```csharp
connection.On<TRequest, TResponse>(request => new TResponse(...));
connection.On<TMessage>(message => Console.WriteLine(message));
```

### Async handlers

```csharp
connection.On<TRequest, TResponse>(async (request, ct) => {
    await Task.Delay(100, ct);
    return new TResponse(...);
});

connection.On<TMessage>(async (message, ct) => {
    await ProcessAsync(message, ct);
});
```

## Request Timeout

```csharp
var result = await connection.RequestAsync<TReq, TRes>(
    request,
    timeout: TimeSpan.FromSeconds(5));
```

## Error Handling

Exceptions thrown in handlers are propagated to the caller as `IpcRemoteException`:

```csharp
try
{
    await connection.RequestAsync<TReq, TRes>(request);
}
catch (IpcRemoteException ex)
{
    Console.WriteLine($"Remote error: {ex.Message}");
}
catch (TimeoutException ex)
{
    Console.WriteLine("Request timed out");
}
catch (IpcDisconnectedException ex)
{
    Console.WriteLine("Connection lost");
}
```

## Disconnection Handling

```csharp
connection.Disconnected += (sender, e) => Console.WriteLine("Disconnected");

// Use token for async operations
await Task.Delay(-1, connection.DisconnectedToken);

// Check status
if (connection.IsConnected) { ... }
```

## Custom Serialization

Implement `IIpcSerializer` for custom serialization:

```csharp
public class MessagePackSerializer : IIpcSerializer
{
    public string Serialize<T>(T value) { ... }
    public string Serialize(object value) { ... }
    public T? Deserialize<T>(string? data) { ... }
    public object? Deserialize(string? data, Type type) { ... }
}

// Use custom serializer
await IpcParentConnection.StartChildAsync("child.exe", new MessagePackSerializer());
await IpcChildConnection.ConnectAsync(args, new MessagePackSerializer());
```

## API Reference

### IpcParentConnection

| Member | Description |
|--------|-------------|
| `StartChildAsync(path, timeout?, ct)` | Start child and connect |
| `On<TReq, TRes>(handler)` | Register request handler |
| `On<TMsg>(handler)` | Register message handler |
| `RequestAsync<TReq, TRes>(req, timeout?, ct)` | Send request and await response |
| `SendAsync<T>(msg, ct)` | Send fire-and-forget message |
| `WaitForExitAsync(ct)` | Wait for child to exit |
| `ChildProcessId` | Child process ID |
| `IsConnected` | Connection status |
| `DisconnectedToken` | Cancellation token for disconnection |
| `Disconnected` | Disconnection event |

### IpcChildConnection

| Member | Description |
|--------|-------------|
| `ConnectAsync(args, timeout?, ct)` | Connect to parent |
| `On<TReq, TRes>(handler)` | Register request handler |
| `On<TMsg>(handler)` | Register message handler |
| `RequestAsync<TReq, TRes>(req, timeout?, ct)` | Send request and await response |
| `SendAsync<T>(msg, ct)` | Send fire-and-forget message |
| `ParentProcessId` | Parent process ID (if available) |
| `IsConnected` | Connection status |
| `DisconnectedToken` | Cancellation token for disconnection |
| `Disconnected` | Disconnection event |

## License

MIT