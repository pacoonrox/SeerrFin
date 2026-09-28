using System.Reflection;
using System.Text;
using Jellyfin.Plugin.SeerrFin.Configuration;
using Jellyfin.Plugin.SeerrFin.Configuration.Advanced;
using Jellyfin.Plugin.SeerrFin.Model;
using Jellyfin.Plugin.SeerrFin.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SeerrFin.Controllers;

[ApiController]
[Route("[controller]")]
public class SeerrFinController : ControllerBase
{
    private readonly JellyseerrDiscoveryService _discoveryService;
    private readonly JellyseerrRequestService _requestService;
    private readonly JellyseerrRequestsService _requestsService;
    private readonly JellyseerrProxyService _proxyService;
    private readonly ServarrProgressService _servarrProgressService;
    private readonly ServarrInteractiveSearchService _interactiveSearchService;
    private readonly ImageCacheService _imageCacheService;
    private readonly TmdbBackdropService _tmdbBackdropService;
    private readonly JustWatchQualitiesService _justWatchQualitiesService;
    private readonly LetterboxdWatchlistService _letterboxdWatchlistService;
    private readonly LetterboxdBulkRequestService _letterboxdBulkRequestService;
    private readonly ILogger<SeerrFinController> _logger;

    public SeerrFinController(
        JellyseerrDiscoveryService discoveryService,
        JellyseerrRequestService requestService,
        JellyseerrRequestsService requestsService,
        JellyseerrProxyService proxyService,
        ServarrProgressService servarrProgressService,
        ServarrInteractiveSearchService interactiveSearchService,
        ImageCacheService imageCacheService,
        TmdbBackdropService tmdbBackdropService,
        JustWatchQualitiesService justWatchQualitiesService,
        LetterboxdWatchlistService letterboxdWatchlistService,
        LetterboxdBulkRequestService letterboxdBulkRequestService,
        ILogger<SeerrFinController> logger)
    {
        _discoveryService = discoveryService;
        _requestService = requestService;
        _requestsService = requestsService;
        _proxyService = proxyService;
        _servarrProgressService = servarrProgressService;
        _interactiveSearchService = interactiveSearchService;
        _imageCacheService = imageCacheService;
        _tmdbBackdropService = tmdbBackdropService;
        _justWatchQualitiesService = justWatchQualitiesService;
        _letterboxdWatchlistService = letterboxdWatchlistService;
        _letterboxdBulkRequestService = letterboxdBulkRequestService;
        _logger = logger;
    }

    private Guid GetUserId()
    {
        string? userIdString = User.Claims
            .FirstOrDefault(x => x.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        return string.IsNullOrEmpty(userIdString) ? Guid.Empty : Guid.Parse(userIdString);
    }

    private string? GetUsername(IUserManager userManager)
    {
        Guid userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return null;
        }

        return userManager.GetUserById(userId)?.Username;
    }

    private void SetCacheHeaders()
    {
        var config = SeerrFinPlugin.Instance.Configuration;
        // Developer mode bypasses browser cache entirely.
        // Production: "private, no-cache" so a per-user browser cache is still allowed (fast
        // 304s), but every load revalidates against the ETag below instead of trusting a blind
        // TTL - and "private" keeps a CDN/reverse proxy in front of Jellyfin (e.g. Cloudflare
        // Tunnel) from caching and serving a stale copy of these injected files to every visitor
        // until it happens to expire. Without this, new tabs/features only appear after a hard
        // refresh instead of a normal page load.
        if (config.DeveloperMode)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else
        {
            Response.Headers.CacheControl = "private, no-cache, must-revalidate";
        }

        // ETag is the assembly version with admin cache-bust counter so config saves invalidate old assets
        string version = SeerrFinPlugin.Instance.GetType().Assembly.GetName().Version?.ToString() ?? "1.0.0.0";
        Response.Headers.ETag = $"\"v{version}-c{config.CacheBustCounter}\"";
    }

