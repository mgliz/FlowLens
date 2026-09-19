using FlowLens;

internal static class SingleInstanceTests
{
    public static void Run(Action<bool, string> check)
    {
        var mutexName = $@"Local\FlowLens.RegressionTests.SingleInstance.{Guid.NewGuid():N}";
        using var owner = SingleInstanceMutex.Acquire(mutexName);
        using var nonOwner = SingleInstanceMutex.Acquire(mutexName);

        check(owner.OwnsMutex, "The first single-instance lease must own a newly created mutex.");
        check(!nonOwner.OwnsMutex, "A second single-instance lease must not claim mutex ownership.");

        Exception? nonOwnerExitException = null;
        try
        {
            nonOwner.Dispose();
            nonOwner.Dispose();
        }
        catch (Exception exception)
        {
            nonOwnerExitException = exception;
        }

        check(
            nonOwnerExitException is null,
            "A non-owner must be able to exit repeatedly without releasing the mutex or throwing.");
        check(
            !CanAcquireFromAnotherThread(mutexName),
            "Disposing a non-owner must leave the original owner's mutex locked.");

        Exception? ownerExitException = null;
        try
        {
            owner.Dispose();
            owner.Dispose();
        }
        catch (Exception exception)
        {
            ownerExitException = exception;
        }

        check(
            ownerExitException is null,
            "The owner must be able to release its mutex exactly once during repeated exit cleanup.");
        check(!owner.OwnsMutex, "A disposed owner must no longer report mutex ownership.");
        check(
            CanCreateOwnedLeaseOnAnotherThread(mutexName),
            "After the owner exits, another instance must be able to create and own the mutex.");
    }

    private static bool CanAcquireFromAnotherThread(string mutexName)
    {
        var acquired = false;
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var mutex = Mutex.OpenExisting(mutexName);
                acquired = mutex.WaitOne(0);
                if (acquired)
                {
                    mutex.ReleaseMutex();
                }
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });

        thread.Start();
        thread.Join();

        if (threadException is not null)
        {
            throw new InvalidOperationException("The mutex probe thread failed.", threadException);
        }

        return acquired;
    }

    private static bool CanCreateOwnedLeaseOnAnotherThread(string mutexName)
    {
        var ownsMutex = false;
        Exception? threadException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var lease = SingleInstanceMutex.Acquire(mutexName);
                ownsMutex = lease.OwnsMutex;
            }
            catch (Exception exception)
            {
                threadException = exception;
            }
        });

        thread.Start();
        thread.Join();

        if (threadException is not null)
        {
            throw new InvalidOperationException("The mutex ownership thread failed.", threadException);
        }

        return ownsMutex;
    }
}
