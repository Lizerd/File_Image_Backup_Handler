namespace MediaBackupTool.Models.Enums;

/// <summary>
/// CPU usage profile controlling parallelism during scan/hash/copy operations.
/// </summary>
public enum CpuProfile
{
    /// <summary>1 worker - minimal impact, PC stays responsive</summary>
    Eco = 0,

    /// <summary>~25% of cores - default, balanced performance</summary>
    Balanced = 1,

    /// <summary>~75% of cores - faster, higher resource usage</summary>
    Fast = 2,

    /// <summary>cores - 1 - maximum speed, dedicated run</summary>
    Max = 3
}
