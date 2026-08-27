namespace FloatingTransferStation.Services;

public sealed class BoardOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly object _stateLock = new();
    private TaskCompletionSource? _drained;
    private int _registeredOperationCount;
    private bool _isSealed;

    public async Task<T> RunAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var entered = false;
        RegisterOperation();
        try
        {
            await _semaphore.WaitAsync(cancellationToken);
            entered = true;
            cancellationToken.ThrowIfCancellationRequested();
            return await operation();
        }
        finally
        {
            if (entered)
            {
                _semaphore.Release();
            }

            CompleteOperation();
        }
    }

    public async Task SealAndRunAsync(
        Func<Task> finalOperation,
        CancellationToken cancellationToken = default)
    {
        Task drained;
        lock (_stateLock)
        {
            if (_isSealed)
            {
                throw new InvalidOperationException("Board operations are closed.");
            }

            _isSealed = true;
            if (_registeredOperationCount == 0)
            {
                drained = Task.CompletedTask;
            }
            else
            {
                _drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }
        }

        try
        {
            await drained;
            cancellationToken.ThrowIfCancellationRequested();
            await finalOperation();
        }
        catch
        {
            Reopen();
            throw;
        }
    }

    private void RegisterOperation()
    {
        lock (_stateLock)
        {
            if (_isSealed)
            {
                throw new InvalidOperationException("Board operations are closed.");
            }

            _registeredOperationCount++;
        }
    }

    private void CompleteOperation()
    {
        TaskCompletionSource? drained = null;
        lock (_stateLock)
        {
            _registeredOperationCount--;
            if (_registeredOperationCount == 0)
            {
                drained = _drained;
                _drained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void Reopen()
    {
        lock (_stateLock)
        {
            _isSealed = false;
        }
    }
}
