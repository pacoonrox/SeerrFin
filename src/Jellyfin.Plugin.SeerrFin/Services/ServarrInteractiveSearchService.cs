using System.Text;
using Jellyfin.Plugin.SeerrFin.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SeerrFin.Services;

/// <summary>
/// Talks directly to Radarr/Sonarr (using the same credentials as <see cref="ServarrProgressService"/>)
/// to let admins run an interactive/manual release search and grab a specific release,
/// mirroring the "Interactive Search" feature in Radarr/Sonarr's own UI.
/// </summary>
public sealed class ServarrInteractiveSearchService
{
    private readonly ILogger<ServarrInteractiveSearchService> _logger;

    public ServarrInteractiveSearchService(ILogger<ServarrInteractiveSearchService> logger)
    {
        _logger = logger;
    }

    public async Task<(int StatusCode, string Body)> GetMovieReleasesAsync(int tmdbId, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsRadarrConfigured(config))
        {
            return (400, ErrorJson("Radarr is not configured in SeerrFin."));
        }

        try
        {
            using HttpClient client = CreateClient(config.RadarrUrl!, config.RadarrApiKey!);
            JObject? movie = await FindRadarrMovieAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (movie == null)
            {
                return (404, ErrorJson("This movie hasn't been added to Radarr yet. Request it first."));
            }

            int movieId = movie.Value<int>("id");
            JArray? releases = await GetJsonArrayAsync(client, $"release?movieId={movieId}", cancellationToken).ConfigureAwait(false);
            return (200, (releases ?? new JArray()).ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to load Radarr releases for tmdbId {TmdbId}", tmdbId);
            return (502, ErrorJson("Failed to reach Radarr."));
        }
    }

    public async Task<(int StatusCode, string Body)> GetSeriesInfoAsync(int tmdbId, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsSonarrConfigured(config))
        {
            return (400, ErrorJson("Sonarr is not configured in SeerrFin."));
        }

