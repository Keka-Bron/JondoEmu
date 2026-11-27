namespace Heroes.Server;

internal sealed record HeroesSettings
{
    public int MaxHeroes { get; init; } = 7;
    public bool ReloadMapOnSwitch { get; init; }
}
