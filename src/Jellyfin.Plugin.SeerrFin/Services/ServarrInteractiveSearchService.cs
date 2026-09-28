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
            (JObject? movie, string? error) = await EnsureRadarrMovieAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (movie == null)
            {
                return (404, ErrorJson(error ?? "Movie not found in Radarr."));
            }

            int movieId = movie.Value<int>("id");
            JArray queue = await GetQueueRecordsAsync(client, "movieIds", movieId, cancellationToken).ConfigureAwait(false);
            JArray? releases = await GetJsonArrayAsync(client, $"release?movieId={movieId}", cancellationToken).ConfigureAwait(false);

            JObject result = new()
            {
                ["queue"] = BuildQueueSummary(queue),
                ["releases"] = releases ?? new JArray()
            };
            return (200, result.ToString(Formatting.None));
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
            (JObject? series, string? error) = await EnsureSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson(error ?? "Series not found in Sonarr."));
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
            (JObject? series, string? error) = await EnsureSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson(error ?? "Series not found in Sonarr."));
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
            (JObject? series, string? error) = await EnsureSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
            if (series == null)
            {
                return (404, ErrorJson(error ?? "Series not found in Sonarr."));
            }

            int seriesId = series.Value<int>("id");
            JArray queue = await GetQueueRecordsAsync(client, "seriesIds", seriesId, cancellationToken).ConfigureAwait(false);
            JArray seasonQueue = new(queue.OfType<JObject>().Where(q => q.Value<int?>("seasonNumber") == seasonNumber));
            JArray? releases = await GetJsonArrayAsync(client, $"release?seriesId={seriesId}&seasonNumber={seasonNumber}", cancellationToken).ConfigureAwait(false);

            JObject result = new()
            {
                ["queue"] = BuildQueueSummary(seasonQueue),
                ["releases"] = releases ?? new JArray()
            };
            return (200, result.ToString(Formatting.None));
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

            JArray queue = new();
            JObject? episode = await GetJsonObjectAsync(client, $"episode/{episodeId}", cancellationToken).ConfigureAwait(false);
            int? seriesId = episode?.Value<int?>("seriesId");
            if (seriesId.HasValue)
            {
                JArray seriesQueue = await GetQueueRecordsAsync(client, "seriesIds", seriesId.Value, cancellationToken).ConfigureAwait(false);
                queue = new JArray(seriesQueue.OfType<JObject>().Where(q => q.Value<int?>("episodeId") == episodeId));
            }

            JArray? releases = await GetJsonArrayAsync(client, $"release?episodeId={episodeId}", cancellationToken).ConfigureAwait(false);

            JObject result = new()
            {
                ["queue"] = BuildQueueSummary(queue),
                ["releases"] = releases ?? new JArray()
            };
            return (200, result.ToString(Formatting.None));
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

    /// <summary>
    /// Interactive search needs the title to already exist in Radarr before releases can be
    /// searched. Rather than making the admin go request it first, silently add it (unmonitored
    /// search-wise - we're about to search manually) using the same lookup Radarr's own "Add
    /// Movie" screen uses, and the first configured quality profile/root folder as defaults.
    /// </summary>
    private async Task<(JObject? Movie, string? Error)> EnsureRadarrMovieAsync(HttpClient client, int tmdbId, CancellationToken cancellationToken)
    {
        JObject? existing = await FindRadarrMovieAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return (existing, null);
        }

        JObject? lookup = await GetJsonObjectAsync(client, $"movie/lookup/tmdb?tmdbId={tmdbId}", cancellationToken).ConfigureAwait(false);
        if (lookup == null)
        {
            return (null, "Couldn't find this movie in Radarr's metadata source.");
        }

        int? profileId = await GetFirstQualityProfileIdAsync(client, cancellationToken).ConfigureAwait(false);
        string? rootFolder = await GetFirstRootFolderAsync(client, cancellationToken).ConfigureAwait(false);
        if (profileId == null || string.IsNullOrWhiteSpace(rootFolder))
        {
            return (null, "Radarr has no quality profile or root folder configured - add one in Radarr first.");
        }

        lookup["qualityProfileId"] = profileId.Value;
        lookup["rootFolderPath"] = rootFolder;
        lookup["monitored"] = true;
        lookup["addOptions"] = new JObject { ["searchForMovie"] = false };

        using StringContent content = new(lookup.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("movie", content, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SeerrFin • failed to add movie {TmdbId} to Radarr: {Status} {Body}", tmdbId, response.StatusCode, responseBody);
            return (null, "Failed to add this movie to Radarr.");
        }

        return (JObject.Parse(responseBody), null);
    }

    /// <summary>
    /// Same idea as <see cref="EnsureRadarrMovieAsync"/>, but for Sonarr series. Sonarr identifies
    /// new shows by TVDB id normally, but its search proxy also accepts a "tmdb:{id}" lookup term.
    /// </summary>
    private async Task<(JObject? Series, string? Error)> EnsureSonarrSeriesAsync(HttpClient client, int tmdbId, CancellationToken cancellationToken)
    {
        JObject? existing = await FindSonarrSeriesAsync(client, tmdbId, cancellationToken).ConfigureAwait(false);
        if (existing != null)
        {
            return (existing, null);
        }

        JArray? lookupResults = await GetJsonArrayAsync(client, $"series/lookup?term=tmdb:{tmdbId}", cancellationToken).ConfigureAwait(false);
        JObject? lookup = lookupResults?.OfType<JObject>().FirstOrDefault();
        if (lookup == null)
        {
            return (null, "Couldn't find this series in Sonarr's metadata source.");
        }

        int? profileId = await GetFirstQualityProfileIdAsync(client, cancellationToken).ConfigureAwait(false);
        string? rootFolder = await GetFirstRootFolderAsync(client, cancellationToken).ConfigureAwait(false);
        if (profileId == null || string.IsNullOrWhiteSpace(rootFolder))
        {
            return (null, "Sonarr has no quality profile or root folder configured - add one in Sonarr first.");
        }

        lookup["qualityProfileId"] = profileId.Value;
        lookup["rootFolderPath"] = rootFolder;
        lookup["monitored"] = true;
        lookup["addOptions"] = new JObject
        {
            ["searchForMissingEpisodes"] = false,
            ["searchForCutoffUnmetEpisodes"] = false
        };

        using StringContent content = new(lookup.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync("series", content, cancellationToken).ConfigureAwait(false);
        string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("SeerrFin • failed to add series {TmdbId} to Sonarr: {Status} {Body}", tmdbId, response.StatusCode, responseBody);
            return (null, "Failed to add this series to Sonarr.");
        }

        return (JObject.Parse(responseBody), null);
    }

    private static async Task<int?> GetFirstQualityProfileIdAsync(HttpClient client, CancellationToken cancellationToken)
    {
        JArray? profiles = await GetJsonArrayAsync(client, "qualityprofile", cancellationToken).ConfigureAwait(false);
        return profiles?.OfType<JObject>().FirstOrDefault()?.Value<int?>("id");
    }

    private static async Task<string?> GetFirstRootFolderAsync(HttpClient client, CancellationToken cancellationToken)
    {
        JArray? folders = await GetJsonArrayAsync(client, "rootfolder", cancellationToken).ConfigureAwait(false);
        return folders?.OfType<JObject>().FirstOrDefault()?.Value<string>("path");
    }

    /// <summary>
    /// Radarr/Sonarr's queue endpoint is paged ({ records: [...] }, not a bare array) and only
    /// filters by movieId/seriesId - a large pageSize keeps this to one request per check.
    /// </summary>
    private static async Task<JArray> GetQueueRecordsAsync(HttpClient client, string idParamName, int id, CancellationToken cancellationToken)
    {
        JObject? paged = await GetJsonObjectAsync(
            client,
            $"queue?{idParamName}={id}&pageSize=250&includeUnknownSeriesItems=false&includeUnknownMovieItems=false",
            cancellationToken).ConfigureAwait(false);
        return paged?.Value<JArray>("records") ?? new JArray();
    }

    /// <summary>
    /// Trims a raw queue record list down to just what the UI needs to warn "this is already
    /// downloading" before letting an admin grab a second, competing release for the same title.
    /// </summary>
    private static JArray BuildQueueSummary(JArray queueRecords)
    {
        JArray summary = new();
        foreach (JObject record in queueRecords.OfType<JObject>())
        {
            decimal size = record.Value<decimal?>("size") ?? 0;
            decimal sizeLeft = record.Value<decimal?>("sizeleft") ?? 0;
            int percent = size > 0 ? (int)Math.Round((1 - (sizeLeft / size)) * 100) : 0;

            summary.Add(new JObject
            {
                ["title"] = record.Value<string>("title"),
                ["status"] = record.Value<string>("status"),
                ["percent"] = percent,
                ["downloadClient"] = record.Value<string>("downloadClient")
            });
        }

        return summary;
    }

    private static async Task<JObject?> GetJsonObjectAsync(HttpClient client, string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(path.TrimStart('/'), cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        string raw = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return JObject.Parse(raw);
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
