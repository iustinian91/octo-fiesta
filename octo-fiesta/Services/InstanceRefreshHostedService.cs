using Microsoft.Extensions.Options;
using octo_fiesta.Models.Settings;

namespace octo_fiesta.Services;

/// <summary>
/// Background service that periodically refreshes instance speed tests.
/// </summary>
public class InstanceRefreshHostedService : BackgroundService
{
    private readonly InstanceManager _instanceManager;
    private readonly InstanceOptions _options;
    private readonly ILogger<InstanceRefreshHostedService> _logger;

    /// <summary>
    /// Event raised when a refresh is triggered externally.
    /// </summary>
    private readonly ManualResetEventSlim _refreshTrigger = new(false);

    /// <summary>
    /// Initializes a new instance of the <see cref="InstanceRefreshHostedService"/> class.
    /// </summary>
    public InstanceRefreshHostedService(
        InstanceManager instanceManager,
        IOptions<InstanceOptions> options,
        ILogger<InstanceRefreshHostedService> logger)
    {
        _instanceManager = instanceManager;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Triggers an immediate refresh of all speed tests.
    /// </summary>
    public void TriggerRefresh()
    {
        _refreshTrigger.Set();
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Instance refresh service started with interval {Interval}ms", _options.RefreshIntervalMs);

        // Initial load/test on startup
        try
        {
            await _instanceManager.RefreshAllAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform initial instance refresh");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval or until triggered
                var waitResult = _refreshTrigger.Wait(_options.RefreshIntervalMs, stoppingToken);

                if (waitResult)
                {
                    _refreshTrigger.Reset();
                    _logger.LogInformation("Manual refresh triggered");
                }

                await _instanceManager.RefreshAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during instance refresh");
            }
        }

        _logger.LogInformation("Instance refresh service stopped");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _refreshTrigger.Dispose();
        base.Dispose();
    }
}
