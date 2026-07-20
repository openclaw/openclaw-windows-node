using System.Threading;
using System.Threading.Tasks;

namespace OpenClawTray.Services;

internal sealed class CaptureOperationGate : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Lease(_semaphore);
    }

    public void Dispose() => _semaphore.Dispose();

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
