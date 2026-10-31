using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;
using NzbDrone.Core.Music;

namespace NzbDrone.Core.Plugins.AlbumArt.Services;

internal sealed record DiscogsArtworkMatch(
    byte[] Data,
    long ReleaseId,
    int Score);

internal interface IDiscogsArtworkProvider
{
    DiscogsArtworkMatch? Find(
        AlbumArtworkLookupContext context,
        CoverArtOptions options);
}

internal sealed class NullDiscogsArtworkProvider : IDiscogsArtworkProvider
{
    public DiscogsArtworkMatch? Find(
        AlbumArtworkLookupContext context,
        CoverArtOptions options)
    {
        return null;
    }
}

internal sealed partial class DiscogsArtworkProvider : IDiscogsArtworkProvider
{
    private const int MaxImageBytes = 32 * 1024 * 1024;
    private const int MaxCandidateDetails = 5;
    private static readonly object ThrottleLock = new();
    private static DateTime _nextRequestUtc = DateTime.MinValue;

    private readonly HttpClient _client;
    private readonly Logger _logger;
    private readonly bool _throttle;

    public DiscogsArtworkProvider(Logger logger)
        : this(logger, new HttpClientHandler { AllowAutoRedirect = true }, true)
    {
    }

    internal DiscogsArtworkProvider(
        Logger logger,
        HttpMessageHandler handler,
        bool throttle = false)
    {
        _logger = logger;
        _throttle = throttle;
        _client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LidarrAlbumArtOptimizer", "1.1"));
    }

