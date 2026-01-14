# SimpleIpc Samples

This directory contains sample applications demonstrating the various IPC communication patterns supported by SimpleIpc.

## Overview

The samples consist of two applications:
- **IpcParent** - The parent process that starts and communicates with child processes
- **IpcChild** - The child process that responds to parent requests and can also initiate its own requests

## Running the Samples

1. Build both projects:
   ```bash
   dotnet build samples/IpcParent
   dotnet build samples/IpcChild
   ```

2. Run the parent application:
   ```bash
   dotnet run --project samples/IpcParent
   ```

The parent will automatically locate and start the child process, then demonstrate all communication patterns.

## Communication Patterns Demonstrated

### 1. Request-Response Pattern
**Location:** `DemoRequestResponseAsync()` in IpcParent

The parent sends calculation requests to the child and awaits responses synchronously:

```csharp
var response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
    new CalculateRequest("+", 10, 5));
Console.WriteLine($"Result: {response?.Result}");
```

The child handles these requests in the `MessageReceived` event:

```csharp
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var request = e.GetPayload<CalculateRequest>();
        int result = request.A + request.B; // Perform calculation
        await connection.RespondAsync(e, new CalculateResponse(result));
    }
};
```

### 2. Multiple Concurrent Requests
**Location:** `DemoMultipleConcurrentRequestsAsync()` in IpcParent

The parent can send multiple requests simultaneously without blocking:

```csharp
var task1 = connection.RequestAsync<CalculateRequest, CalculateResponse>(...);
var task2 = connection.RequestAsync<CalculateRequest, CalculateResponse>(...);
var task3 = connection.RequestAsync<CalculateRequest, CalculateResponse>(...);

await Task.WhenAll(task1, task2, task3);
```

Each request is tracked independently and responses are correlated back to the correct awaiter.

### 3. One-Way Messages
**Location:** `DemoOneWayMessagesAsync()` in IpcParent

Fire-and-forget messages that don't expect a response:

```csharp
await connection.SendAsync(new StatusUpdate("Notification", DateTime.UtcNow));
```

The receiver handles these through the same `MessageReceived` event, but `e.IsRequest` will be `false`:

```csharp
if (!e.IsRequest)
{
    var message = e.GetPayload<StatusUpdate>();
    Console.WriteLine($"Received: {message.Message}");
}
```

### 4. Bidirectional Communication
**Location:** `DemoChildToParentCommunicationAsync()` in IpcChild

Both parent and child can initiate requests to each other. The child can send requests to the parent:

```csharp
// In child process
var parentInfo = await connection.RequestAsync<ParentInfoRequest, ParentInfoResponse>(
    new ParentInfoRequest("What is your process name?"));
```

The parent handles these requests just like the child does:

```csharp
// In parent process
connection.MessageReceived += async (sender, e) =>
{
    if (e.IsRequest)
    {
        var request = e.GetPayload<ParentInfoRequest>();
        await connection.RespondAsync(e, new ParentInfoResponse("MyApp"));
    }
};
```

### 5. Error Handling
**Location:** `DemoErrorHandlingAsync()` in IpcParent

Demonstrates how errors are propagated across process boundaries:

**Sender side:**
```csharp
try
{
    var response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
        new CalculateRequest("/", 10, 0)); // Division by zero
}
catch (IpcRemoteException ex)
{
    Console.WriteLine($"Remote error: {ex.Message}");
}
```

**Receiver side:**
```csharp
try
{
    int result = request.A / request.B; // Throws DivideByZeroException
    await connection.RespondAsync(e, new CalculateResponse(result));
}
catch (Exception ex)
{
    await connection.RespondWithErrorAsync(e, $"Error: {ex.Message}");
}
```

## Message Types

The samples use the following message types (defined in both projects):

- **CalculateRequest** - Request to perform arithmetic calculation
- **CalculateResponse** - Result of calculation
- **StatusUpdate** - One-way notification message
- **ParentInfoRequest** - Child's request for parent information
- **ParentInfoResponse** - Parent's response with its information

## Key Takeaways

1. **Multiplexing**: Multiple concurrent requests are supported without blocking
2. **Type Safety**: All messages are strongly typed using C# records
3. **Bidirectional**: Either side can initiate requests or send notifications
4. **Error Propagation**: Exceptions in the remote process are surfaced as `IpcRemoteException`
5. **Event-Based**: All incoming messages are handled through the `MessageReceived` event
6. **Async/Await**: All communication is fully async with proper cancellation support

## Debugging Tips

- Child process writes to `Console.WriteLine()` which is visible in the parent's console
- Both processes log their actions with `[Parent]` and `[Child]` prefixes
- Use the debugger to attach to the child process for stepping through child code
- Check the `DisconnectedToken` to detect when the other process exits
