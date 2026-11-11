using System.Text.Json.Serialization;

namespace Companion.Diagnostics;

// Diagnostic contracts only. Nothing in Phase 0 confirms or accumulates loot.
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EventType { ChestObtained, EquipmentObtained, SessionStarted, SessionEnded, UnknownEvent }
public record CandidateEvent(string EventId, string SessionId, EventType EventType, DateTimeOffset EventTime,
    string Source, string? EquipmentName, string? EquipmentQuality, double? Confidence,
    double? RuleConfirmationScore, bool IsConfirmed = false);

public readonly record struct Region(int X, int Y, int Width, int Height)
{
    public static Region Parse(string value)
    {
        var p = value.Split(',').Select(int.Parse).ToArray();
        if (p.Length != 4) throw new ArgumentException("区域格式应为 x,y,width,height（捕获帧像素）。");
        var result = new Region(p[0], p[1], p[2], p[3]);
        result.Validate(16384, 16384);
        return result;
    }
    public void Validate(int width, int height)
    {
        if (X < 0 || Y < 0 || Width <= 0 || Height <= 0 || (long)X+Width > width || (long)Y+Height > height ||
            (long)Width*Height > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(Region), "区域越界或超过 200 万像素；不会回退为全屏识别。");
    }
}
