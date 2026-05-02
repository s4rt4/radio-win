using System.Collections.Generic;

namespace ClassicRadio;

public class Station
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Country { get; set; }
}

public class StationData
{
    public List<Station> Indonesia { get; set; } = new();
    public List<Station> International { get; set; } = new();
}

public class AppState
{
    public string? LastSource { get; set; }   // "Indonesia" | "International"
    public string? LastStation { get; set; }  // station name (resilient to list reorder)
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
}
