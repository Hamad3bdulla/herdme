namespace HerdMe.Windows.Models;

public enum InitialSetupStage
{
    Welcome,
    LocalDomains,
    Certificate,
    Php,
    Composer,
    Node,
    Finishing,
    Completed
}

public static class InitialSetupStages
{
    public static IReadOnlyList<InitialSetupStage> Installation { get; } =
    [
        InitialSetupStage.LocalDomains,
        InitialSetupStage.Certificate,
        InitialSetupStage.Php,
        InitialSetupStage.Composer,
        InitialSetupStage.Node,
        InitialSetupStage.Finishing
    ];

    public static string Title(InitialSetupStage stage) => stage switch
    {
        InitialSetupStage.Welcome => "Welcome to HerdMe",
        InitialSetupStage.LocalDomains => "Setting up local .test domains",
        InitialSetupStage.Certificate => "Trusting the local HTTPS certificate",
        InitialSetupStage.Php => "Installing PHP 8.4",
        InitialSetupStage.Composer => "Installing Composer and Laravel Installer",
        InitialSetupStage.Node => "Installing Node.js 22",
        InitialSetupStage.Finishing => "Finishing setup",
        InitialSetupStage.Completed => "HerdMe is ready",
        _ => "Preparing HerdMe"
    };

    public static string Detail(InitialSetupStage stage) => stage switch
    {
        InitialSetupStage.Welcome =>
            "HerdMe needs to prepare the local development environment. Windows will ask for administrator approval.",
        InitialSetupStage.LocalDomains =>
            "Preparing private routing for projects that use the .test domain.",
        InitialSetupStage.Certificate =>
            "Adding the HerdMe local certificate authority to the Windows trust store.",
        InitialSetupStage.Php =>
            "Installing the default runtime and checking every extension required by Laravel.",
        InitialSetupStage.Composer =>
            "Preparing the managed PHP tools used to create Laravel projects.",
        InitialSetupStage.Node =>
            "Preparing the default JavaScript runtime and npm.",
        InitialSetupStage.Finishing =>
            "Saving the verified setup and refreshing the local environment.",
        InitialSetupStage.Completed =>
            "The default local development environment has been installed successfully.",
        _ => string.Empty
    };
}
