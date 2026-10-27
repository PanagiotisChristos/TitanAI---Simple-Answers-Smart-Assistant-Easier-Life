using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Project_no._1.Services;

public interface IWebSearchService
{
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);
}

public sealed class TavilyWebSearchService(
    IHttpClientFactory httpClientFactory,
    IWebSearchQuotaStore quotaStore) : IWebSearchService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(1);
    private const int MaxCachedQueries = 256;
    private readonly ConcurrentDictionary<string, CachedSearch> _cache = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        var apiKey = Environment.GetEnvironmentVariable("TAVILY_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new WebSearchServiceException(
                StatusCodes.Status503ServiceUnavailable,
                "Web search is not configured",
                "Set TAVILY_API_KEY in the server environment and restart the app.");
        }

        var cacheKey = NormalizeQuery(query);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Results;
            }

            _cache.TryRemove(cacheKey, out _);
        }

        var reservation = await quotaStore.TryReserveAsync(cancellationToken);
        if (!reservation.Allowed)
        {
            var period = reservation.LimitedPeriod ?? "usage";
            throw new WebSearchServiceException(
                StatusCodes.Status429TooManyRequests,
                $"Web search {period} limit reached",
                period switch
                {
                    "hourly" => $"To protect the monthly free allowance, web search is limited to {reservation.Limit} new searches per hour. Try again later.",
                    "daily" => $"To protect the monthly free allowance, web search is limited to {reservation.Limit} new searches per day. Try again tomorrow.",
                    _ => $"The monthly web search limit of {reservation.Limit} has been reached. It will reset at the start of next month."
                });
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "search")
        {
            Content = JsonContent.Create(new
            {
                api_key = apiKey,
                query,
                search_depth = "basic",
                max_results = 5,
                include_answer = false,
                include_raw_content = false
            })
        };

        try
        {
            using var response = await httpClientFactory.CreateClient("TavilySearch")
                .SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
                var unauthorized = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
                throw new WebSearchServiceException(
                    rateLimited ? StatusCodes.Status429TooManyRequests : StatusCodes.Status502BadGateway,
                    rateLimited ? "Web search rate limit reached" : "Web search failed",
                    unauthorized
                        ? "Check that TAVILY_API_KEY is valid and enabled for search."
                        : rateLimited
                            ? "The web search provider is busy. Please try again shortly."
                            : $"Tavily returned HTTP {(int)response.StatusCode}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!payload.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("The Tavily response did not contain results.");
            }

            var searchResults = results.EnumerateArray()
                .Select(ParseResult)
                .Where(result => result is not null)
                .Select(result => result!)
                .DistinctBy(result => result.Url, StringComparer.OrdinalIgnoreCase)
                .Take(5)
                .ToArray();

            _cache[cacheKey] = new CachedSearch(DateTimeOffset.UtcNow.Add(CacheDuration), searchResults);
            if (_cache.Count > MaxCachedQueries)
            {
                foreach (var entry in _cache.OrderBy(entry => entry.Value.ExpiresAt)
                             .Take(_cache.Count - MaxCachedQueries))
                {
                    _cache.TryRemove(entry.Key, out _);
                }
            }

            return searchResults;
        }
        catch (WebSearchServiceException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            throw new WebSearchServiceException(
                StatusCodes.Status502BadGateway,
                "Could not reach the web search provider",
                "Check the server's internet connection and try again.");
        }
        catch (JsonException)
        {
            throw new WebSearchServiceException(
                StatusCodes.Status502BadGateway,
                "Tavily returned an unreadable response",
                "Please try searching again in a moment.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSearchServiceException(
                StatusCodes.Status504GatewayTimeout,
                "Web search took too long",
                "Please try searching again in a moment.");
        }
    }

    private static WebSearchResult? ParseResult(JsonElement result)
    {
        if (!result.TryGetProperty("url", out var urlElement) ||
            urlElement.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(urlElement.GetString(), UriKind.Absolute, out var url) ||
            url.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var title = GetText(result, "title", 300);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        return new WebSearchResult(title, url.AbsoluteUri, GetText(result, "content", 1_500));
    }

    private static string GetText(JsonElement element, string propertyName, int maxLength)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(Regex.Replace(value.GetString() ?? string.Empty, "<[^>]*>", " "));
        var trimmed = text.Trim();
        return trimmed[..Math.Min(trimmed.Length, maxLength)];
    }

    private static string NormalizeQuery(string query) =>
        string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();

    private sealed record CachedSearch(DateTimeOffset ExpiresAt, IReadOnlyList<WebSearchResult> Results);
}

public sealed record WebSearchResult(string Title, string Url, string Description);

public sealed class WebSearchServiceException(int statusCode, string title, string detail) : Exception(detail)
{
    public int StatusCode { get; } = statusCode;
    public string Title { get; } = title;
}
