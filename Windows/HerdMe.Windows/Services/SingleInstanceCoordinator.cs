using System.Runtime.Versioning;

namespace HerdMe.Windows.Services;

[SupportedOSPlatform("windows")]
public sealed class SingleInstanceCoordinator : IDisposable
{
    public const string MutexName = @"Local\HerdMe.Desktop.SingleInstance";
    public const string ActivationEventName = @"Local\HerdMe.Desktop.Activate";

    private readonly Mutex mutex;
    private readonly EventWaitHandle? activationEvent;
    private bool ownsMutex;

    public SingleInstanceCoordinator()
    {
        mutex = new Mutex(
            initiallyOwned: true,
            name: MutexName,
            createdNew: out var createdNew
        );
        IsPrimary = createdNew;
        ownsMutex = createdNew;
        if (!createdNew)
        {
            SignalExistingInstance();
            return;
        }

        activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ActivationEventName
        );
    }

    public bool IsPrimary { get; }

    public bool WaitForActivation()
    {
        if (activationEvent is null) return false;
        activationEvent.WaitOne();
        return true;
    }

    public void WakeListener() => activationEvent?.Set();

    public void Dispose()
    {
        activationEvent?.Dispose();
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
        GC.SuppressFinalize(this);
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using var existingEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                existingEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) when (attempt < 19)
            {
                Thread.Sleep(25);
            }
        }
    }
}
