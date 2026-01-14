using SimpleIpc;
using System.IO.Pipes;

namespace SimpleIpc.Tests;

public record TestRequest(string Data);
public record TestResponse(string Result);
public record TestNotification(string Message);

public class IpcIntegrationTests
{
    [Fact]
    public async Task RequestResponse_SyncHandler_ReturnsResponse()
    {
        // Arrange
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var _ = server;
        await using var __ = client;

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
        var (server, client) = await CreateConnectedPairAsync();
        await using var __ = client;

        var disconnectedTcs = new TaskCompletionSource();
        client.Disconnected += (_, _) => disconnectedTcs.SetResult();

        // Act
        await server.DisposeAsync();

        // Assert
        await disconnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(client.DisconnectedToken.IsCancellationRequested);
    }

    private static async Task<(TestConnection server, TestConnection client)> CreateConnectedPairAsync()
    {
        var pipeName = $"test_{Guid.NewGuid():N}";

        var serverPipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);

        var clientPipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // Connect both ends
        var serverConnectTask = serverPipe.WaitForConnectionAsync();
        await clientPipe.ConnectAsync(5000);
        await serverConnectTask;

        // Create connections after pipes are connected
        var server = new TestConnection(serverPipe);
        var client = new TestConnection(clientPipe);

        return (server, client);
    }

    private sealed class TestConnection : IpcConnection
    {
        private bool _disposed;

        public TestConnection(PipeStream pipe)
            : base(pipe, SystemTextJsonSerializer.Default)
        {
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
