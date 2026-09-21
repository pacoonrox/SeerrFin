using Jellyfin.Plugin.SeerrFin.Configuration;
using Jellyfin.Plugin.SeerrFin.Configuration.Advanced;
using Jellyfin.Plugin.SeerrFin.Services;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrFin.Helpers;

public static class ImageCacheHelper
{
    public static string GetCachedImageUrl(
        ImageCacheService imageCacheService,
        string? sourceUrl,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(sourceUrl))
        {
            return string.Empty;
        }

        PluginConfiguration? config = SeerrFinPlugin.Instance?.Configuration;
        try
        {
            if (config != null && AdvancedSettingsHelper.Resolve(config).Tmdb.DirectBrowserImages)
            {
                return sourceUrl;
            }

            int cacheTimeout = config?.CacheTimeoutSeconds ?? 86400;

            // Used in discovery mapping which allows cached images to be used in discovery cards
            string? cacheKey = imageCacheService.GetOrCacheImage(sourceUrl, cacheTimeout)
                .GetAwaiter()
                .GetResult();

            if (!string.IsNullOrEmpty(cacheKey))
            {
                return $"/SeerrFin/CachedImage/{cacheKey}";
            }

            bool fallback = config == null || AdvancedSettingsHelper.Resolve(config).Tmdb.FallbackToOriginalImageUrl;
            if (fallback)
            {
                logger?.LogWarning("SeerrFin • failed to cache image from {SourceUrl}, using original URL", sourceUrl);
                return sourceUrl;
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "SeerrFin • error caching image from {SourceUrl}", sourceUrl);
            bool fallback = config == null || AdvancedSettingsHelper.Resolve(config).Tmdb.FallbackToOriginalImageUrl;
            return fallback ? sourceUrl : string.Empty;
        }
    }

    // Hands out a CachedImage URL without downloading anything. The actual fetch happens lazily,
    // the first time a client requests that URL, so building a discover row never blocks on image I/O.
    public static string GetLazyCachedImageUrl(ImageCacheService imageCacheService, string? sourceUrl)
    {
        if (string.IsNullOrEmpty(sourceUrl))
        {
            return string.Empty;
        }

        PluginConfiguration? config = SeerrFinPlugin.Instance?.Configuration;
        if (config != null && AdvancedSettingsHelper.Resolve(config).Tmdb.DirectBrowserImages)
        {
            return sourceUrl;
        }

        string cacheKey = imageCacheService.RegisterSourceUrl(sourceUrl);
        return $"/SeerrFin/CachedImage/{cacheKey}";
    }
}
