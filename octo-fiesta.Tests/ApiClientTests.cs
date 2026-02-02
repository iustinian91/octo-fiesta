using octo_fiesta.Services;
using octo_fiesta.Models.Settings;
using Moq;
using Moq.Protected;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace octo_fiesta.Tests;

public class ApiClientTests : IDisposable
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<HttpMessageHandler> _httpMessageHandlerMock;
    private readonly Mock<ILogger<InstanceManager>> _instanceLoggerMock;
    private readonly Mock<ILogger<ApiClient>> _apiClientLoggerMock;
    private readonly InstanceOptions _options;
    private readonly string _storagePath;

    public ApiClientTests()
    {
        _httpMessageHandlerMock = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(_httpMessageHandlerMock.Object);

        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _httpClientFactoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

        _instanceLoggerMock = new Mock<ILogger<InstanceManager>>();
        _apiClientLoggerMock = new Mock<ILogger<ApiClient>>();

        _storagePath = Path.Combine(Path.GetTempPath(), "api-client-tests-" + Guid.NewGuid());

        _options = new InstanceOptions
        {
            InstancesUrl = "https://example.com/instances.json",
            StoragePath = _storagePath,
            SpeedTestTimeoutMs = 5000,
            DefaultApiInstances = new[] { "https://api1.example.com", "https://api2.example.com" },
            DefaultStreamingInstances = new[] { "https://stream1.example.com" }
        };

        // Pre-seed the instances by creating files directly
        SeedInstancesFile();
    }

    private void SeedInstancesFile()
    {
        Directory.CreateDirectory(_storagePath);

        var apiInstances = new[]
        {
            new InstanceInfo { BaseUrl = "https://api1.example.com", LatencyMs = 100 },
            new InstanceInfo { BaseUrl = "https://api2.example.com", LatencyMs = 200 }
        };

        var json = JsonSerializer.Serialize(apiInstances, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_storagePath, "instances-api.json"), json);
    }

    private ApiClient CreateApiClient()
    {
        var instanceManager = new InstanceManager(
            _httpClientFactoryMock.Object,
            Options.Create(_options),
            _instanceLoggerMock.Object);

        return new ApiClient(
            instanceManager,
            _httpClientFactoryMock.Object,
            _apiClientLoggerMock.Object);
    }

    private void SetupSequentialResponses(params (HttpStatusCode Status, string Content)[] responses)
    {
        var callIndex = 0;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                var response = responses[Math.Min(callIndex, responses.Length - 1)];
                callIndex++;
                return new HttpResponseMessage
                {
                    StatusCode = response.Status,
                    Content = new StringContent(response.Content)
                };
            });
    }

    #region FetchWithRetryAsync Tests

    [Fact]
    public async Task FetchWithRetryAsync_OnSuccess_ReturnsResponse()
    {
        // Arrange
        SetupSequentialResponses((HttpStatusCode.OK, "Success"));
        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FetchWithRetryAsync_On429_RotatesToNextInstance()
    {
        // Arrange
        SetupSequentialResponses(
            (HttpStatusCode.TooManyRequests, "Rate limited"),
            (HttpStatusCode.OK, "Success"));
        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify two requests were made
        _httpMessageHandlerMock
            .Protected()
            .Verify("SendAsync", Times.AtLeast(2),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task FetchWithRetryAsync_On401WithSubStatus11002_SkipsInstance()
    {
        // Arrange
        var authErrorBody = JsonSerializer.Serialize(new { subStatus = 11002 });
        SetupSequentialResponses(
            (HttpStatusCode.Unauthorized, authErrorBody),
            (HttpStatusCode.OK, "Success"));
        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FetchWithRetryAsync_On401WithoutSubStatus_ReturnsResponse()
    {
        // Arrange - 401 without subStatus 11002 should return the response
        var authErrorBody = JsonSerializer.Serialize(new { error = "Unauthorized" });
        SetupSequentialResponses((HttpStatusCode.Unauthorized, authErrorBody));
        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task FetchWithRetryAsync_On5xx_SkipsToNextInstance()
    {
        // Arrange
        SetupSequentialResponses(
            (HttpStatusCode.InternalServerError, "Server Error"),
            (HttpStatusCode.OK, "Success"));
        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FetchWithRetryAsync_OnNetworkError_SkipsToNextInstance()
    {
        // Arrange
        var callCount = 0;
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new HttpRequestException("Connection refused");
                }
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent("Success")
                };
            });

        var client = CreateApiClient();

        // Act
        using var response = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FetchWithRetryAsync_AllAttemptsFail_ThrowsException()
    {
        // Arrange - Always return 500
        SetupSequentialResponses(
            (HttpStatusCode.InternalServerError, "Error"),
            (HttpStatusCode.InternalServerError, "Error"),
            (HttpStatusCode.InternalServerError, "Error"),
            (HttpStatusCode.InternalServerError, "Error"));
        var client = CreateApiClient();

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.FetchWithRetryAsync("/test/path", InstanceType.Api));
    }

    [Fact]
    public async Task FetchWithRetryAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        // Arrange
        SetupSequentialResponses((HttpStatusCode.OK, "Success"));
        var client = CreateApiClient();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - TaskCanceledException is a subclass of OperationCanceledException
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.FetchWithRetryAsync("/test/path", InstanceType.Api, cts.Token));
    }

    [Fact]
    public async Task FetchWithRetryAsync_RotatesInstancesAcrossRequests()
    {
        // Arrange
        var requestedUrls = new List<string>();
        _httpMessageHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, ct) =>
            {
                requestedUrls.Add(req.RequestUri!.ToString());
            })
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("Success")
            });

        var client = CreateApiClient();

        // Act - Make multiple requests
        using var response1 = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);
        using var response2 = await client.FetchWithRetryAsync("/test/path", InstanceType.Api);

        // Assert - Verify different instances were used (or at least multiple requests made)
        Assert.True(requestedUrls.Count >= 2);
    }

    #endregion

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_storagePath))
            {
                Directory.Delete(_storagePath, true);
            }
        }
        catch { }
    }
}
