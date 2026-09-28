using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.SeerrFin.Model;

public class DiscoverRequestPayload
{
    [JsonPropertyName("MediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("MediaId")]
    public int MediaId { get; set; }

    [JsonPropertyName("ServerId")]
    public int? ServerId { get; set; }

    [JsonPropertyName("ProfileId")]
    public int? ProfileId { get; set; }

    [JsonPropertyName("RootFolder")]
    public string? RootFolder { get; set; }

    [JsonPropertyName("Is4k")]
    public bool Is4k { get; set; }

    [JsonPropertyName("Seasons")]
    public List<int>? Seasons { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Seerr should skip its own automatic search when
    /// fulfilling this request. Only meaningful against a Seerr fork that understands the flag
    /// (see server/entity/MediaRequest.ts's skipSearch column) - set this for requests created
    /// purely to track a release that was already grabbed directly through Radarr/Sonarr (e.g.
    /// SeerrFin's interactive search), so Seerr doesn't independently re-search and grab a
    /// second, competing release for the same title while the first is still downloading.
    /// </summary>
    [JsonPropertyName("SkipSearch")]
    public bool SkipSearch { get; set; }
}
