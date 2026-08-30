namespace PageToMovie.LoadSim;

/// <summary>Load-sim run profile. CLI parses this by name.</summary>
public enum LoadSimScenario
{
    Browse,
    Play,
    Gen,
    Remux,
    Mixed,
    Soak,
    Stress
}
