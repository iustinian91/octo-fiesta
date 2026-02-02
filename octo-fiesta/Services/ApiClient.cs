using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services;

/// <summary>
/// HTTP client that uses instance rotation and retry logic when making requests.
/// </summary>
public class ApiClient
{
    private readonly InstanceManager _instanceManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ApiClient> _logger;

    // Global index for round-robin rotation across requests
    private int _globalApiIndex;
    private int _globalStreamingIndex;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiClient"/> class.
    /// </summary>
    public ApiClient(
        InstanceManager instanceManager,
        IHttpClientFactory httpClientFactory,
        ILogger<ApiClient> logger)
    {
        _instanceManager = instanceManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Fetches a resource with automatic instance rotation and retry logic.
    /// </summary>
    /// <param name="relativePath">The relative path to request.</param>
    /// <param name="type">The type of instance to use (api or streaming).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>HttpResponseMessage on success (caller responsible for disposal).</returns>
    /// <exception cref="HttpRequestException">Thrown when all attempts fail.</exception>
    public async Task<HttpResponseMessage> FetchWithRetryAsync(
        string relativePath,
        InstanceType type,
        CancellationToken cancellationToken = default)
    {
        var instances = await _instanceManager.GetInstancesAsync(type, cancellationToken);
        if (instances.Count == 0)
        {
            throw new InvalidOperationException("No instances available");
        }

        var maxTotalAttempts = instances.Count * 2;
        var startIndex = GetAndIncrementIndex(type) % instances.Count;

        Exception? lastException = null;
        var client = _httpClientFactory.CreateClient("apiClient");

        for (var attempt = 0; attempt < maxTotalAttempts; attempt++)
        {
            var instanceIndex = (startIndex + attempt) % instances.Count;
            var instance = instances[instanceIndex];
            var url = InstanceManager.CombineUrl(instance.BaseUrl, relativePath);

            try
            {
                _logger.LogDebug("Attempt {Attempt}/{Max}: {Url}", attempt + 1, maxTotalAttempts, url);

                var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                // Success case
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                var statusCode = (int)response.StatusCode;

                // Handle 429 - Rate limited
                if (statusCode == 429)
                {
                    _logger.LogWarning("Rate limited by {Instance}, rotating to next", instance.BaseUrl);
                    response.Dispose();
                    await Task.Delay(500, cancellationToken);
                    IncrementIndex(type);
                    continue;
                }

                // Handle 401 - Check for subStatus 11002
                if (statusCode == 401)
                {
                    if (await IsInstanceAuthFailureAsync(response, cancellationToken))
                    {
                        _logger.LogWarning("Instance auth failure at {Instance}, skipping", instance.BaseUrl);
                        response.Dispose();
                        IncrementIndex(type);
                        continue;
                    }
                }

                // Handle 5xx - Server errors
                if (statusCode >= 500)
                {
                    _logger.LogWarning("Server error {StatusCode} from {Instance}, skipping", statusCode, instance.BaseUrl);
                    response.Dispose();
                    IncrementIndex(type);
                    continue;
                }

                // For other status codes (4xx except 429/401), return the response
                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout
                _logger.LogWarning("Timeout for {Instance}, skipping", instance.BaseUrl);
                lastException = new HttpRequestException($"Request to {instance.BaseUrl} timed out");
                await Task.Delay(200, cancellationToken);
                IncrementIndex(type);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Network error for {Instance}, skipping", instance.BaseUrl);
                lastException = ex;
                await Task.Delay(200, cancellationToken);
                IncrementIndex(type);
            }
            catch (OperationCanceledException)
            {
                // User cancellation
                throw;
            }
        }

        _logger.LogError("All {Attempts} attempts failed", maxTotalAttempts);
        throw lastException ?? new HttpRequestException("All request attempts failed");
    }

    /// <summary>
    /// Checks if the 401 response indicates an instance-specific auth failure (subStatus 11002).
    /// </summary>
    private async Task<bool> IsInstanceAuthFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("subStatus", out var subStatusElement))
            {
                if (subStatusElement.TryGetInt32(out var subStatus) && subStatus == 11002)
                {
                    return true;
                }
            }
        }
        catch
        {
            // If we can't parse the response, assume it's not the specific auth failure
        }

        return false;
    }

    /// <summary>
    /// Gets the current index and increments for next call.
    /// </summary>
    private int GetAndIncrementIndex(InstanceType type)
    {
        if (type == InstanceType.Api)
        {
            return Interlocked.Increment(ref _globalApiIndex) - 1;
        }
        else
        {
            return Interlocked.Increment(ref _globalStreamingIndex) - 1;
        }
    }

    /// <summary>
    /// Increments the global index (used when skipping an instance).
    /// </summary>
    private void IncrementIndex(InstanceType type)
    {
        if (type == InstanceType.Api)
        {
            Interlocked.Increment(ref _globalApiIndex);
        }
        else
        {
            Interlocked.Increment(ref _globalStreamingIndex);
        }
    }
}
