using System;
using System.Threading;
using System.Threading.Tasks;

namespace GitHgMirror.Runner.Extensions
{
    public static class CancellationTokenExtensions
    {
        public static async Task<bool> WaitAsync(this CancellationToken cancellationToken, TimeSpan timeout)
        {
            try
            {
                await Task.Delay(timeout, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                return true;
            }

            return false;
        }
    }
}
