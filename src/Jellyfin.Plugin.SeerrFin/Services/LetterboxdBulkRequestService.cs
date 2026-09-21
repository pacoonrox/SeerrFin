using System.Collections.Concurrent;
using Jellyfin.Plugin.SeerrFin;
using Jellyfin.Plugin.SeerrFin.Configuration;
using Jellyfin.Plugin.SeerrFin.Configuration.Advanced;
using Jellyfin.Plugin.SeerrFin.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SeerrFin.Services;

public class LetterboxdBulkRequestService
{
    private readonly JellyseerrRequestService _requestService;
    private readonly JustWatchQualitiesService _qualitiesService;
    private readonly ILogger<LetterboxdBulkRequestService> _logger;
    private readonly ConcurrentDictionary<Guid, LetterboxdRequestProgressDto> _requestProgress = new();

    public LetterboxdBulkRequestService(
        JellyseerrRequestService requestService,
        JustWatchQualitiesService qualitiesService,
        ILogger<LetterboxdBulkRequestService> logger)
    {
        _requestService = requestService;
        _qualitiesService = qualitiesService;
        _logger = logger;
    }

    public LetterboxdRequestProgressDto GetRequestProgress(Guid userId)
    {
        if (_requestProgress.TryGetValue(userId, out LetterboxdRequestProgressDto? progress))
        {
            return progress;
        }

        return new LetterboxdRequestProgressDto { Done = 0, Total = 0, Percent = 0, IsActive = false };
    }

