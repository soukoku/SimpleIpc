```markdown
# SimpleIpc

A lightweight library for inter-process communication (IPC) between a parent and child process using named pipes.

## Installation

Reference the `Soukoku.SimpleIpc` package.

## Usage

### Parent Process

```csharp
using SimpleIpc;

// Start child and connect
await using var connection = await IpcParentConnection.StartChildAsync("path/to/child.exe");

// Send a message
await connection.SendAsync(new MyRequest { Data = "hello" });

// Receive a response
var response = await connection.ReadAsync<MyResponse>();

// Wait for child to exit
await connection.WaitForExitAsync();
```

### Child Process

```csharp
using SimpleIpc;

// Connect to parent (reads pipe name from command-line args)
await using var connection = await IpcChildConnection.CreateAndWaitForConnectionAsync(args);

// Receive a message
var request = await connection.ReadAsync<MyRequest>();

// Send a response
await connection.SendAsync(new MyResponse { Result = "world" });
```

## Features

- Typed message serialization using System.Text.Json
- Custom serializer support via `IIpcSerializer`
- Automatic disconnection detection with `Disconnected` event and `DisconnectedToken`
- Connection timeout configuration
- Parent process monitoring from child

## Custom Serializer

Implement `IIpcSerializer` to use a different serialization library:

```csharp
var connection = await IpcParentConnection.StartChildAsync(
    "path/to/child.exe", 
    myCustomSerializer);
```

## License

MIT
```
