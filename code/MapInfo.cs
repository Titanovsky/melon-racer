using Sandbox;

public sealed class MapInfo : Component
{
    [Property] public string Header { get; set; } = "map_lestoria";
    [Property] public int Laps { get; set; } = 10;
    [Property] public List<SpawnMelon> SpawnsMelons { get; set; } = new();
}
