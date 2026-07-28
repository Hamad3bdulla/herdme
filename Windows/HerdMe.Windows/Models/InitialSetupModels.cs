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

}
