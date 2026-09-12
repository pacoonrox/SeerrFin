namespace Jellyfin.Plugin.SeerrFin.Configuration;

public class SeerrFinTabConfig
{
    public string Id { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public string Title { get; set; } = string.Empty;

    public static List<SeerrFinTabConfig> CreateDefaults() =>
    [
        new() { Id = "requests", Title = "Requests" }
    ];
}
