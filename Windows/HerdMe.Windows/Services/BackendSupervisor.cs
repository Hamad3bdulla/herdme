namespace HerdMe.Windows.Services;

public sealed class BackendSupervisor : IAsyncDisposable
{
    private readonly LocalHttpSiteServer http;
    private readonly MailCaptureService mail;
    private readonly DumpCaptureService dumps;
    private readonly OperationJournal journal;
    private readonly SemaphoreSlim lifecycle = new(1, 1);

    public BackendSupervisor(
        LocalHttpSiteServer http,
        MailCaptureService mail,
        DumpCaptureService dumps,
        OperationJournal journal)
    {
        this.http = http;
        this.mail = mail;
        this.dumps = dumps;
        this.journal = journal;
    }

    public async Task StartCaptureServicesAsync(int mailPort, int dumpPort, CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken);
        try
        {
            var mailWasRunning = mail.IsRunning;
            var dumpsWereRunning = dumps.IsRunning;
            journal.Append("capture-services", "starting");
            try
            {
                await mail.StartAsync(mailPort, cancellationToken);
                await dumps.StartAsync(dumpPort, cancellationToken);
                journal.Append("capture-services", "running");
            }
            catch
            {
                // Starting the pair is transactional. Preserve services that
                // were already running, but roll back anything started here.
                try
                {
                    if (!dumpsWereRunning && dumps.IsRunning) await dumps.StopAsync();
                    if (!mailWasRunning && mail.IsRunning) await mail.StopAsync();
                }
                catch (Exception rollbackError)
                {
                    journal.Append(
                        "capture-services",
                        "rollback-failed",
                        rollbackError.Message
                    );
                }
                throw;
            }
        }
        catch (Exception error)
        {
            journal.Append("capture-services", "failed", error.Message);
            throw;
        }
        finally { lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync();
        try
        {
            journal.Append("capture-services", "stopping");
            await mail.StopAsync();
            await dumps.StopAsync();
            await http.StopAsync();
            journal.Append("capture-services", "stopped");
        }
        finally { lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await http.DisposeAsync();
        await mail.DisposeAsync();
        await dumps.DisposeAsync();
        lifecycle.Dispose();
    }
}
