using PageToMovie.Engine;

namespace PageToMovie.Api.Hosting;

/// <summary>
/// In-memory jobs die with the process. Fail them on recycle so a still-connected
/// client can see the error instead of hanging on the first queued line.
/// </summary>
public sealed class JobShutdownService : IHostedService
{
    private readonly FilmJobService _jobs;
    private readonly IHostApplicationLifetime _lifetime;
    private CancellationTokenRegistration _stopping;

    public JobShutdownService(FilmJobService jobs, IHostApplicationLifetime lifetime)
    {
        _jobs = jobs;
        _lifetime = lifetime;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = _lifetime.ApplicationStopping.Register(_jobs.FailInFlightJobsOnShutdown);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Dispose();
        return Task.CompletedTask;
    }
}
