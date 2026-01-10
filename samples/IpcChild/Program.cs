using SimpleIpc;

namespace IpcChild;

// Sample message types
public record GreetingRequest(string Command, DateTime Timestamp);
public record GreetingResponse(string Result, string ProcessedBy);

internal class Program
{
    static async Task Main(string[] args)
    {
        await using var connection = await IpcChildConnection.CreateAndWaitForConnectionAsync(args);

        // Subscribe to disconnection event
        connection.Disconnected += (sender, e) =>
        {
            Console.Error.WriteLine("Parent disconnected! Shutting down...");
        };

        Console.Error.WriteLine($"Connected to parent (PID: {connection.ParentProcessId})");

        try
        {
            // Use DisconnectedToken to automatically cancel operations when parent exits
            while (!connection.DisconnectedToken.IsCancellationRequested)
            {
                var request = await connection.ReadAsync<GreetingRequest>();
                
                if (request is null)
                {
                    // Connection closed normally
                    break;
                }

                if (request.Command == "hello")
                {
                    var response = new GreetingResponse(
                        Result: "world",
                        ProcessedBy: $"Child PID {Environment.ProcessId}"
                    );
                    await connection.SendAsync(response);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (connection.DisconnectedToken.IsCancellationRequested)
        {
            // Parent exited, exit gracefully
        }

        Console.Error.WriteLine("Child exiting.");
    }
}
