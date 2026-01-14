#if NET462
using System.Threading;
using System.Threading.Tasks;

namespace SimpleIpc;

internal static class TaskExtensions
{
    public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(() => tcs.TrySetResult(true)))
        {
            if (task != await Task.WhenAny(task, tcs.Task))
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        return await task;
    }
}
#endif
