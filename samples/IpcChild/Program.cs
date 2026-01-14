using SimpleIpc;

namespace IpcChild;

// Sample message types
public record CalculateRequest(string Operation, int A, int B);
public record CalculateResponse(int Result);
public record StatusUpdate(string Message, DateTime Timestamp);
public record ParentInfoRequest(string Query);
public record ParentInfoResponse(string Info);

internal class Program
{
    private static IpcChildConnection? _connection;
    private static int _requestCount = 0;

    static async Task Main(string[] args)
    {
        await using var connection = await IpcChildConnection.CreateAndWaitForConnectionAsync(args);
        _connection = connection;

        Console.WriteLine($"[Child {Environment.ProcessId}] Connected to parent (PID: {connection.ParentProcessId})");
        Console.WriteLine("[Child] Demonstrating multiple IPC communication patterns...\n");

        // Subscribe to disconnection event
        connection.Disconnected += (sender, e) =>
        {
            Console.WriteLine("\n[Child] Parent disconnected! Shutting down...");
        };

        // Handle incoming messages from parent
        connection.MessageReceived += async (sender, e) =>
        {
            try
            {
                if (e.IsRequest)
                {
                    await HandleRequestAsync(e);
                }
                else
                {
                    // Handle one-way messages
                    await HandleOneWayMessageAsync(e);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Child] Error handling message: {ex.Message}");
                if (e.IsRequest)
                {
                    await connection.RespondWithErrorAsync(e, $"Error: {ex.Message}");
                }
            }
        };

        try
        {
            // Demo: Child can also initiate requests to parent
            await Task.Delay(500); // Let parent set up handlers
            await DemoChildToParentCommunicationAsync(connection);

            // Wait for disconnection
            await Task.Delay(-1, connection.DisconnectedToken);
        }
        catch (OperationCanceledException)
        {
            // Parent exited, exit gracefully
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Child] Error: {ex.Message}");
        }

        Console.WriteLine("[Child] Exiting.");
    }

    private static async Task HandleRequestAsync(IpcMessageReceivedEventArgs e)
    {
        _requestCount++;
        
        // Try to deserialize as CalculateRequest
        var calcRequest = e.GetPayload<CalculateRequest>();
        if (calcRequest != null)
        {
            Console.WriteLine($"[Child] Request #{_requestCount}: Calculate {calcRequest.A} {calcRequest.Operation} {calcRequest.B}");

            int result = calcRequest.Operation switch
            {
                "+" => calcRequest.A + calcRequest.B,
                "-" => calcRequest.A - calcRequest.B,
                "*" => calcRequest.A * calcRequest.B,
                "/" when calcRequest.B != 0 => calcRequest.A / calcRequest.B,
                "/" => throw new DivideByZeroException("Cannot divide by zero"),
                _ => throw new NotSupportedException($"Unknown operation: {calcRequest.Operation}")
            };

            Console.WriteLine($"[Child] Sending response with result: {result}");
            var response = new CalculateResponse(result);
            await _connection!.RespondAsync(e, response);
            Console.WriteLine($"[Child] Response sent successfully");
            return;
        }

        // Unknown request type
        Console.WriteLine($"[Child] Received unknown request type");
        await _connection!.RespondWithErrorAsync(e, "Unknown request type");
    }

    private static async Task HandleOneWayMessageAsync(IpcMessageReceivedEventArgs e)
    {
        // Try to deserialize as StatusUpdate
        var statusUpdate = e.GetPayload<StatusUpdate>();
        if (statusUpdate != null)
        {
            Console.WriteLine($"[Child] Received one-way message: {statusUpdate.Message}");
            
            // Send acknowledgment back (one-way)
            await _connection!.SendAsync(new StatusUpdate(
                $"Child received: {statusUpdate.Message}",
                DateTime.UtcNow));
            return;
        }

        Console.WriteLine($"[Child] Received unknown one-way message");
    }

    private static async Task DemoChildToParentCommunicationAsync(IpcChildConnection connection)
    {
        try
        {
            Console.WriteLine("\n[Child] === Demonstrating Child-to-Parent Request ===");
            
            // Child can request information from parent
            var parentInfo = await connection.RequestAsync<ParentInfoRequest, ParentInfoResponse>(
                new ParentInfoRequest("What is your process name?"));
            
            Console.WriteLine($"[Child] Parent responded: {parentInfo?.Info}");

            // Child can also send one-way notifications to parent
            Console.WriteLine("[Child] Sending one-way status update to parent...");
            await connection.SendAsync(new StatusUpdate("Child is ready and waiting for requests", DateTime.UtcNow));
        }
        catch (IpcRemoteException ex)
        {
            Console.WriteLine($"[Child] Parent returned error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Child] Error communicating with parent: {ex.Message}");
        }
    }
}
