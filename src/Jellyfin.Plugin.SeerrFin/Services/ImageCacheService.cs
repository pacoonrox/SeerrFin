using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SeerrFin.Configuration;
using Jellyfin.Plugin.SeerrFin.Model;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SeerrFin.Services;

public class ImageCacheService
{
    private readonly ILogger<ImageCacheService> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, CachedImageDto> _imageCache = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _inflightDownloads = new();
    private readonly ConcurrentDictionary<string, string> _knownSourceUrls = new();

    public ImageCacheService(
        ILogger<ImageCacheService> logger,
        IApplicationPaths applicationPaths,
        HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
        _cacheDirectory = Path.Combine(applicationPaths.CachePath, "SeerrFin", "Images");
        Directory.CreateDirectory(_cacheDirectory);
        LoadCacheIndex();
    }

    public async Task<string?> GetOrCacheImage(string sourceUrl, int cacheTimeoutSeconds)
    {
        if (string.IsNullOrEmpty(sourceUrl))
        {
            return null;
        }

        string cacheKey = GenerateCacheKey(sourceUrl);

        if (IsValidCacheKey(cacheKey))
        {
            return cacheKey;
        }

        // Index hit but file missing or expired. Remove stale entry before re-downloading
        if (_imageCache.ContainsKey(cacheKey))
        {
            CleanupCacheEntry(cacheKey);
        }

        return await GetOrStartDownload(sourceUrl, cacheKey, cacheTimeoutSeconds).ConfigureAwait(false);
    }

    // Pure, no I/O: lets discovery mapping hand out a CachedImage URL before the image is ever downloaded.
    public string RegisterSourceUrl(string sourceUrl)
    {
        string cacheKey = GenerateCacheKey(sourceUrl);
        // Already downloaded (or in flight): _imageCache/_inflightDownloads already know the source url,
        // so skip the write - otherwise every re-registration of an already-cached image (i.e. most of them,
        // every time a row is reloaded) would pile up here and never get cleared.
        if (!_imageCache.ContainsKey(cacheKey))
        {
            _knownSourceUrls[cacheKey] = sourceUrl;
        }

        return cacheKey;
    }

    public async Task<CachedImageFile?> GetOrFetchCachedImageFileAsync(string cacheKey)
    {
        if (_imageCache.TryGetValue(cacheKey, out CachedImageDto? cachedInfo)
            && cachedInfo.ExpiresAt > DateTime.UtcNow
            && File.Exists(cachedInfo.FilePath))
        {
            return BuildCachedImageFile(cachedInfo);
        }

        string? sourceUrl = cachedInfo?.SourceUrl;
        if (string.IsNullOrEmpty(sourceUrl))
        {
            _knownSourceUrls.TryGetValue(cacheKey, out sourceUrl);
        }

        if (string.IsNullOrEmpty(sourceUrl))
        {
            return null;
        }

        int cacheTimeout = SeerrFinPlugin.Instance.Configuration.CacheTimeoutSeconds;
        string? refreshedKey = await GetOrCacheImage(sourceUrl, cacheTimeout).ConfigureAwait(false);
        if (refreshedKey == cacheKey && _imageCache.TryGetValue(cacheKey, out CachedImageDto? freshInfo))
        {
            return BuildCachedImageFile(freshInfo);
        }

        _imageCache.TryRemove(cacheKey, out _);
        return null;
    }

    private async Task<string?> GetOrStartDownload(string sourceUrl, string cacheKey, int cacheTimeoutSeconds)
    {
        Task<string?> downloadTask = _inflightDownloads.GetOrAdd(
            cacheKey,
            _ => DownloadAndCacheImage(sourceUrl, cacheKey, cacheTimeoutSeconds));

        try
        {
            return await downloadTask.ConfigureAwait(false);
        }
        finally
        {
            _inflightDownloads.TryRemove(cacheKey, out _);
        }
    }

    private bool IsValidCacheKey(string cacheKey)
    {
        return _imageCache.TryGetValue(cacheKey, out CachedImageDto? cachedInfo)
               && cachedInfo.ExpiresAt > DateTime.UtcNow
               && File.Exists(cachedInfo.FilePath);
    }

