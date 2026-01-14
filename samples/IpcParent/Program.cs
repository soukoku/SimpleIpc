using SimpleIpc;
using System.Diagnostics;

namespace IpcParent;

// Sample message types (matching child's types)
public record CalculateRequest(string Operation, int A, int B);
public record CalculateResponse(int Result);
public record StatusUpdate(string Message, DateTime Timestamp);
public record ParentInfoRequest(string Query);
public record ParentInfoResponse(string Info);

internal class Program
{
    static async Task Main(string[] args)
    {
        var childPath = GetChildProcessPath();

        Console.WriteLine("=== SimpleIpc Communication Patterns Demo ===\n");
        Console.WriteLine("This demo showcases:");
        Console.WriteLine("  1. Request-Response pattern (with awaitable results)");
        Console.WriteLine("  2. One-way messages (fire-and-forget)");
        Console.WriteLine("  3. Bidirectional communication (child can request from parent)");
        Console.WriteLine("  4. Multiple concurrent requests");
        Console.WriteLine("  5. Error handling\n");

        await DemoAllPatternsAsync(childPath);
        
        Console.WriteLine("\n=== Demo completed ===");
    }

    static async Task DemoAllPatternsAsync(string childPath)
    {
        await using var connection = await IpcParentConnection.StartChildAsync(childPath);
        var childPid = connection.ChildProcessId;

        Console.WriteLine($"[Parent] Connected to child process (PID: {childPid})\n");

        // Set up message handler for child-to-parent communication
        connection.MessageReceived += async (sender, e) =>
        {
            try
            {
                if (e.IsRequest)
                {
                    // Handle requests from child
                    var infoRequest = e.GetPayload<ParentInfoRequest>();
                    if (infoRequest != null)
                    {
                        Console.WriteLine($"[Parent] Child requested: {infoRequest.Query}");
                        await connection.RespondAsync(e, new ParentInfoResponse(
                            $"Parent is '{Process.GetCurrentProcess().ProcessName}' (PID: {Environment.ProcessId})"));
                    }
                }
                else
                {
                    // Handle one-way messages from child
                    var statusUpdate = e.GetPayload<StatusUpdate>();
                    if (statusUpdate != null)
                    {
                        Console.WriteLine($"[Parent] Child says: {statusUpdate.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Parent] Error handling message: {ex.Message}");
            }
        };

        // Pattern 1: Request-Response (awaitable)
        await DemoRequestResponseAsync(connection);

        // Pattern 2: Multiple concurrent requests
        await DemoMultipleConcurrentRequestsAsync(connection);

        // Pattern 3: One-way messages
        await DemoOneWayMessagesAsync(connection);

        // Pattern 4: Error handling
        await DemoErrorHandlingAsync(connection);

        Console.WriteLine("\n[Parent] Demo complete. Press Ctrl+C to exit (child will be terminated).");
        await Task.Delay(-1);
    }

    static async Task DemoRequestResponseAsync(IpcParentConnection connection)
    {
        Console.WriteLine("=== Pattern 1: Request-Response ===");
        
        var response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("+", 10, 5));
        
        Console.WriteLine($"[Parent] 10 + 5 = {response?.Result}");

        response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("*", 7, 6));
        
        Console.WriteLine($"[Parent] 7 * 6 = {response?.Result}\n");
        
        await Task.Delay(100); // Brief delay to see child's logs
    }

    static async Task DemoMultipleConcurrentRequestsAsync(IpcParentConnection connection)
    {
        Console.WriteLine("=== Pattern 2: Multiple Concurrent Requests ===");
        
        var task1 = connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("+", 100, 50));
        
        var task2 = connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("-", 75, 25));
        
        var task3 = connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("*", 8, 9));

        Console.WriteLine("[Parent] Sent 3 concurrent requests...");
        
        await Task.WhenAll(task1, task2, task3);

        Console.WriteLine($"[Parent] Result 1: 100 + 50 = {task1.Result?.Result}");
        Console.WriteLine($"[Parent] Result 2: 75 - 25 = {task2.Result?.Result}");
        Console.WriteLine($"[Parent] Result 3: 8 * 9 = {task3.Result?.Result}\n");
    }

    static async Task DemoOneWayMessagesAsync(IpcParentConnection connection)
    {
        Console.WriteLine("=== Pattern 3: One-Way Messages ===");
        
        // Send fire-and-forget messages
        await connection.SendAsync(new StatusUpdate("Parent sending notification 1", DateTime.UtcNow));
        Console.WriteLine("[Parent] Sent one-way message (no response expected)");
        
        await Task.Delay(100); // Brief delay to see child's response

        await connection.SendAsync(new StatusUpdate("Parent sending notification 2", DateTime.UtcNow));
        Console.WriteLine("[Parent] Sent another one-way message\n");
        
        await Task.Delay(100); // Brief delay to see child's response
    }

    static async Task DemoErrorHandlingAsync(IpcParentConnection connection)
    {
        Console.WriteLine("=== Pattern 4: Error Handling ===");
        
        try
        {
            // This will cause a divide by zero error in the child
            var response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
                new CalculateRequest("/", 10, 0));
            
            Console.WriteLine($"[Parent] 10 / 0 = {response?.Result}");
        }
        catch (IpcRemoteException ex)
        {
            Console.WriteLine($"[Parent] Child returned error: {ex.Message}");
        }

        try
        {
            // This will cause an unknown operation error
            var response = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
                new CalculateRequest("%", 10, 3));
            
            Console.WriteLine($"[Parent] 10 % 3 = {response?.Result}");
        }
        catch (IpcRemoteException ex)
        {
            Console.WriteLine($"[Parent] Child returned error: {ex.Message}");
        }
    }

    static string GetChildProcessPath()
    {
        var currentDir = AppContext.BaseDirectory;
        
        // Try various common paths
        var possiblePaths = new[]
        {
            Path.Combine(currentDir, "..", "..", "..", "..", "IpcChild", "bin", "Debug", "net9.0", "IpcChild.exe"),
            Path.Combine(currentDir, "..", "..", "..", "..", "IpcChild", "bin", "Release", "net9.0", "IpcChild.exe"),
            Path.Combine(currentDir, "..", "..", "..", "..", "IpcChild", "bin", "Debug", "net8.0", "IpcChild.exe"),
            Path.Combine(currentDir, "..", "..", "..", "..", "IpcChild", "bin", "Release", "net8.0", "IpcChild.exe"),
        };

        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException("Could not find IpcChild.exe. Make sure to build the IpcChild project first.");
    }
}
