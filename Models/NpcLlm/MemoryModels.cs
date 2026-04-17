using System.Text.Json.Serialization;

namespace StardewMod.Models;

/// <summary>
/// 单条记忆记录。
/// </summary>
public sealed class MemoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string NpcName { get; set; } = string.Empty;

    public string EventType { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string EmbeddingRef { get; set; } = string.Empty;

    public float[] Embedding { get; set; } = Array.Empty<float>();

    [JsonIgnore]
    public float Similarity { get; set; }
}

/// <summary>
/// 日终摘要记录。
/// </summary>
public sealed class DayMemoryRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string NpcName { get; set; } = string.Empty;

    public string GameDate { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string ScheduleKey { get; set; } = string.Empty;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// 调试日志记录。
/// </summary>
public sealed class NpcAgentDebugRecord
{
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public string NpcName { get; set; } = string.Empty;

    public string Trigger { get; set; } = string.Empty;

    public string RequestSummary { get; set; } = string.Empty;

    public string ResponseSummary { get; set; } = string.Empty;

    public List<string> ToolCalls { get; set; } = new();

    public string PatchSummary { get; set; } = string.Empty;

    public string RejectionReason { get; set; } = string.Empty;
}
