namespace HerdMe.Windows.Services;

public static class UnhandledExceptionPolicy
{
    public static bool CanRecover(Exception error) => error is not (
        OutOfMemoryException or AccessViolationException
    );

    public static string UserMessage(Exception error)
    {
        return string.IsNullOrWhiteSpace(error.Message)
            ? "An unexpected operation failed. Details were written to HerdMe's logs."
            : error.Message;
    }
}
