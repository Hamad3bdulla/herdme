namespace HerdMe.Windows.Services;

public static class UserErrorPresentation
{
    public static string Describe(Exception error)
    {
        var key = error switch
        {
            OperationCanceledException => "ServicesCancelled",
            HttpRequestException => "ErrorNetwork",
            UnauthorizedAccessException => "ErrorAccess",
            FileNotFoundException => "ErrorMissingRuntime",
            InvalidDataException => "ErrorPackage",
            _ => "ErrorOperation"
        };
        return AppLocalization.Get(key) + Environment.NewLine + error.Message;
    }
}
