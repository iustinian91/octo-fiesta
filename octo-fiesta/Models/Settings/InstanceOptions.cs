namespace octo_fiesta.Models.Settings;

/// <summary>
/// Configuration options for the instance manager and API client.
/// </summary>
public class InstanceOptions
{
    /// <summary>
    /// Remote URL to fetch instances.json from.
    /// Defaults to a raw GitHub URL.
    /// </summary>
    public string InstancesUrl { get; set; } = "https://raw.githubusercontent.com/example/config/main/instances.json";

    /// <summary>
    /// Directory path for persisting instance data (ordering and speed cache).
    /// </summary>
    public string StoragePath { get; set; } = "./data";

    /// <summary>
    /// Timeout in milliseconds for speed test requests.
    /// </summary>
    public int SpeedTestTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Interval in milliseconds between background refresh cycles.
    /// Default: 1 hour (3600000 ms)
    /// </summary>
    public int RefreshIntervalMs { get; set; } = 3600000;

    /// <summary>
    /// Default instances to use when remote fetch fails (api group).
    /// </summary>
    public string[] DefaultApiInstances { get; set; } = new[]
    {
        "https://api.monochrome.tf/",
        "https://tidal-api.binimum.org",
        "https://triton.squid.wtf",
        "https://wolf.qqdl.site"
    };

    /// <summary>
    /// Default instances to use when remote fetch fails (streaming group).
    /// </summary>
    public string[] DefaultStreamingInstances { get; set; } = new[]
    {
        "https://api.monochrome.tf/",
        "https://triton.squid.wtf",
        "https://wolf.qqdl.site"
    };
}