    private async Task<string?> DownloadAndCacheImage(string sourceUrl, string cacheKey, int cacheTimeoutSeconds)
    {
        try
        {
            if (IsValidCacheKey(cacheKey))
            {
                return cacheKey;
            }

            PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
            if (_imageCache.Count >= config.MaxImageCacheEntries)
            {
                EvictOldEntries();
            }

            using HttpResponseMessage response = await _httpClient.GetAsync(sourceUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            byte[] imageData = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            string extension = GetExtensionFromContentType(contentType);
            string filePath = Path.Combine(_cacheDirectory, $"{cacheKey}{extension}");
            await File.WriteAllBytesAsync(filePath, imageData).ConfigureAwait(false);

            DateTime cachedAt = DateTime.UtcNow;
            _imageCache[cacheKey] = new CachedImageDto
            {
                CacheKey = cacheKey,
                SourceUrl = sourceUrl,
                FilePath = filePath,
                ContentType = contentType,
                CachedAt = cachedAt,
                ExpiresAt = cachedAt.AddSeconds(cacheTimeoutSeconds)
            };
            _knownSourceUrls.TryRemove(cacheKey, out _);
            SaveCacheIndex();
            return cacheKey;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeerrFin • error caching image from {SourceUrl}", sourceUrl);
            return null;
        }
    }

    private CachedImageFile BuildCachedImageFile(CachedImageDto cachedInfo)
    {
        DateTime lastModified = cachedInfo.CachedAt;
        try
        {
            lastModified = File.GetLastWriteTimeUtc(cachedInfo.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SeerrFin • failed to read last write time for {FilePath}", cachedInfo.FilePath);
        }

        int maxAgeSeconds = Math.Max(0, (int)Math.Ceiling((cachedInfo.ExpiresAt - DateTime.UtcNow).TotalSeconds));
        long fileLength = 0;
        try
        {
            fileLength = new FileInfo(cachedInfo.FilePath).Length;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SeerrFin • failed to read file length for {FilePath}", cachedInfo.FilePath);
        }

        return new CachedImageFile
        {
            FilePath = cachedInfo.FilePath,
            ContentType = cachedInfo.ContentType,
            LastModified = lastModified,
            MaxAgeSeconds = maxAgeSeconds,
            ETag = BuildETag(cachedInfo.CacheKey, lastModified, fileLength)
        };
    }

    private static string BuildETag(string cacheKey, DateTime lastModified, long fileLength)
    {
        return $"\"{cacheKey}-{lastModified.Ticks:x}-{fileLength:x}\"";
    }

    private void CleanupCacheEntry(string cacheKey)
    {
        if (_imageCache.TryRemove(cacheKey, out CachedImageDto? cachedInfo) && File.Exists(cachedInfo.FilePath))
        {
            try
            {
                File.Delete(cachedInfo.FilePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SeerrFin • failed to delete cache file {FilePath}", cachedInfo.FilePath);
            }
        }
    }

    private void EvictOldEntries()
    {
        int maxEntries = SeerrFinPlugin.Instance.Configuration.MaxImageCacheEntries;
        foreach (string key in _imageCache.Values
                     .OrderBy(x => x.CachedAt)
                     .Take(Math.Max(1, maxEntries / 10))
                     .Select(x => x.CacheKey))
        {
            CleanupCacheEntry(key);
        }

        SaveCacheIndex();
    }

    private void LoadCacheIndex()
    {
        string indexPath = Path.Combine(_cacheDirectory, "cache-index.json");
        if (!File.Exists(indexPath))
        {
            return;
        }

        try
        {
            CachedImageDto[]? entries = JsonSerializer.Deserialize<CachedImageDto[]>(File.ReadAllText(indexPath));
            if (entries == null)
            {
                return;
            }

            foreach (CachedImageDto entry in entries)
            {
                if (entry.ExpiresAt > DateTime.UtcNow && File.Exists(entry.FilePath))
                {
                    _imageCache[entry.CacheKey] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeerrFin • error loading cache index");
        }
    }

    private void SaveCacheIndex()
    {
        try
        {
            string indexPath = Path.Combine(_cacheDirectory, "cache-index.json");
            // Write to a uniquely-named temp file then rename over the index so concurrent saves
            // (now common: images cache in parallel instead of one at a time) can't interleave writes
            // and corrupt the shared file. Rename is atomic, so the worst case is one save's snapshot
            // losing to another's, which just means a newly cached entry gets silently re-downloaded
            // once after a restart - never corruption.
            string tempPath = Path.Combine(_cacheDirectory, $"cache-index.{Guid.NewGuid():N}.tmp");
            string json = JsonSerializer.Serialize(_imageCache.Values.ToArray(), new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, indexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SeerrFin • error saving cache index");
        }
    }

    private static string GenerateCacheKey(string sourceUrl)
    {
        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static string GetExtensionFromContentType(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        _ => ".jpg"
    };
}
