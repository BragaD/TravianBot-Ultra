namespace TbotUltra.Worker.Domain;

public enum AcademyResearchOutcome
{
    /// <summary>A research was started in this run.</summary>
    Started,

    /// <summary>Nothing left to research among the selected troops.</summary>
    AllResearched,

    /// <summary>The Academy is busy or resources are short; try again later.</summary>
    Waiting,

    /// <summary>The remaining troops need other buildings first.</summary>
    Blocked,

    NoAcademy,
}

/// <param name="RetrySeconds">When known, how long until the next research can start.</param>
/// <param name="Remaining">Selected troops that are not researched yet.</param>
public sealed record AcademyResearchResult(
    AcademyResearchOutcome Outcome,
    string Message,
    int? RetrySeconds,
    IReadOnlyList<string> Remaining,
    string? StartedTroop = null);

public enum TrapBuildOutcome
{
    Queued,
    NothingToBuild,
    NoTrapper,
    Failed,
}

public sealed record TrapBuildResult(TrapBuildOutcome Outcome, string Message, int Queued, int? RetrySeconds);
