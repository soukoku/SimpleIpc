using SimpleIpc;

namespace IpcChild;

// Message types shared with parent
public record CalculateRequest(string Operation, int A, int B);
public record CalculateResponse(int Result);
public record StatusUpdate(string Message, DateTime Timestamp);
public record ParentInfoRequest(string Query);
public record ParentInfoResponse(string Info);

internal class Program
{
    static async Task Main(string[] args)
    {
        await using var connection = await IpcChildConnection.ConnectAsync(args);

        Console.WriteLine($"[Child {Environment.ProcessId}] Connected to parent (PID: {connection.ParentProcessId})");

        // Register request handler for calculations
        connection.On<CalculateRequest, CalculateResponse>(request =>
        {
            Console.WriteLine($"[Child] Calculate: {request.A} {request.Operation} {request.B}");

            int result = request.Operation switch
            {
                "+" => request.A + request.B,
                "-" => request.A - request.B,
                "*" => request.A * request.B,
                "/" when request.B != 0 => request.A / request.B,
                "/" => throw new DivideByZeroException("Cannot divide by zero"),
                _ => throw new NotSupportedException($"Unknown operation: {request.Operation}")
            };

            return new CalculateResponse(result);
        });

        // Register handler for one-way status messages
        connection.On<StatusUpdate>(status =>
        {
            Console.WriteLine($"[Child] Received: {status.Message}");
        });

        // Subscribe to disconnection
        connection.Disconnected += (_, _) =>
        {
            Console.WriteLine("[Child] Parent disconnected.");
        };

        try
        {
            // Demo: Child can request from parent
            await Task.Delay(500);
            var info = await connection.RequestAsync<ParentInfoRequest, ParentInfoResponse>(
                new ParentInfoRequest("What is your process name?"));
            Console.WriteLine($"[Child] Parent responded: {info.Info}");

            // Send a notification to parent
            await connection.SendAsync(new StatusUpdate("Child is ready", DateTime.UtcNow));

            // Wait until disconnected
            await Task.Delay(-1, connection.DisconnectedToken);
        }
        catch (OperationCanceledException)
        {
        }

        Console.WriteLine("[Child] Exiting.");
    }
}