    public DiscogsArtworkMatch? Find(
        AlbumArtworkLookupContext context,
        CoverArtOptions options)
    {
        if (!options.UseDiscogs ||
            string.IsNullOrWhiteSpace(options.DiscogsToken))
        {
            return null;
        }

        var overrides = ParseOverrides(options.DiscogsReleaseOverrides);
        if (overrides.TryGetValue(context.Album.Id, out var overrideReleaseId))
        {
            using var overridden = GetRelease(overrideReleaseId, options.DiscogsToken);
            return overridden is null
                ? null
                : DownloadReleaseImage(overridden, overrideReleaseId, 100, options.DiscogsToken);
        }

        var artistName = GetArtistName(context);
        var albumTitle = context.Album.Title;
        var releaseYear = GetReleaseYear(context);
        var searchUri = BuildSearchUri(artistName, albumTitle, releaseYear);
        using var search = SendJson(searchUri, options.DiscogsToken, "search");
        if (search is null ||
            !search.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        DiscogsCandidate? best = null;
        foreach (var result in results.EnumerateArray()
                     .Where(value => value.TryGetProperty("id", out _))
                     .Take(MaxCandidateDetails))
        {
            var id = result.GetProperty("id").GetInt64();
            using var release = GetRelease(id, options.DiscogsToken);
            if (release is null)
            {
                continue;
            }

            var candidate = ScoreRelease(
                release.RootElement,
                id,
                artistName,
                albumTitle,
                releaseYear,
                GetKnownTrackTitles(context));
            if (candidate is not null && (best is null || candidate.Score > best.Score))
            {
                best?.Document.Dispose();
                best = candidate;
            }
            else
            {
                candidate?.Document.Dispose();
            }
        }

        if (best is null)
        {
            return null;
        }

        using (best.Document)
        {
            return DownloadReleaseImage(
                best.Document,
                best.ReleaseId,
                best.Score,
                options.DiscogsToken);
        }
    }

    internal static Uri BuildSearchUri(
        string artist,
        string albumTitle,
        int? year)
    {
        var query = new List<string>
        {
            "type=release",
            $"artist={Uri.EscapeDataString(artist)}",
            $"release_title={Uri.EscapeDataString(albumTitle)}",
            "per_page=10"
        };
        if (year is > 0)
        {
            query.Add($"year={year.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        return new Uri($"https://api.discogs.com/database/search?{string.Join("&", query)}");
    }

    internal static IReadOnlyDictionary<int, long> ParseOverrides(string? value)
    {
        var result = new Dictionary<int, long>();
        foreach (var line in (value ?? string.Empty)
                     .Split(
                         new[] { '\r', '\n', ',', ';' },
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var albumId) &&
                long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var releaseId) &&
                albumId > 0 &&
                releaseId > 0)
            {
                result[albumId] = releaseId;
            }
        }

        return result;
    }

    private DiscogsCandidate? ScoreRelease(
        JsonElement release,
        long releaseId,
        string expectedArtist,
        string expectedTitle,
        int? expectedYear,
        IReadOnlySet<string> expectedTracks)
    {
        var actualTitle = release.TryGetProperty("title", out var title)
            ? title.GetString() ?? string.Empty
            : string.Empty;
        var actualArtist = release.TryGetProperty("artists_sort", out var artist)
            ? artist.GetString() ?? string.Empty
            : GetJoinedArtists(release);
        var actualYear = release.TryGetProperty("year", out var year) && year.TryGetInt32(out var parsedYear)
            ? parsedYear
            : 0;
        var normalizedExpectedTitle = Normalize(expectedTitle);
        var titleMatches = Normalize(actualTitle) == normalizedExpectedTitle;
        var artistMatches = Normalize(actualArtist) == Normalize(expectedArtist);
        var yearMatches = expectedYear is null ||
                          (actualYear > 0 && Math.Abs(actualYear - expectedYear.Value) <= 1);
        var trackOverlap = CountTrackOverlap(release, expectedTracks);
        var isCompilation = IsVariousArtists(expectedArtist);

        var score = (titleMatches ? 55 : 0) +
                    (artistMatches ? 30 : 0) +
                    (yearMatches ? 10 : 0) +
                    Math.Min(trackOverlap * 5, 30);
        var accepted = isCompilation
            ? titleMatches && yearMatches && trackOverlap >= 2 && score >= 75
            : titleMatches && artistMatches && score >= 80;
        if (!accepted)
        {
            _logger.Debug(
                "AlbumArt Discogs candidate rejected: releaseId={0} score={1} titleMatch={2} artistMatch={3} yearMatch={4} trackOverlap={5}",
                releaseId,
                score,
                titleMatches,
                artistMatches,
                yearMatches,
                trackOverlap);
            return null;
        }

        return new DiscogsCandidate(
            releaseId,
            score,
            JsonDocument.Parse(release.GetRawText()));
    }

    private DiscogsArtworkMatch? DownloadReleaseImage(
        JsonDocument release,
        long releaseId,
        int score,
        string token)
    {
        var imageUrl = SelectPrimaryImage(release.RootElement);
        if (imageUrl is null)
        {
            return null;
        }

        var data = SendBytes(new Uri(imageUrl), token, "image", MaxImageBytes);
        return data is null ? null : new DiscogsArtworkMatch(data, releaseId, score);
    }

    private JsonDocument? GetRelease(long releaseId, string token)
    {
        return SendJson(
            new Uri($"https://api.discogs.com/releases/{releaseId.ToString(CultureInfo.InvariantCulture)}"),
            token,
            "release");
    }

    private JsonDocument? SendJson(Uri uri, string token, string requestKind)
    {
        var data = SendBytes(uri, token, requestKind, 2 * 1024 * 1024);
        if (data is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            _logger.Debug("AlbumArt Discogs returned invalid JSON: kind={0}", requestKind);
            return null;
        }
    }

    private byte[]? SendBytes(
        Uri uri,
        string token,
        string requestKind,
        int maxBytes)
    {
        try
        {
            Throttle();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Discogs", $"token={token}");
            using var response = _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();
            LogRateLimit(response);
            if (response.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or
                HttpStatusCode.NotFound or
                HttpStatusCode.TooManyRequests)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    ApplyRetryAfter(response);
                }

                _logger.Warn(
                    "AlbumArt Discogs request unavailable: kind={0} status={1}",
                    requestKind,
                    (int)response.StatusCode);
                return null;
            }

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength > maxBytes)
            {
                _logger.Warn(
                    "AlbumArt Discogs request failed: kind={0} status={1}",
                    requestKind,
                    (int)response.StatusCode);
                return null;
            }