    public async Task<LetterboxdBulkRequestResultDto> SubmitBulkRequestAsync(
        Guid userId,
        string username,
        LetterboxdBulkRequestPayload payload,
        CancellationToken cancellationToken = default)
    {
        LetterboxdBulkRequestResultDto result = new();
        if (payload.TmdbIds == null || payload.TmdbIds.Count == 0)
        {
            return result;
        }

        List<int> tmdbIds = payload.TmdbIds.Distinct().ToList();
        int total = tmdbIds.Count;

        JArray requestOptions = _requestService.GetRequestOptions(username, "movie");
        // Empty options means no Advanced Requests permission so use Seerr defaults.
        bool useSeerrDefaults = requestOptions.Count == 0;
        string qualityMode = string.Empty;
        if (!useSeerrDefaults)
        {
            qualityMode = NormalizeQualityMode(string.IsNullOrWhiteSpace(payload.QualityMode) ? AdvancedSettingsHelper.Resolve(SeerrFinPlugin.Instance.Configuration).Letterboxd.DefaultBulkQualityMode : payload.QualityMode);
            if (qualityMode == "singleprofile" &&
                (payload.ServerId == null || payload.ProfileId == null))
            {
                throw new ArgumentException("ServerId and ProfileId are required for single profile mode.");
            }
        }

        SetRequestProgress(userId, 0, total, tmdbIds[0], result.Results);

        try
        {
            foreach (int tmdbId in tmdbIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Publish the current movie before doing jw lookup and Seerr submit work.
                SetRequestProgress(userId, result.Results.Count, total, tmdbId, result.Results);

                try
                {
                    (int? serverId, int? profileId, string? rootFolder, bool is4k, string? profileName, string? qualityLabel, string? warning) =
                        useSeerrDefaults ? (null, null, null, payload.Is4k, null, null, null) : await ResolveRequestOptionAsync(qualityMode, tmdbId, payload, requestOptions, cancellationToken).ConfigureAwait(false);

                    if (!useSeerrDefaults && (serverId == null || profileId == null))
                    {
                        result.Results.Add(new LetterboxdBulkRequestItemResult
                        {
                            TmdbId = tmdbId,
                            Status = "failed",
                            ProfileName = profileName,
                            QualityLabel = qualityLabel,
                            Message = warning ?? "Could not resolve a quality profile."
                        });
                        result.Failed++;
                    }
                    else
                    {
                        DiscoverRequestPayload requestPayload = new()
                        {
                            MediaType = "movie",
                            MediaId = tmdbId,
                            ServerId = serverId,
                            ProfileId = profileId,
                            RootFolder = rootFolder,
                            Is4k = is4k
                        };

                        (int statusCode, string body, _) = await _requestService
                            .SubmitRequestAsync(username, requestPayload, cancellationToken)
                            .ConfigureAwait(false);

                        // Seerr returns 409 when the title is already requested.
                        if (IsAlreadyRequested(statusCode, body))
                        {
                            result.Results.Add(new LetterboxdBulkRequestItemResult
                            {
                                TmdbId = tmdbId,
                                Status = "skipped",
                                ProfileName = profileName,
                                QualityLabel = qualityLabel,
                                Message = warning ?? "Already requested."
                            });
                            result.Skipped++;
                        }
                        else if (statusCode >= 200 && statusCode < 300)
                        {
                            result.Results.Add(new LetterboxdBulkRequestItemResult
                            {
                                TmdbId = tmdbId,
                                Status = "requested",
                                ProfileName = profileName,
                                QualityLabel = qualityLabel,
                                Message = warning
                            });
                            result.Requested++;
                        }
                        else
                        {
                            result.Results.Add(new LetterboxdBulkRequestItemResult
                            {
                                TmdbId = tmdbId,
                                Status = "failed",
                                ProfileName = profileName,
                                QualityLabel = qualityLabel,
                                Message = ExtractErrorMessage(body) ?? $"Request failed with status {statusCode}."
                            });
                            result.Failed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SeerrFin • bulk request failed for TMDB movie {TmdbId}", tmdbId);
                    result.Results.Add(new LetterboxdBulkRequestItemResult
                    {
                        TmdbId = tmdbId,
                        Status = "failed",
                        Message = ex.Message
                    });
                    result.Failed++;
                }

                // Read completed list so the modal can update one row at a time.
                SetRequestProgress(userId, result.Results.Count, total, null, result.Results);
            }
        }
        finally
        {
            ClearRequestProgress(userId);
        }

        return result;
    }

    private void SetRequestProgress(Guid userId, int done, int total, int? currentTmdbId, IReadOnlyList<LetterboxdBulkRequestItemResult> completed)
    {
        _requestProgress[userId] = new LetterboxdRequestProgressDto
        {
            Done = done,
            Total = total,
            Percent = total <= 0 ? 0 : (int)Math.Round(done * 100.0 / total),
            CurrentTmdbId = currentTmdbId,
            IsActive = true,
            Completed = completed.ToList()
        };
    }

    private void ClearRequestProgress(Guid userId)
    {
        _requestProgress.TryRemove(userId, out _);
    }

    private async Task<(int? ServerId, int? ProfileId, string? RootFolder, bool Is4k, string? ProfileName, string? QualityLabel, string? Warning)> ResolveRequestOptionAsync(
        string qualityMode,
        int tmdbId,
        LetterboxdBulkRequestPayload payload,
        JArray requestOptions,
        CancellationToken cancellationToken)
    {
        // User picked one quality profile for every selected movie.
        if (qualityMode == "singleprofile")
        {
            string? profileName = GetProfileName(requestOptions, payload.ServerId, payload.ProfileId);
            return (payload.ServerId, payload.ProfileId, payload.RootFolder, payload.Is4k, profileName, null, null);
        }

        // Per movie: ask jw which tier fits, then match to the quality profile.
        JustWatchQualitiesDto? qualities = await _qualitiesService
            .GetQualitiesAsync("movie", tmdbId, cancellationToken)
            .ConfigureAwait(false);

        string? targetLabel = qualityMode switch
        {
            "highestavailable" => qualities?.HighestReleasedQuality,
            "mostcommon" => qualities?.MostCommonQuality,
            _ => null
        };

        AdvancedJustWatchSettings jwSettings = AdvancedSettingsHelper.Resolve(SeerrFinPlugin.Instance.Configuration).JustWatch;
        if (string.IsNullOrWhiteSpace(targetLabel))
        {
            if (!jwSettings.FallbackToDefaultProfile)
            {
                return (null, null, null, false, null, null, "Quality recommendation unavailable.");
            }

            JObject? fallback = GetDefaultProfileOption(requestOptions, prefer4k: false);
            return (
                fallback?.Value<int?>("serverId"),
                fallback?.Value<int?>("profileId"),
                fallback?.Value<string>("rootFolder"),
                fallback?.Value<bool?>("is4k") ?? false,
                fallback?.Value<string>("profileName"),
                null,
                "Quality recommendation unavailable; used default profile.");
        }

        bool prefer4k = string.Equals(targetLabel, "Ultra-HD", StringComparison.OrdinalIgnoreCase) && jwSettings.Prefer4kServerForUltraHd;
        JObject? matched = FindProfileOption(requestOptions, targetLabel, prefer4k);
        if (matched != null)
        {
            return (
                matched.Value<int?>("serverId"),
                matched.Value<int?>("profileId"),
                matched.Value<string>("rootFolder"),
                matched.Value<bool?>("is4k") ?? false,
                matched.Value<string>("profileName"),
                targetLabel,
                null);
        }

        // No profile name matched the jw tier so fall back to server default (or maybe Any? idk whats more fitting.)
        JObject? defaultOption = GetDefaultProfileOption(requestOptions, prefer4k);
        return (
            defaultOption?.Value<int?>("serverId"),
            defaultOption?.Value<int?>("profileId"),
            defaultOption?.Value<string>("rootFolder"),
            defaultOption?.Value<bool?>("is4k") ?? false,
            defaultOption?.Value<string>("profileName"),
            targetLabel,
            $"Could not match {targetLabel}; used default profile.");
    }

    private static string? GetProfileName(JArray options, int? serverId, int? profileId)
    {
        return options.OfType<JObject>().FirstOrDefault(option => option.Value<int?>("serverId") == serverId && option.Value<int?>("profileId") == profileId)
            ?.Value<string>("profileName");
    }

    private static string NormalizeQualityMode(string? mode)
    {
        string normalized = (mode ?? "singleProfile").Trim();
        return normalized.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static JObject? FindProfileOption(JArray options, string targetLabel, bool prefer4k)
    {
        IEnumerable<JObject> candidates = options.OfType<JObject>();
        if (prefer4k)
        {
            // Ultra-HD targets should land on a 4K Radarr server when one exists.
            IEnumerable<JObject> fourK = candidates.Where(o => o.Value<bool?>("is4k") == true);
            JObject? fourKMatch = MatchByLabel(fourK, targetLabel);
            if (fourKMatch != null)
            {
                return fourKMatch;
            }
        }
        else
        {
            candidates = candidates.Where(o => o.Value<bool?>("is4k") != true);
        }

        // Try non-4K (or any) profiles, then widen search if nothing matched.
        return MatchByLabel(candidates, targetLabel) ?? MatchByLabel(options.OfType<JObject>(), targetLabel);
    }

    private static JObject? MatchByLabel(IEnumerable<JObject> options, string targetLabel)
    {
        Dictionary<string, string[]> aliasesByLabel = AdvancedSettingsHelper.GetQualityLabelAliases(SeerrFinPlugin.Instance.Configuration);
        if (aliasesByLabel.TryGetValue(targetLabel, out string[]? aliases))
        {
            foreach (string alias in aliases)
            {
                JObject? exact = options.FirstOrDefault(option =>
                    ProfileNamesMatch(option.Value<string>("profileName"), alias));
                if (exact != null)
                {
                    return exact;
                }
            }
        }

        return options.FirstOrDefault(option => ProfileNamesMatch(option.Value<string>("profileName"), targetLabel));
    }

    private static bool ProfileNamesMatch(string? profileName, string target)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            return false;
        }

        string normalizedProfile = NormalizeProfileName(profileName);
        string normalizedTarget = NormalizeProfileName(target);
        return normalizedProfile.Contains(normalizedTarget, StringComparison.Ordinal)
               || normalizedTarget.Contains(normalizedProfile, StringComparison.Ordinal);
    }

    private static string NormalizeProfileName(string value) =>
        value.Replace("-", " ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();

    private static JObject? GetDefaultProfileOption(JArray options, bool prefer4k)
    {
        IEnumerable<JObject> candidates = options.OfType<JObject>();
        if (prefer4k)
        {
            JObject? fourKDefault = candidates.FirstOrDefault(o =>
                o.Value<bool?>("is4k") == true && o.Value<bool?>("isDefaultProfile") == true);
            if (fourKDefault != null)
            {
                return fourKDefault;
            }
        }

        return candidates.FirstOrDefault(o =>
                   o.Value<bool?>("is4k") != true && o.Value<bool?>("isDefaultProfile") == true)
               ?? candidates.FirstOrDefault(o => o.Value<bool?>("is4k") != true)
               ?? candidates.FirstOrDefault();
    }

    private static bool IsAlreadyRequested(int statusCode, string body)
    {
        if (statusCode == 409)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        return body.Contains("already been requested", StringComparison.OrdinalIgnoreCase)
               || body.Contains("already requested", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractErrorMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            JObject json = JObject.Parse(body);
            JArray? errors = json.Value<JArray>("errors");
            if (errors != null && errors.Count > 0)
            {
                return string.Join("; ", errors.Select(error => error.ToString()));
            }

            return json.Value<string>("message");
        }
        catch
        {
            return null;
        }
    }
}