        try
        {
            using HttpClient client = CreateClient(config.SonarrUrl!, config.SonarrApiKey!);
            JObject? series = await FindSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson("This series hasn't been added to Sonarr yet. Request it first."));
            }

            JArray seasons = new();
            foreach (JObject season in series.Value<JArray>("seasons")?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                int? seasonNumber = season.Value<int?>("seasonNumber");
                if (seasonNumber == null)
                {
                    continue;
                }

                JObject? stats = season.Value<JObject>("statistics");
                seasons.Add(new JObject
                {
                    ["seasonNumber"] = seasonNumber,
                    ["monitored"] = season.Value<bool?>("monitored") ?? false,
                    ["episodeCount"] = stats?.Value<int?>("totalEpisodeCount") ?? 0,
                    ["episodeFileCount"] = stats?.Value<int?>("episodeFileCount") ?? 0
                });
            }

            JObject result = new()
            {
                ["seriesId"] = series.Value<int?>("id"),
                ["title"] = series.Value<string>("title"),
                ["seasons"] = seasons
            };

            return (200, result.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to load Sonarr series info for tmdbId {TmdbId}", tmdbId);
            return (502, ErrorJson("Failed to reach Sonarr."));
        }
    }

    public async Task<(int StatusCode, string Body)> GetSeasonEpisodesAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsSonarrConfigured(config))
        {
            return (400, ErrorJson("Sonarr is not configured in SeerrFin."));
        }

        try
        {
            using HttpClient client = CreateClient(config.SonarrUrl!, config.SonarrApiKey!);
            JObject? series = await FindSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson("This series hasn't been added to Sonarr yet. Request it first."));
            }

            int seriesId = series.Value<int>("id");
            JArray? episodes = await GetJsonArrayAsync(client, $"episode?seriesId={seriesId}&seasonNumber={seasonNumber}", cancellationToken).ConfigureAwait(false);
            return (200, (episodes ?? new JArray()).ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to load Sonarr episodes for tmdbId {TmdbId} season {Season}", tmdbId, seasonNumber);
            return (502, ErrorJson("Failed to reach Sonarr."));
        }
    }

    public async Task<(int StatusCode, string Body)> GetSeasonReleasesAsync(int tmdbId, int seasonNumber, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsSonarrConfigured(config))
        {
            return (400, ErrorJson("Sonarr is not configured in SeerrFin."));
        }

        try
        {
            using HttpClient client = CreateClient(config.SonarrUrl!, config.SonarrApiKey!);
            JObject? series = await FindSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson("This series hasn't been added to Sonarr yet. Request it first."));
            }

            int seriesId = series.Value<int>("id");
            JArray? releases = await GetJsonArrayAsync(client, $"release?seriesId={seriesId}&seasonNumber={seasonNumber}", cancellationToken).ConfigureAwait(false);
            return (200, (releases ?? new JArray()).ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to load Sonarr season releases for tmdbId {TmdbId} season {Season}", tmdbId, seasonNumber);
            return (502, ErrorJson("Failed to reach Sonarr."));
        }
    }

    public async Task<(int StatusCode, string Body)> GetEpisodeReleasesAsync(int episodeId, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsSonarrConfigured(config))
        {
            return (400, ErrorJson("Sonarr is not configured in SeerrFin."));
        }

        try
        {
            using HttpClient client = CreateClient(config.SonarrUrl!, config.SonarrApiKey!);
            JArray? releases = await GetJsonArrayAsync(client, $"release?episodeId={episodeId}", cancellationToken).ConfigureAwait(false);
            return (200, (releases ?? new JArray()).ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to load Sonarr episode releases for episodeId {EpisodeId}", episodeId);
            return (502, ErrorJson("Failed to reach Sonarr."));
        }
    }

    public async Task<(int StatusCode, string Body)> GrabMovieReleaseAsync(string releaseJson, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsRadarrConfigured(config))
        {
            return (400, ErrorJson("Radarr is not configured in SeerrFin."));
        }

        using HttpClient client = CreateClient(config.RadarrUrl!, config.RadarrApiKey!);
        return await PostReleaseAsync(client, releaseJson, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int StatusCode, string Body)> GrabSeriesReleaseAsync(string releaseJson, CancellationToken cancellationToken)
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (!IsSonarrConfigured(config))
        {
            return (400, ErrorJson("Sonarr is not configured in SeerrFin."));
        }

        using HttpClient client = CreateClient(config.SonarrUrl!, config.SonarrApiKey!);
        return await PostReleaseAsync(client, releaseJson, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int StatusCode, string Body)> PostReleaseAsync(HttpClient client, string releaseJson, CancellationToken cancellationToken)
    {
        try
        {
            using StringContent content = new(releaseJson, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client.PostAsync("release", content, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ((int)response.StatusCode, string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to grab release");
            return (502, ErrorJson("Failed to reach the download client. The release list may have expired — search again."));
        }
    }

    private static async Task<JObject?> FindRadarrMovieAsync(HttpClient client, int tmdbId, CancellationToken cancellationToken)
    {
        JArray? movies = await GetJsonArrayAsync(client, $"movie?tmdbId={tmdbId}", cancellationToken).ConfigureAwait(false);
        return movies?.OfType<JObject>().FirstOrDefault();
    }

    private static async Task<JObject?> FindSonarrSeriesAsync(HttpClient client, int tmdbId, CancellationToken cancellationToken)
    {
        // Sonarr only supports filtering the series list by tvdbId, not tmdbId, so fetch the
        // full list and match locally (the same approach ServarrProgressService already uses).
        JArray? seriesList = await GetJsonArrayAsync(client, "series", cancellationToken).ConfigureAwait(false);
        return seriesList?.OfType<JObject>().FirstOrDefault(s => s.Value<int?>("tmdbId") == tmdbId);
    }

    private static bool IsRadarrConfigured(PluginConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.RadarrUrl) && !string.IsNullOrWhiteSpace(config.RadarrApiKey);

    private static bool IsSonarrConfigured(PluginConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.SonarrUrl) && !string.IsNullOrWhiteSpace(config.SonarrApiKey);

    private static string ErrorJson(string message) =>
        new JObject { ["error"] = true, ["message"] = message }.ToString(Formatting.None);

    private static async Task<JArray?> GetJsonArrayAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(path.TrimStart('/'), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JToken.Parse(raw) as JArray;
    }

    private static HttpClient CreateClient(string baseUrl, string apiKey)
    {
        string normalized = baseUrl.Trim().TrimEnd('/');
        HttpClient client = new() { BaseAddress = new Uri(normalized + "/api/v3/") };
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }
}
