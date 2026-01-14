# SimpleIpc

A lightweight, full-duplex IPC library for communication between parent and child processes using named pipes. Supports .NET Framework 4.6.2+ and .NET 8.0+.

## Features

- **Request-Response Pattern** - Send requests and await correlated responses in a single call
- **Multiplexed Communication** - Multiple concurrent requests without blocking
- **Bidirectional** - Both parent and child can initiate requests
- **One-Way Messages** - Fire-and-forget notifications
- **Type-Safe** - Strongly-typed message serialization using System.Text.Json
- **Event-Based** - Handle incoming messages via `MessageReceived` event
- **Error Propagation** - Remote exceptions are surfaced locally
- **Connection Monitoring** - Automatic disconnection detection with `Disconnected` event
- **Extensible** - Custom serializer support via `IIpcSerializer`

## Installation

```bash
dotnet add package Soukoku.SimpleIpc
```

## Quick Start

### Parent Process - Request-Response Pattern

```csharp
using SimpleIpc;

// Start child process and establish connection
await using var connection = await IpcParentConnection.StartChildAsync("path/to/child.exe");

// Send request and await response
var response = await connection.RequestAsync<MyRequest, MyResponse>(
    new MyRequest { Data = "hello" });

Console.WriteLine($"Child responded: {response.Result}");
```

### Child Process - Handle Requests

```csharp
using SimpleIpc;

// Connect to parent (automatically reads pipe name from command-line args)
await using var connection = await IpcChildConnection.CreateAndWaitForConnectionAsync(args);

// Handle incoming requests
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var request = e.GetPayload<MyRequest>();
        await connection.RespondAsync(e, new MyResponse { Result = "world" });
    }
};

// Keep running until disconnected
await Task.Delay(-1, connection.DisconnectedToken);
```

## Communication Patterns

### 1. Request-Response (Recommended)

Send a request and await its response in a single call. Responses are automatically correlated to their requests, allowing multiple concurrent requests.

**Parent to Child:**
```csharp
// Parent sends request
var result = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
    new CalculateRequest { A = 10, B = 5, Operation = "+" });

Console.WriteLine($"Result: {result.Value}");
```

**Child handles and responds:**
```csharp
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var request = e.GetPayload<CalculateRequest>();
        var result = request.A + request.B;
        await connection.RespondAsync(e, new CalculateResponse { Value = result });
    }
};
```

### 2. Multiple Concurrent Requests

Send multiple requests simultaneously. Each request is independently tracked and responses are correlated correctly.

```csharp
// Fire off multiple requests at once
var task1 = connection.RequestAsync<string, int>("GetNumber1");
var task2 = connection.RequestAsync<string, int>("GetNumber2");
var task3 = connection.RequestAsync<string, int>("GetNumber3");

// Wait for all responses
await Task.WhenAll(task1, task2, task3);

Console.WriteLine($"Results: {task1.Result}, {task2.Result}, {task3.Result}");
```

### 3. One-Way Messages

Send fire-and-forget messages that don't expect a response.

**Sender:**
```csharp
// Send notification without waiting for response
await connection.SendAsync(new StatusUpdate { Message = "Processing started" });
```

**Receiver:**
```csharp
connection.MessageReceived += (sender, e) =>
{
    if (!e.IsRequest) // One-way message
    {
        var status = e.GetPayload<StatusUpdate>();
        Console.WriteLine($"Status: {status.Message}");
    }
};
```

### 4. Bidirectional Communication

Both parent and child can initiate requests to each other simultaneously.

**Child requests from Parent:**
```csharp
// In child process
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var config = e.GetPayload<ConfigRequest>();
        await connection.RespondAsync(e, new ConfigResponse { Value = "setting" });
    }
};

// Child can also send requests TO parent
var parentInfo = await connection.RequestAsync<ParentInfoRequest, ParentInfoResponse>(
    new ParentInfoRequest { Query = "version" });
```

**Parent handles child requests:**
```csharp
// In parent process
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var request = e.GetPayload<ParentInfoRequest>();
        await connection.RespondAsync(e, new ParentInfoResponse { Info = "1.0.0" });
    }
};
```

## Error Handling

Exceptions thrown in the remote process are propagated as `IpcRemoteException`.

**Receiver (handling request):**
```csharp
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        try
        {
            var request = e.GetPayload<DivideRequest>();
            if (request.B == 0)
                throw new DivideByZeroException();
            
            await connection.RespondAsync(e, new DivideResponse { Result = request.A / request.B });
        }
        catch (Exception ex)
        {
            // Send error response
            await connection.RespondWithErrorAsync(e, ex.Message);
        }
    }
};
```

