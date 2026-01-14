using SimpleIpc;
using System.Diagnostics;

namespace IpcParent;

// Message types shared with child
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

        Console.WriteLine("=== SimpleIpc Demo ===\n");

        await using var connection = await IpcParentConnection.StartChildAsync(childPath);
        Console.WriteLine($"[Parent] Connected to child (PID: {connection.ChildProcessId})\n");

        // Register handler for requests from child
        connection.On<ParentInfoRequest, ParentInfoResponse>(request =>
        {
            Console.WriteLine($"[Parent] Child asked: {request.Query}");
            return new ParentInfoResponse($"Parent is '{Process.GetCurrentProcess().ProcessName}' (PID: {Environment.ProcessId})");
        });

        // Register handler for one-way messages from child
        connection.On<StatusUpdate>(status =>
        {
            Console.WriteLine($"[Parent] Child says: {status.Message}");
        });

        // Request-Response pattern
        Console.WriteLine("--- Request-Response ---");
        var result = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("+", 10, 5));
        Console.WriteLine($"[Parent] 10 + 5 = {result.Result}");

        result = await connection.RequestAsync<CalculateRequest, CalculateResponse>(
            new CalculateRequest("*", 7, 6));
        Console.WriteLine($"[Parent] 7 * 6 = {result.Result}\n");

        // Concurrent requests
        Console.WriteLine("--- Concurrent Requests ---");
        var tasks = new[]
        {
            connection.RequestAsync<CalculateRequest, CalculateResponse>(new CalculateRequest("+", 100, 50)),
            connection.RequestAsync<CalculateRequest, CalculateResponse>(new CalculateRequest("-", 75, 25)),
            connection.RequestAsync<CalculateRequest, CalculateResponse>(new CalculateRequest("*", 8, 9))
        };

        var results = await Task.WhenAll(tasks);
        Console.WriteLine($"[Parent] Results: {results[0].Result}, {results[1].Result}, {results[2].Result}\n");

        // One-way messages
        Console.WriteLine("--- One-Way Messages ---");
        await connection.SendAsync(new StatusUpdate("Hello from parent", DateTime.UtcNow));
        Console.WriteLine("[Parent] Sent notification\n");

        await Task.Delay(100);

        // Error handling
        Console.WriteLine("--- Error Handling ---");
        try
        {
            await connection.RequestAsync<CalculateRequest, CalculateResponse>(
                new CalculateRequest("/", 10, 0));
        }
        catch (IpcRemoteException ex)
        {
            Console.WriteLine($"[Parent] Remote error: {ex.Message}");
        }

        Console.WriteLine("\n[Parent] Demo complete. Press any key to exit.");
        Console.ReadKey();
    }

    static string GetChildProcessPath()
    {
        var currentDir = AppContext.BaseDirectory;

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

        throw new FileNotFoundException("Could not find IpcChild.exe. Build the IpcChild project first.");
    }
}
