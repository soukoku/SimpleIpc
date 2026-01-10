using SimpleIpc;

namespace IpcParent;

// Sample message types (matching child's types)
public record GreetingRequest(string Command, DateTime Timestamp);
public record GreetingResponse(string Result, string ProcessedBy);

internal class Program
{
    static async Task Main(string[] args)
    {
        var childPath = GetChildProcessPath();

        // Demo: Start multiple child processes concurrently
        var tasks = new[]
        {
            CommunicateWithChildAsync(childPath, "Child 1"),
            CommunicateWithChildAsync(childPath, "Child 2"),
            CommunicateWithChildAsync(childPath, "Child 3")
        };

        await Task.WhenAll(tasks);
        Console.WriteLine("All child processes completed.");
    }

    static async Task CommunicateWithChildAsync(string childPath, string childName)
    {
        await using var connection = await IpcParentConnection.StartChildAsync(childPath);

        Console.WriteLine($"[{childName}] Connected to child process (PID: {connection.ChildProcessId})");

        // Send typed request
        var request = new GreetingRequest("hello", DateTime.UtcNow);
        await connection.SendAsync(request);
        Console.WriteLine($"[{childName}] Sent: {request}");

        // Receive typed response
        var response = await connection.ReadAsync<GreetingResponse>();
        Console.WriteLine($"[{childName}] Received: {response}");

        await connection.WaitForExitAsync();
        Console.WriteLine($"[{childName}] Exited.");
    }

    static string GetChildProcessPath()
    {
        var currentDir = AppContext.BaseDirectory;
        var childPath = Path.GetFullPath(
            Path.Combine(currentDir, "..", "..", "..", "..",
                "IpcChild", "bin", "Debug", "net9.0", "IpcChild.exe"));

        if (!File.Exists(childPath))
        {
            childPath = Path.GetFullPath(
                Path.Combine(currentDir, "..", "..", "..", "..",
                    "IpcChild", "bin", "Release", "net9.0", "IpcChild.exe"));
        }

        return childPath;
    }
}
