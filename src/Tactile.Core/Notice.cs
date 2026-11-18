namespace Tactile.Core;

/// <param name="Kind">One of <see cref="NoticeKinds"/>.</param>
/// <param name="Progress">0-1 for long running work, otherwise null.</param>
public sealed record Notice(string Kind, string Message, double? Progress = null);

public static class NoticeKinds
{
    public const string LowBattery = "lowBattery";
    public const string BrokenFile = "brokenFile";
    public const string IdleSleep = "idleSleep";
    public const string GyroCalibration = "gyroCalibration";
    public const string Info = "info";
}