    [HttpGet("seerrfin-tabs.js")]
    [Produces("application/javascript")]
    public ActionResult GetScript() => ServeEmbedded("Inject.seerrfin-tabs.js", "application/javascript");

    [HttpGet("seerrfin-tabs.css")]
    [Produces("text/css")]
    public ActionResult GetStylesheet() => ServeEmbedded("Inject.seerrfin-tabs.css", "text/css");

    [HttpGet("seerrfin-nativeui.js")]
    [Produces("application/javascript")]
    public ActionResult GetNativeUiScript() => ServeEmbedded("Inject.seerrfin-nativeui.js", "application/javascript");

    [HttpGet("seerrfin-modal.js")]
    [Produces("application/javascript")]
    public ActionResult GetModalScript() => ServeEmbedded("Inject.seerrfin-modal.js", "application/javascript");

    [HttpGet("seerrfin-modal.css")]
    [Produces("text/css")]
    public ActionResult GetModalStylesheet() => ServeEmbedded("Inject.seerrfin-modal.css", "text/css");

    [HttpGet("seerrfin-requests.js")]
    [Produces("application/javascript")]
    public ActionResult GetRequestsScript() => ServeEmbedded("Inject.seerrfin-requests.js", "application/javascript");

    [HttpGet("seerrfin-requests.css")]
    [Produces("text/css")]
    public ActionResult GetRequestsStylesheet() => ServeEmbedded("Inject.seerrfin-requests.css", "text/css");

    [HttpGet("seerrfin-letterboxd.js")]
    [Produces("application/javascript")]
    public ActionResult GetLetterboxdScript() => ServeEmbedded("Inject.seerrfin-letterboxd.js", "application/javascript");

    [HttpGet("seerrfin-letterboxd.css")]
    [Produces("text/css")]
    public ActionResult GetLetterboxdStylesheet() => ServeEmbedded("Inject.seerrfin-letterboxd.css", "text/css");

    [HttpGet("jellyseerr/{*path}")]
    [Authorize]
    public Task<IActionResult> JellyseerrProxyGet(
        string path,
        [FromServices] IUserManager userManager,
        CancellationToken cancellationToken) =>
        ProxyJellyseerr(userManager, HttpMethod.Get, path, null, cancellationToken);

