using SimpleIpc;
using System.Diagnostics;
using System.IO.Pipes;

namespace SimpleIpc.Tests;

public record TestRequest(string Data);
public record TestResponse(string Result);
public record TestNotification(string Message);

public class IpcIntegrationTests : IDisposable
{
    private readonly string _pipeName = $"test_{Guid.NewGuid():N}";

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RequestResponse_SyncHandler_ReturnsResponse()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(req => new TestResponse($"Echo: {req.Data}"));

        // Act
        var response = await client.RequestAsync<TestRequest, TestResponse>(
            new TestRequest("Hello"),
            TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal("Echo: Hello", response.Result);
    }

    [Fact]
    public async Task RequestResponse_AsyncHandler_ReturnsResponse()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(async (req, ct) =>
        {
            await Task.Delay(10, ct);
            return new TestResponse($"Async: {req.Data}");
        });

        // Act
        var response = await client.RequestAsync<TestRequest, TestResponse>(
            new TestRequest("Test"),
            TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal("Async: Test", response.Result);
    }

    [Fact]
    public async Task RequestResponse_HandlerThrows_ThrowsRemoteException()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(_ => throw new InvalidOperationException("Handler error"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync<TestRequest, TestResponse>(
                new TestRequest("Test"),
                TimeSpan.FromSeconds(5)));

        Assert.Contains("Handler error", ex.Message);
    }

    [Fact]
    public async Task RequestResponse_NoHandler_ThrowsRemoteException()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        // No handler registered

        // Act & Assert
        var ex = await Assert.ThrowsAsync<IpcRemoteException>(() =>
            client.RequestAsync<TestRequest, TestResponse>(
                new TestRequest("Test"),
                TimeSpan.FromSeconds(5)));

        Assert.Contains("No handler registered", ex.Message);
    }

    [Fact]
    public async Task RequestResponse_Timeout_ThrowsTimeoutException()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new TestResponse("Too late");
        });

        // Act & Assert
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.RequestAsync<TestRequest, TestResponse>(
                new TestRequest("Test"),
                TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task SendAsync_WithHandler_ReceivesMessage()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        var receivedMessage = new TaskCompletionSource<string>();
        server.On<TestNotification>(msg => receivedMessage.SetResult(msg.Message));

        // Act
        await client.SendAsync(new TestNotification("Fire and forget"));

        // Assert
        var result = await receivedMessage.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Fire and forget", result);
    }

    [Fact]
    public async Task ConcurrentRequests_AllSucceed()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(req => new TestResponse($"Response: {req.Data}"));

        // Act
        var tasks = Enumerable.Range(1, 10)
            .Select(i => client.RequestAsync<TestRequest, TestResponse>(
                new TestRequest($"Request{i}"),
                TimeSpan.FromSeconds(10)))
            .ToArray();

        var responses = await Task.WhenAll(tasks);

        // Assert
        Assert.All(responses, r => Assert.StartsWith("Response: Request", r.Result));
    }

    [Fact]
    public async Task Bidirectional_BothCanRequest()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        await using var server = await serverTask;
        await using var client = await clientTask;

        server.On<TestRequest, TestResponse>(req => new TestResponse($"Server: {req.Data}"));
        client.On<TestRequest, TestResponse>(req => new TestResponse($"Client: {req.Data}"));

        // Act
        var clientToServer = await client.RequestAsync<TestRequest, TestResponse>(
            new TestRequest("FromClient"),
            TimeSpan.FromSeconds(5));

        var serverToClient = await server.RequestAsync<TestRequest, TestResponse>(
            new TestRequest("FromServer"),
            TimeSpan.FromSeconds(5));

        // Assert
        Assert.Equal("Server: FromClient", clientToServer.Result);
        Assert.Equal("Client: FromServer", serverToClient.Result);
    }

    [Fact]
    public async Task Disconnect_CancelsToken()
    {
        // Arrange
        var serverTask = CreateServerConnectionAsync();
        var clientTask = CreateClientConnectionAsync();

        var server = await serverTask;
        await using var client = await clientTask;

        var disconnectedTcs = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnectedTcs.SetResult();

        // Act
        await server.DisposeAsync();

        // Assert
        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.DisconnectedToken.IsCancellationRequested);
    }

    private async Task<TestConnection> CreateServerConnectionAsync()
    {
        var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var connectTask = pipe.WaitForConnectionAsync();
        return new TestConnection(pipe, connectTask);
    }

    private async Task<TestConnection> CreateClientConnectionAsync()
    {
        var pipe = new NamedPipeClientStream(
            ".",
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        await pipe.ConnectAsync(5000);
        return new TestConnection(pipe, Task.CompletedTask);
    }

    private sealed class TestConnection : IpcConnection
    {
        private readonly Task _connectTask;
        private bool _disposed;

        public TestConnection(PipeStream pipe, Task connectTask)
            : base(pipe, SystemTextJsonSerializer.Default)
        {
            _connectTask = connectTask;
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            await _connectTask;
            StartMessageLoop();
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            DisposeCore();
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await DisposeCoreAsync();
        }
    }
}