            using var input = response.Content.ReadAsStream();
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maxBytes)
                {
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            return output.ToArray();
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
            IOException or
            OperationCanceledException)
        {
            _logger.Warn(exception, "AlbumArt Discogs request failed: kind={0}", requestKind);
            return null;
        }
    }

    private void Throttle()
    {
        if (!_throttle)
        {
            return;
        }

        lock (ThrottleLock)
        {
            var now = DateTime.UtcNow;
            if (_nextRequestUtc > now)
            {
                Thread.Sleep(_nextRequestUtc - now);
            }

            _nextRequestUtc = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private void LogRateLimit(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-Discogs-Ratelimit-Remaining", out var remaining))
        {
            _logger.Debug(
                "AlbumArt Discogs rate limit remaining: {0}",
                remaining.FirstOrDefault());
        }
    }

    private void ApplyRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        var delay = retryAfter?.Delta ??
                    (retryAfter?.Date is { } date
                        ? date - DateTimeOffset.UtcNow
                        : TimeSpan.FromSeconds(60));
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        delay = delay > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : delay;
        lock (ThrottleLock)
        {
            var candidate = DateTime.UtcNow.Add(delay);
            if (candidate > _nextRequestUtc)
            {
                _nextRequestUtc = candidate;
            }
        }

        _logger.Debug(
            "AlbumArt Discogs rate limit cooldown: seconds={0}",
            Math.Ceiling(delay.TotalSeconds));
    }

    private static string? SelectPrimaryImage(JsonElement release)
    {
        if (!release.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = images.EnumerateArray().ToList();
        var selected = values.FirstOrDefault(image =>
                           image.TryGetProperty("type", out var type) &&
                           string.Equals(type.GetString(), "primary", StringComparison.OrdinalIgnoreCase));
        if (selected.ValueKind == JsonValueKind.Undefined)
        {
            selected = values.FirstOrDefault();
        }

        if (selected.ValueKind == JsonValueKind.Undefined ||
            !selected.TryGetProperty("uri", out var uri))
        {
            return null;
        }

        var value = uri.GetString();
        return Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
               parsed.Scheme == Uri.UriSchemeHttps &&
               (parsed.Host.Equals("discogs.com", StringComparison.OrdinalIgnoreCase) ||
                parsed.Host.EndsWith(".discogs.com", StringComparison.OrdinalIgnoreCase))
            ? value
            : null;
    }

    private static int? GetReleaseYear(AlbumArtworkLookupContext context)
    {
        return context.Release?.ReleaseDate?.Year ??
               context.Album.ReleaseDate?.Year;
    }

    private static string GetArtistName(AlbumArtworkLookupContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ArtistName))
        {
            return context.ArtistName;
        }

        try
        {
            return context.Album.Artist?.Value?.Name ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlySet<string> GetKnownTrackTitles(AlbumArtworkLookupContext context)
    {
        var titles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in AlbumArtworkResolver.GetOrderedReleases(context))
        {
            try
            {
                foreach (var track in release.Tracks?.Value ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(track.Title))
                    {
                        titles.Add(Normalize(track.Title));
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // Track metadata is optional in this lookup context.
            }
        }

        return titles;
    }

    private static int CountTrackOverlap(
        JsonElement release,
        IReadOnlySet<string> expectedTracks)
    {
        if (expectedTracks.Count == 0 ||
            !release.TryGetProperty("tracklist", out var tracklist) ||
            tracklist.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return tracklist.EnumerateArray()
            .Select(track => track.TryGetProperty("title", out var title)
                ? Normalize(title.GetString() ?? string.Empty)
                : string.Empty)
            .Count(expectedTracks.Contains);
    }

    private static string GetJoinedArtists(JsonElement release)
    {
        if (!release.TryGetProperty("artists", out var artists) ||
            artists.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            artists.EnumerateArray()
                .Select(value => value.TryGetProperty("name", out var name)
                    ? name.GetString()
                    : null)
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static bool IsVariousArtists(string artist)
    {
        var normalized = Normalize(artist);
        return normalized is "various artists" or "various";
    }

    internal static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var withoutMarks = new string(decomposed
            .Where(character =>
                CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray());
        return WhitespaceRegex()
            .Replace(PunctuationRegex().Replace(withoutMarks, " "), " ")
            .Trim()
            .ToLowerInvariant();
    }

    private sealed record DiscogsCandidate(
        long ReleaseId,
        int Score,
        JsonDocument Document);

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex PunctuationRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
