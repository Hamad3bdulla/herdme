namespace HerdMe.Windows.Services;

public sealed record GettingStartedStepState(string Id, bool Done);

/// <summary>
/// The Dashboard "Getting started" checklist for a new installation: create a site, open it,
/// send a test mail, try a dump. A step counts as done when the user did it once (marked in
/// sites.json) or when HerdMe already sees the result (a site, a captured mail or dump), so
/// people who used HerdMe before never see steps they did long ago. Pure so the contract
/// tests can pin it.
/// </summary>
public static class GettingStarted
{
    public const string StepCreateSite = "create-site";
    public const string StepOpenSite = "open-site";
    public const string StepSendMail = "send-mail";
    public const string StepTryDump = "try-dump";

    public static readonly IReadOnlyList<string> Steps =
    [
        StepCreateSite, StepOpenSite, StepSendMail, StepTryDump
    ];

    public static bool IsKnownStep(string? step) =>
        step is not null && Steps.Contains(step, StringComparer.Ordinal);

    // Known ids only, each once, in checklist order.
    public static List<string> NormalizeSteps(IEnumerable<string>? steps)
    {
        var marked = new HashSet<string>(steps ?? [], StringComparer.Ordinal);
        return Steps.Where(marked.Contains).ToList();
    }

    public static IReadOnlyList<GettingStartedStepState> Evaluate(
        IEnumerable<string>? marked,
        int siteCount,
        int mailCount,
        int dumpCount
    )
    {
        var done = new HashSet<string>(NormalizeSteps(marked), StringComparer.Ordinal);
        return
        [
            new(StepCreateSite, done.Contains(StepCreateSite) || siteCount > 0),
            new(StepOpenSite, done.Contains(StepOpenSite)),
            new(StepSendMail, done.Contains(StepSendMail) || mailCount > 0),
            new(StepTryDump, done.Contains(StepTryDump) || dumpCount > 0)
        ];
    }

    public static int DoneCount(IReadOnlyList<GettingStartedStepState> steps) =>
        steps.Count(step => step.Done);

    // Hidden once every step is done or after the user closed it.
    public static bool ShouldShow(bool dismissed, IReadOnlyList<GettingStartedStepState> steps) =>
        !dismissed && steps.Any(step => !step.Done);

    // The first step still open gets the accent button.
    public static string? NextStep(IReadOnlyList<GettingStartedStepState> steps) =>
        steps.FirstOrDefault(step => !step.Done)?.Id;
}
