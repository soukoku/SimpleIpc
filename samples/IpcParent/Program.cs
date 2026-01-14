using SimpleIpc;
using SharedMessages;

namespace SharedMessages
{
    // Shared message types - must use same namespace in both projects for type matching
    public record MathRequest(string Op, int A, int B);
    public record MathResponse(int Result);
    public record Ping(string Message);
    public record Pong(string Reply);
    public record Notification(string Text);
}

namespace IpcParent
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            var childPath = GetChildProcessPath();

            Console.WriteLine("=== SimpleIpc Demo ===\n");

            await using var connection = await IpcParentConnection.StartChildAsync(childPath);
            Console.WriteLine($"[Parent] Connected to child (PID: {connection.ChildProcessId})\n");

            // Handle ping requests from child
            connection.On<Ping, Pong>(p => new Pong($"Hi child! You said: {p.Message}"));

            // Handle one-way notifications from child
            connection.On<Notification>(n => Console.WriteLine($"  [Parent] Notification from child: {n.Text}"));

            // 1. Request-Response pattern
            Console.WriteLine("--- Request-Response ---");
            var r1 = await connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("+", 10, 5));
            Console.WriteLine($"  10 + 5 = {r1.Result}");

            var r2 = await connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("*", 7, 6));
            Console.WriteLine($"  7 * 6 = {r2.Result}\n");

            // 2. Concurrent requests
            Console.WriteLine("--- Concurrent Requests ---");
            var tasks = new[]
            {
                connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("+", 100, 50)),
                connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("-", 75, 25)),
                connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("*", 8, 9))
            };
            var results = await Task.WhenAll(tasks);
            Console.WriteLine($"  100+50={results[0].Result}, 75-25={results[1].Result}, 8*9={results[2].Result}\n");

            // 3. One-way messages
            Console.WriteLine("--- One-Way Messages ---");
            await connection.SendAsync(new Notification("Hello from parent!"));
            Console.WriteLine("  Sent notification to child\n");

            await Task.Delay(100); // Let child process the notification

            // 4. Error handling
            Console.WriteLine("--- Error Handling ---");
            try
            {
                await connection.RequestAsync<MathRequest, MathResponse>(new MathRequest("/", 10, 0));
            }
            catch (IpcRemoteException ex)
            {
                Console.WriteLine($"  Remote error caught: {ex.Message}\n");
            }

            Console.WriteLine("[Parent] Demo complete. Press any key to exit.");
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
                    return fullPath;
            }

            throw new FileNotFoundException("Could not find IpcChild.exe. Build the IpcChild project first.");
        }
    }
}
