namespace Cazacabezas.Server;

internal sealed record HuntSettings
{
    public bool Enabled { get; init; }
    public int HuntWindowMinutes { get; init; } = 15;
    public int RestMinutes { get; init; } = 30;
}
