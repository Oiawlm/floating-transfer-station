using FloatingTransferStation.Services;

namespace FloatingTransferStation.Tests;

[TestClass]
public sealed class BoardOperationGateTests
{
    [TestMethod]
    public async Task SealAndRun_WaitsForRegisteredCleanupRejectsNewOperationsAndStaysSealed()
    {
        var gate = new BoardOperationGate();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupFinished = false;
        var running = gate.RunAsync(async () =>
        {
            operationStarted.TrySetResult();
            try
            {
                await releaseOperation.Task;
            }
            finally
            {
                cleanupFinished = true;
            }

            return true;
        });
        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finalWriteStarted = false;

        var sealing = gate.SealAndRunAsync(() =>
        {
            finalWriteStarted = true;
            Assert.IsTrue(cleanupFinished);
            return Task.CompletedTask;
        });

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gate.RunAsync(() => Task.FromResult(true)));
        Assert.IsFalse(finalWriteStarted);

        releaseOperation.TrySetResult();
        await running;
        await sealing;

        Assert.IsTrue(finalWriteStarted);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => gate.RunAsync(() => Task.FromResult(true)));
    }

    [TestMethod]
    public async Task SealAndRun_FinalFailureReopensForLaterOperations()
    {
        var gate = new BoardOperationGate();

        await Assert.ThrowsExactlyAsync<IOException>(
            () => gate.SealAndRunAsync(() => throw new IOException("Injected final-write failure.")));

        var ran = false;
        await gate.RunAsync(() =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        Assert.IsTrue(ran);
    }

    [TestMethod]
    public async Task SealAndRun_CancellationReopensForLaterOperations()
    {
        var gate = new BoardOperationGate();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => gate.SealAndRunAsync(() => Task.CompletedTask, cancellation.Token));

        var ran = false;
        await gate.RunAsync(() =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        Assert.IsTrue(ran);
    }
}
