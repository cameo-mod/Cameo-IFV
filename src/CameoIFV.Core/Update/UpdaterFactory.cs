namespace CameoIFV.Core.Update;

public interface IUpdaterFactory
{
    IUpdater ForPlan(UpdatePlan plan);
}

/// <summary>
/// Chooses the cheapest viable updater for a plan: zsync when a control file is available,
/// otherwise the full download. Keeps the strategy decision in one place.
/// </summary>
public sealed class UpdaterFactory : IUpdaterFactory
{
    private readonly HttpClient _http;

    public UpdaterFactory(HttpClient http) => _http = http;

    public IUpdater ForPlan(UpdatePlan plan)
        // zsync only pays off (and is only meaningfully tested) when we have a seed to diff against.
        // First install — or any plan without a usable seed — takes the full-download path, even if
        // the release ships a .zsync. This avoids running zsyncnet seedless on a fresh machine.
        => plan.ZsyncUrl is not null
           && !string.IsNullOrEmpty(plan.SeedZipPath)
           && File.Exists(plan.SeedZipPath)
            ? new ZsyncUpdater(_http)
            : new FullDownloadUpdater(_http);
}
