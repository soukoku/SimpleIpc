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

namespace IpcChild
{
    internal class Program
    {
        static async Task Main(string[] args)
        {
            await using var connection = await IpcChildConnection.ConnectAsync(args);

            Console.WriteLine($"[Child] Connected to parent (PID: {connection.ParentProcessId})");

            // Handle math requests from parent
            connection.On<MathRequest, MathResponse>(req =>
            {
                Console.WriteLine($"  [Child] Calculating: {req.A} {req.Op} {req.B}");
                int result = req.Op switch
                {
                    "+" => req.A + req.B,
                    "-" => req.A - req.B,
                    "*" => req.A * req.B,
                    "/" when req.B != 0 => req.A / req.B,
                    "/" => throw new DivideByZeroException("Cannot divide by zero"),
                    _ => throw new NotSupportedException($"Unknown: {req.Op}")
                };
                return new MathResponse(result);
            });

            // Handle one-way notifications from parent
            connection.On<Notification>(n => Console.WriteLine($"  [Child] Notification: {n.Text}"));

            // Handle disconnection
            connection.Disconnected += (_, _) => Console.WriteLine("[Child] Disconnected.");

            try
            {
                // Child can also send requests to parent
                await Task.Delay(200);
                var pong = await connection.RequestAsync<Ping, Pong>(new Ping("Hello parent!"));
                Console.WriteLine($"  [Child] Got reply: {pong.Reply}");

                // Child can send notifications too
                await connection.SendAsync(new Notification("Child ready"));

                // Wait until parent disconnects
                await Task.Delay(-1, connection.DisconnectedToken);
            }
            catch (OperationCanceledException) { }

            Console.WriteLine("[Child] Exiting.");
        }
    }
}