**Sender (making request):**
```csharp
try
{
    var result = await connection.RequestAsync<DivideRequest, DivideResponse>(
        new DivideRequest { A = 10, B = 0 });
}
catch (IpcRemoteException ex)
{
    Console.WriteLine($"Remote error: {ex.Message}"); // "Attempted to divide by zero."
}
catch (IpcDisconnectedException ex)
{
    Console.WriteLine($"Connection lost: {ex.Message}");
}
```

## Connection Management

### Disconnection Detection

Both connections provide events and tokens for monitoring disconnection.

```csharp
// Event-based notification
connection.Disconnected += (sender, e) =>
{
    Console.WriteLine("Connection lost!");
};

// Cancellation token for async operations
try
{
    await SomeLongRunningOperationAsync(connection.DisconnectedToken);
}
catch (OperationCanceledException) when (connection.DisconnectedToken.IsCancellationRequested)
{
    Console.WriteLine("Operation cancelled due to disconnection");
}

// Check connection status
if (connection.IsConnected)
{
    await connection.SendAsync(message);
}
```

### Parent-Specific Features

```csharp
await using var connection = await IpcParentConnection.StartChildAsync(
    "child.exe",
    connectionTimeout: TimeSpan.FromSeconds(10));

// Get child process ID
Console.WriteLine($"Child PID: {connection.ChildProcessId}");

// Wait for child to exit
await connection.WaitForExitAsync();

// Dispose will terminate the child process if still running
```

### Child-Specific Features

```csharp
await using var connection = await IpcChildConnection.CreateAndWaitForConnectionAsync(
    args,
    connectionTimeout: TimeSpan.FromSeconds(30));

// Get parent process ID
Console.WriteLine($"Parent PID: {connection.ParentProcessId}");

// Child automatically monitors parent and disconnects if parent exits
```

## Custom Serialization

Implement `IIpcSerializer` to use a different serialization library (e.g., MessagePack, Protobuf).

```csharp
public class MyCustomSerializer : IIpcSerializer
{
    public string Serialize<T>(T value)
    {
        // Your serialization logic
        return JsonConvert.SerializeObject(value);
    }

    public T? Deserialize<T>(string? data)
    {
        if (data == null) return default;
        return JsonConvert.DeserializeObject<T>(data);
    }
}

// Use custom serializer
var connection = await IpcParentConnection.StartChildAsync(
    "child.exe",
    new MyCustomSerializer());
```

## API Reference

### IpcParentConnection

| Method | Description |
|--------|-------------|
| `StartChildAsync(string path, IIpcSerializer? serializer, TimeSpan? timeout, CancellationToken ct)` | Starts child process and establishes connection |
| `RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken ct)` | Sends request and awaits correlated response |
| `SendAsync<T>(T value, CancellationToken ct)` | Sends one-way message |
| `RespondAsync<T>(IpcMessageReceivedEventArgs e, T response, CancellationToken ct)` | Responds to received request |
| `RespondWithErrorAsync(IpcMessageReceivedEventArgs e, string error, CancellationToken ct)` | Sends error response |
| `WaitForExitAsync(CancellationToken ct)` | Waits for child process to exit |

### IpcChildConnection

| Method | Description |
|--------|-------------|
| `CreateAndWaitForConnectionAsync(string[] args, IIpcSerializer? serializer, TimeSpan? timeout, CancellationToken ct)` | Creates connection using command-line args |
| `RequestAsync<TRequest, TResponse>(TRequest request, CancellationToken ct)` | Sends request and awaits correlated response |
| `SendAsync<T>(T value, CancellationToken ct)` | Sends one-way message |
| `RespondAsync<T>(IpcMessageReceivedEventArgs e, T response, CancellationToken ct)` | Responds to received request |
| `RespondWithErrorAsync(IpcMessageReceivedEventArgs e, string error, CancellationToken ct)` | Sends error response |

### Events

| Event | Description |
|-------|-------------|
| `MessageReceived` | Raised when a message is received (check `e.IsRequest` to determine if response is expected) |
| `Disconnected` | Raised when connection is lost or remote process exits |

### Properties

| Property | Description |
|----------|-------------|
| `IsConnected` | Gets whether connection is active |
| `DisconnectedToken` | Cancellation token that fires when disconnected |
| `ChildProcessId` / `ParentProcessId` | Process ID of remote process |

## Examples

See the `samples` directory for complete working examples demonstrating:
- Request-response pattern
- Multiple concurrent requests
- One-way messages
- Bidirectional communication
- Error handling

## License

MIT