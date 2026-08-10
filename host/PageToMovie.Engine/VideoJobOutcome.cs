namespace PageToMovie.Engine;

/// <summary>
/// Outcome of an asynchronous video generation job (submit + poll).
/// </summary>
public enum VideoJobOutcome
{
    Ok,
    OkAfterRetry,
    ProviderFailed,
    Expired,
    TimedOut,
    PollFailed,
}