    [HttpPost("jellyseerr/{*path}")]
    [Authorize]
    public async Task<IActionResult> JellyseerrProxyPost(
        string path,
        [FromServices] IUserManager userManager,
        CancellationToken cancellationToken)
    {
        using StreamReader reader = new(Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        return await ProxyJellyseerr(userManager, HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IActionResult> ProxyJellyseerr(
        IUserManager userManager,
        HttpMethod method,
        string path,
        string? body,
        CancellationToken cancellationToken)
    {
        string? username = GetUsername(userManager);
        if (string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        (int statusCode, string responseBody, string contentType) = await _proxyService
            .ProxyAsync(username, method, path, body, cancellationToken)
            .ConfigureAwait(false);

        return new ContentResult
        {
            StatusCode = statusCode,
            Content = responseBody,
            ContentType = contentType
        };
    }

    [HttpGet("interactive-search/movie/{tmdbId:int}/releases")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetMovieInteractiveReleases(int tmdbId, CancellationToken cancellationToken)
    {
        (int statusCode, string body) = await _interactiveSearchService
            .GetMovieReleasesAsync(tmdbId, cancellationToken)
            .ConfigureAwait(false);
        return new ContentResult { StatusCode = statusCode, Content = body, ContentType = "application/json" };
    }

    [HttpGet("interactive-search/series/{tmdbId:int}")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetSeriesInteractiveInfo(int tmdbId, CancellationToken cancellationToken)
    {
        (int statusCode, string body) = await _interactiveSearchService
            .GetSeriesInfoAsync(tmdbId, cancellationToken)
            .ConfigureAwait(false);
        return new ContentResult { StatusCode = statusCode, Content = body, ContentType = "application/json" };
    }

    [HttpGet("interactive-search/series/{tmdbId:int}/season/{seasonNumber:int}/episodes")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetSeasonInteractiveEpisodes(int tmdbId, int seasonNumber, CancellationToken cancellationToken)
    {
        (int statusCode, string body) = await _interactiveSearchService
            .GetSeasonEpisodesAsync(tmdbId, seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        return new ContentResult { StatusCode = statusCode, Content = body, ContentType = "application/json" };
    }

    [HttpGet("interactive-search/series/{tmdbId:int}/season/{seasonNumber:int}/releases")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetSeasonInteractiveReleases(int tmdbId, int seasonNumber, CancellationToken cancellationToken)
    {
        (int statusCode, string body) = await _interactiveSearchService
            .GetSeasonReleasesAsync(tmdbId, seasonNumber, cancellationToken)
            .ConfigureAwait(false);
        return new ContentResult { StatusCode = statusCode, Content = body, ContentType = "application/json" };
    }

    [HttpGet("interactive-search/episode/{episodeId:int}/releases")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GetEpisodeInteractiveReleases(int episodeId, CancellationToken cancellationToken)
    {
        (int statusCode, string body) = await _interactiveSearchService
            .GetEpisodeReleasesAsync(episodeId, cancellationToken)
            .ConfigureAwait(false);
        return new ContentResult { StatusCode = statusCode, Content = body, ContentType = "application/json" };
    }

    [HttpPost("interactive-search/movie/grab")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GrabMovieInteractiveRelease(
        [FromServices] IUserManager userManager,
        CancellationToken cancellationToken)
    {
        (int? tmdbId, _, string releaseJson) = await ReadInteractiveGrabRequestAsync(cancellationToken).ConfigureAwait(false);

        (int statusCode, string responseBody) = await _interactiveSearchService
            .GrabMovieReleaseAsync(releaseJson, cancellationToken)
            .ConfigureAwait(false);

        if (statusCode is >= 200 and < 300 && tmdbId.HasValue)
        {
            // Fire-and-forget: the admin's UI locks every other release button as soon as this
            // response comes back, so awaiting a whole extra round trip to Seerr here (resolving
            // its user, then POSTing a request) left that window open long enough for a second
            // grab to slip through before the lock applied. The actual download already
            // succeeded either way, so this response shouldn't wait on best-effort bookkeeping.
            string? username = GetUsername(userManager);
            if (!string.IsNullOrWhiteSpace(username))
            {
                _ = SyncInteractiveGrabWithSeerrAsync(username, "movie", tmdbId.Value, null);
            }
        }

        return new ContentResult { StatusCode = statusCode, Content = responseBody, ContentType = "application/json" };
    }

    [HttpPost("interactive-search/series/grab")]
    [Authorize(Roles = "Administrator")]
    public async Task<IActionResult> GrabSeriesInteractiveRelease(
        [FromServices] IUserManager userManager,
        CancellationToken cancellationToken)
    {
        (int? tmdbId, int? seasonNumber, string releaseJson) = await ReadInteractiveGrabRequestAsync(cancellationToken).ConfigureAwait(false);

        (int statusCode, string responseBody) = await _interactiveSearchService
            .GrabSeriesReleaseAsync(releaseJson, cancellationToken)
            .ConfigureAwait(false);

        if (statusCode is >= 200 and < 300 && tmdbId.HasValue)
        {
            string? username = GetUsername(userManager);
            if (!string.IsNullOrWhiteSpace(username))
            {
                _ = SyncInteractiveGrabWithSeerrAsync(username, "tv", tmdbId.Value, seasonNumber);
            }
        }

        return new ContentResult { StatusCode = statusCode, Content = responseBody, ContentType = "application/json" };
    }

    private async Task<(int? TmdbId, int? SeasonNumber, string ReleaseJson)> ReadInteractiveGrabRequestAsync(CancellationToken cancellationToken)
    {
        using StreamReader reader = new(Request.Body, Encoding.UTF8);
        string body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            JObject payload = JObject.Parse(body);
            int? tmdbId = payload.Value<int?>("tmdbId");
            int? seasonNumber = payload.Value<int?>("seasonNumber");
            string releaseJson = (payload.Value<JObject>("release") ?? new JObject()).ToString(Newtonsoft.Json.Formatting.None);
            return (tmdbId, seasonNumber, releaseJson);
        }
        catch (Newtonsoft.Json.JsonReaderException)
        {
            // Fall back to treating the whole body as the raw release object, in case a caller
            // hasn't wrapped it with { tmdbId, seasonNumber, release }.
            return (null, null, body);
        }
    }

    /// <summary>
    /// Grabbing a release directly through Radarr/Sonarr (as interactive search does) bypasses
    /// Seerr entirely, so it would never show up in Seerr's own Downloads tab. Best-effort submit
    /// a matching Seerr request afterward so it gets tracked the same as a normal request would.
    /// Deliberately not awaited by the caller (see the fire-and-forget comment at each call site)
    /// and takes a plain username rather than IUserManager, so it never touches anything tied to
    /// the HTTP request's lifetime - this keeps running after the response has already been sent.
    /// </summary>
    private async Task SyncInteractiveGrabWithSeerrAsync(
        string username,
        string mediaType,
        int tmdbId,
        int? seasonNumber)
    {
        try
        {
            DiscoverRequestPayload payload = new()
            {
                MediaType = mediaType,
                MediaId = tmdbId,
                Seasons = seasonNumber.HasValue ? new List<int> { seasonNumber.Value } : null
            };

            await _requestService.SubmitRequestAsync(username, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • failed to sync interactive-search grab with Seerr for tmdbId {TmdbId}", tmdbId);
        }
    }

    [HttpGet("Configuration")]
    [Authorize(Roles = "Administrator")]
    public ActionResult<PluginConfiguration> GetConfiguration() => SeerrFinPlugin.Instance.Configuration;

    [HttpGet("display-settings")]
    [Authorize]
    public ActionResult GetDisplaySettings()
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        List<SeerrFinTabConfig> tabs = SeerrFinTabConfigHelper.Normalize(config.Tabs);
        string? browseUrl = config.ExternalJellyseerrUrl?.Trim();
        if (string.IsNullOrEmpty(browseUrl))
        {
            browseUrl = config.JellyseerrUrl?.Trim();
        }
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Ok(new
        {
            jellyseerrBrowseUrl = browseUrl ?? string.Empty,
            config.StreamingServiceUseImages,
            config.StudioNetworkUseImages,
            config.GenreUseBackdrops,
            config.DiscoverUsePosters,
            config.ElegantFinFixes,
            config.QualityRecommendations,
            config.AddSeerrResultsInSearch,
            config.NativeCarousels,
            config.NativeGridPages,
            config.NativeSearchResults,
            displayCustomizations = DisplayCustomizationsHelper.Resolve(config),
            advanced = AdvancedSettingsHelper.BuildFrontendPayload(config),
            tabs = tabs.Select(tab => new { id = tab.Id, enabled = tab.Enabled, title = tab.Title }),
            tabBarOrder = SeerrFinTabConfigHelper.NormalizeBarOrder(config.TabBarOrder)
        });
    }

    [HttpGet("backdrop/{mediaType}/{tmdbId}")]
    [Authorize]
    public async Task<ActionResult> GetBackdrop(string mediaType, int tmdbId, [FromQuery] bool preferNeutral = false, CancellationToken cancellationToken = default)
    {
        if (tmdbId <= 0)
        {
            return NotFound();
        }

        if (!string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        TmdbBackdropService.CachedBackdropDto? backdrop = await _tmdbBackdropService
            .GetCachedBackdropAsync(mediaType, tmdbId, preferNeutral, cancellationToken)
            .ConfigureAwait(false);

        if (backdrop == null || string.IsNullOrEmpty(backdrop.BackdropUrl))
        {
            return NotFound();
        }

        return Ok(new
        {
            backdropUrl = backdrop.BackdropUrl,
            tmdbBackdropPath = backdrop.TmdbBackdropPath,
            hasEnglishBackdrop = backdrop.HasEnglishBackdrop
        });
    }

    [HttpGet("CachedImage/{cacheKey}")]
    public async Task<ActionResult> GetCachedImage([FromRoute] string cacheKey)
    {
        CachedImageFile? cachedFile = await _imageCacheService.GetOrFetchCachedImageFileAsync(cacheKey).ConfigureAwait(false);
        if (cachedFile == null || !System.IO.File.Exists(cachedFile.FilePath))
        {
            return NotFound();
        }

        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (config.DeveloperMode)
        {
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        }
        else
        {
            Response.Headers.CacheControl = $"public, max-age={cachedFile.MaxAgeSeconds}";
        }

        Response.Headers.ETag = cachedFile.ETag;
        Response.Headers.LastModified = cachedFile.LastModified.ToString("R");

        if (Request.Headers.TryGetValue("If-None-Match", out Microsoft.Extensions.Primitives.StringValues etagValues)
            && etagValues.Any(value => string.Equals(value, cachedFile.ETag, StringComparison.Ordinal)))
        {
            return StatusCode(304);
        }

        if (Request.Headers.TryGetValue("If-Modified-Since", out Microsoft.Extensions.Primitives.StringValues modifiedValues)
            && DateTime.TryParse(modifiedValues.FirstOrDefault(), out DateTime ifModifiedSince)
            && ifModifiedSince.ToUniversalTime() >= cachedFile.LastModified)
        {
            return StatusCode(304);
        }

        return PhysicalFile(cachedFile.FilePath, cachedFile.ContentType);
    }

    [HttpPost("backdrops")]
    [Authorize]
    public async Task<ActionResult<BackdropBatchResponseDto>> GetBackdrops(
        [FromBody] BackdropBatchRequestDto request,
        CancellationToken cancellationToken)
    {
        if (request.Items == null || request.Items.Count == 0)
        {
            return BadRequest();
        }

        List<BackdropBatchItemDto> items = await _tmdbBackdropService
            .GetCachedBackdropsAsync(request.Items, cancellationToken)
            .ConfigureAwait(false);

        return Ok(new BackdropBatchResponseDto { Items = items });
    }

    [HttpGet("discover/movies/trending")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesTrending(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/trending", "movie", startIndex, limit);

    [HttpGet("discover/movies/popular")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesPopular(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/movies?sortBy=popularity.desc", "movie", startIndex, limit);

    [HttpGet("discover/movies/top-rated")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesTopRated(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/movies?sortBy=vote_average.desc&voteCountGte=200", "movie", startIndex, limit);

    [HttpGet("discover/movies/upcoming")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesUpcoming(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/movies/upcoming", "movie", startIndex, limit);

    [HttpGet("discover/tv/trending")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvTrending(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/trending", "tv", startIndex, limit);

    [HttpGet("discover/tv/popular")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvPopular(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/tv?sortBy=popularity.desc", "tv", startIndex, limit);

    [HttpGet("discover/tv/top-rated")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvTopRated(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/tv?sortBy=vote_average.desc&voteCountGte=200", "tv", startIndex, limit);

    [HttpGet("discover/tv/upcoming")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvUpcoming(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, "/api/v1/discover/tv/upcoming", "tv", startIndex, limit);

    [HttpGet("discover/tv/anime")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvAnime(
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        _discoveryService.GetAnimeRow(GetUsername(userManager) ?? string.Empty, startIndex, limit);

    [HttpGet("discover/movies/genre/{genreId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesByGenre(
        int genreId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, $"/api/v1/discover/movies?genre={genreId}", "movie", startIndex, limit);

    [HttpGet("discover/tv/genre/{genreId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvByGenre(
        int genreId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, $"/api/v1/discover/tv?genre={genreId}", "tv", startIndex, limit);

    [HttpGet("discover/movies/studio/{studioId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesByStudio(
        int studioId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, $"/api/v1/discover/movies?studio={studioId}", "movie", startIndex, limit);

    [HttpGet("discover/tv/network/{networkId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvByNetwork(
        int networkId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null) =>
        DiscoverRow(userManager, $"/api/v1/discover/tv?network={networkId}", "tv", startIndex, limit);

    [HttpGet("discover/movies/provider/{providerId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> MoviesByProvider(
        int providerId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null)
    {
        string region = SeerrFinPlugin.Instance.Configuration.WatchRegion;
        if (string.IsNullOrWhiteSpace(region))
        {
            region = "US";
        }
        return DiscoverRow(userManager, $"/api/v1/discover/movies?watchProviders={providerId}&watchRegion={Uri.EscapeDataString(region)}", "movie", startIndex, limit);
    }

    [HttpGet("discover/tv/provider/{providerId}")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> TvByProvider(
        int providerId,
        [FromServices] IUserManager userManager,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null)
    {
        string region = SeerrFinPlugin.Instance.Configuration.WatchRegion;
        if (string.IsNullOrWhiteSpace(region))
        {
            region = "US";
        }
        return DiscoverRow(userManager, $"/api/v1/discover/tv?watchProviders={providerId}&watchRegion={Uri.EscapeDataString(region)}", "tv", startIndex, limit);
    }

    private ActionResult<QueryResult<BaseItemDto>> DiscoverRow(
        IUserManager userManager,
        string jellyseerrPath,
        string mediaType,
        int startIndex,
        int? limit) =>
        _discoveryService.GetDiscoverRow(GetUsername(userManager) ?? string.Empty, jellyseerrPath, mediaType, startIndex, limit);

    [HttpGet("search")]
    [Authorize]
    public ActionResult<QueryResult<BaseItemDto>> Search(
        [FromServices] IUserManager userManager,
        [FromQuery] string? query,
        [FromQuery] string? language = null,
        [FromQuery] int startIndex = 0,
        [FromQuery] int? limit = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new
            {
                error = true,
                code = "missing_query",
                message = "Search query is required."
            });
        }

        return _discoveryService.Search(GetUsername(userManager) ?? string.Empty, query, language, startIndex, limit);
    }

    [HttpGet("genres/movie")]
    [Authorize]
    public ActionResult MovieGenres([FromServices] IUserManager userManager)
    {
        JArray data = _discoveryService.GetGenreSlider("movie", GetUsername(userManager) ?? string.Empty);
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("genres/tv")]
    [Authorize]
    public ActionResult TvGenres([FromServices] IUserManager userManager)
    {
        JArray data = _discoveryService.GetGenreSlider("tv", GetUsername(userManager) ?? string.Empty);
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("providers/movie")]
    [Authorize]
    public ActionResult MovieProviders()
    {
        JArray data = _discoveryService.GetMovieStreamingServices();
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("providers/tv")]
    [Authorize]
    public ActionResult TvProviders()
    {
        JArray data = _discoveryService.GetTvStreamingServices();
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("studios/movie")]
    [Authorize]
    public ActionResult MovieStudios()
    {
        JArray data = _discoveryService.GetStudios();
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("networks/tv")]
    [Authorize]
    public ActionResult TvNetworks()
    {
        JArray data = _discoveryService.GetNetworks();
        return Content(data.ToString(), "application/json");
    }

    [HttpGet("client-settings")]
    [Authorize]
    public ActionResult GetClientSettings()
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        string? key = config.TmdbApiKey?.Trim();
        string? browseUrl = config.ExternalJellyseerrUrl?.Trim();
        if (string.IsNullOrEmpty(browseUrl))
        {
            browseUrl = config.JellyseerrUrl?.Trim();
        }

        return Ok(new
        {
            tmdbApiKey = key ?? string.Empty,
            jellyseerrBrowseUrl = browseUrl ?? string.Empty,
            radarrUrl = IsServarrBrowseUrlConfigured(config.RadarrUrl, config.RadarrApiKey),
            sonarrUrl = IsServarrBrowseUrlConfigured(config.SonarrUrl, config.SonarrApiKey)
        });
    }

    private static string IsServarrBrowseUrlConfigured(string? url, string? apiKey) =>
        !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(apiKey)
            ? url.Trim().TrimEnd('/')
            : string.Empty;

    [HttpGet("details/{mediaType}/{mediaId}")]
    [Authorize]
    public ActionResult GetDetails(
        string mediaType,
        int mediaId,
        [FromServices] IUserManager userManager)
    {
        JObject? details = _discoveryService.GetMediaDetails(GetUsername(userManager) ?? string.Empty, mediaType, mediaId);
        return details == null ? NotFound() : Content(details.ToString(), "application/json");
    }

    [HttpGet("justwatch/qualities/{mediaType}/{tmdbId}")]
    [Authorize]
    public async Task<ActionResult<JustWatchQualitiesDto>> GetJustWatchQualities(
        string mediaType,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        if (tmdbId <= 0)
        {
            return BadRequest();
        }

        if (!string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest();
        }

        JustWatchQualitiesDto? qualities = await _justWatchQualitiesService
            .GetQualitiesAsync(mediaType, tmdbId, cancellationToken)
            .ConfigureAwait(false);

        if (qualities == null)
        {
            return NotFound();
        }

        return Ok(new
        {
            highestReleasedQuality = qualities.HighestReleasedQuality,
            mostCommonQuality = qualities.MostCommonQuality
        });
    }

    [HttpGet("request-options/{mediaType}")]
    [Authorize]
    public ActionResult GetRequestOptions(string mediaType, [FromServices] IUserManager userManager)
    {
        string? username = GetUsername(userManager);
        if (string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        RequestOptionsResult result = _requestService.GetRequestOptionsResult(username, mediaType);
        // serialize so nested option keys stay camelcase
        JObject payload = new()
        {
            ["canRequest"] = result.CanRequest,
            ["canRequest4k"] = result.CanRequest4k,
            ["canRequestAdvanced"] = result.CanRequestAdvanced,
            ["options"] = result.Options
        };
        return Content(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json");
    }

    [HttpGet("requests")]
    [Authorize]
    public async Task<ActionResult> GetRequests(
        [FromServices] IUserManager userManager,
        [FromQuery] int take = 20,
        [FromQuery] int skip = 0,
        [FromQuery] string? filter = null,
        CancellationToken cancellationToken = default)
    {
        Guid userId = GetUserId();
        string? username = GetUsername(userManager);
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        (int statusCode, string body) = await _requestsService
            .GetRequestsAsync(userId, username, take, skip, filter, cancellationToken)
            .ConfigureAwait(false);

        return new ContentResult
        {
            StatusCode = statusCode,
            Content = body,
            ContentType = "application/json"
        };
    }

    [HttpGet("calendar")]
    [Authorize]
    public async Task<ActionResult> GetCalendar(
        [FromQuery] DateTime? start = null,
        [FromQuery] DateTime? end = null,
        CancellationToken cancellationToken = default)
    {
        DateTime today = DateTime.UtcNow.Date;
        DateTime rangeStart = (start ?? new DateTime(today.Year, today.Month, 1)).Date;
        DateTime rangeEnd = (end ?? rangeStart.AddMonths(1).AddDays(-1)).Date;

        if (rangeEnd < rangeStart)
        {
            return BadRequest();
        }

        if ((rangeEnd - rangeStart).TotalDays > 93)
        {
            rangeEnd = rangeStart.AddDays(93);
        }

        JObject payload = await _servarrProgressService
            .GetCalendarAsync(rangeStart, rangeEnd, cancellationToken)
            .ConfigureAwait(false);

        return Content(payload.ToString(Newtonsoft.Json.Formatting.None), "application/json");
    }

    [HttpGet("proxy/avatar")]
    [Authorize]
    public async Task<ActionResult> ProxyAvatar([FromQuery] string? path, CancellationToken cancellationToken)
    {
        (byte[]? data, string? contentType) = await _requestsService
            .GetAvatarAsync(path, cancellationToken)
            .ConfigureAwait(false);

        if (data == null || contentType == null)
        {
            return NotFound();
        }

        return File(data, contentType);
    }

    [HttpPost("request")]
    [Authorize]
    public async Task<ActionResult> MakeDiscoverRequest(
        [FromServices] IUserManager userManager,
        [FromBody] DiscoverRequestPayload payload,
        CancellationToken cancellationToken)
    {
        string? username = GetUsername(userManager);
        if (string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        (int statusCode, string body, string contentType) = await _requestService
            .SubmitRequestAsync(username, payload, cancellationToken)
            .ConfigureAwait(false);

        return new ContentResult
        {
            StatusCode = statusCode,
            Content = body,
            ContentType = contentType
        };
    }

    [HttpGet("letterboxd/sync/progress")]
    [Authorize]
    public ActionResult<LetterboxdSyncProgressDto> GetLetterboxdSyncProgress()
    {
        Guid userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Ok(_letterboxdWatchlistService.GetSyncProgress(userId));
    }

    [HttpPost("letterboxd/sync")]
    [Authorize]
    public async Task<ActionResult> SyncLetterboxdWatchlist(
        [FromQuery] string letterboxdUsername,
        CancellationToken cancellationToken)
    {
        Guid userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(letterboxdUsername))
        {
            return BadRequest(new { message = "Letterboxd username is needed." });
        }

        try
        {
            (List<BaseItemDto> items, int totalCount, int resolvedCount, int unresolvedCount) = await _letterboxdWatchlistService
                .SyncAsync(userId, letterboxdUsername, cancellationToken)
                .ConfigureAwait(false);
            Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
            return Ok(new
            {
                letterboxdUsername = letterboxdUsername.Trim(),
                totalCount,
                resolvedCount,
                unresolvedCount,
                items
            });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("letterboxd/request/progress")]
    [Authorize]
    public ActionResult<LetterboxdRequestProgressDto> GetLetterboxdRequestProgress()
    {
        Guid userId = GetUserId();
        if (userId == Guid.Empty)
        {
            return Forbid();
        }

        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Ok(_letterboxdBulkRequestService.GetRequestProgress(userId));
    }

    [HttpPost("letterboxd/request/check")]
    [Authorize]
    public ActionResult CheckLetterboxdRequestStatus(
        [FromServices] IUserManager userManager,
        [FromBody] LetterboxdBulkRequestPayload payload)
    {
        string? username = GetUsername(userManager);
        if (string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        if (payload.TmdbIds == null || payload.TmdbIds.Count == 0)
        {
            return BadRequest(new { message = "Select at least one movie." });
        }

        List<int> alreadyRequested = _discoveryService
            .GetAlreadyRequestedMovieIds(username, payload.TmdbIds);
        return Ok(new { tmdbIds = alreadyRequested });
    }

    [HttpPost("letterboxd/request")]
    [Authorize]
    public async Task<ActionResult<LetterboxdBulkRequestResultDto>> RequestLetterboxdItems(
        [FromServices] IUserManager userManager,
        [FromBody] LetterboxdBulkRequestPayload payload,
        CancellationToken cancellationToken)
    {
        Guid userId = GetUserId();
        string? username = GetUsername(userManager);
        if (userId == Guid.Empty || string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        if (payload.TmdbIds == null || payload.TmdbIds.Count == 0)
        {
            return BadRequest(new { message = "Select at least one movie." });
        }

        try
        {
            LetterboxdBulkRequestResultDto result = await _letterboxdBulkRequestService
                .SubmitBulkRequestAsync(userId, username, payload, cancellationToken)
                .ConfigureAwait(false);
            return Ok(result);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    private ActionResult ServeEmbedded(string resourceName, string contentType)
    {
        Stream? stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"{typeof(SeerrFinPlugin).Namespace}.{resourceName}");
        if (stream == null)
        {
            return NotFound();
        }

        SetCacheHeaders();
        return File(stream, contentType);
    }
}
