namespace StardewMod.Models;

/// <summary>
/// 结构化事实记忆的作用域。
/// persistent 表示长期事实，today 表示仅当天有效。
/// </summary>
public static class NpcMemoryFactScopes
{
    public const string Persistent = "persistent";
    public const string Today = "today";

    public static string Normalize(string? scope)
    {
        if (string.Equals(scope, Today, StringComparison.OrdinalIgnoreCase))
        {
            return Today;
        }

        return Persistent;
    }

    public static bool IsActiveForGameDate(MemoryFactRecord fact, string gameDate)
    {
        string normalizedScope = Normalize(fact.Scope);
        return normalizedScope == Persistent
            || string.Equals(fact.GameDate, gameDate, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// LLM 可更新的结构化事实。
/// 用于承载长期偏好、今日状态和被玩家纠正后的确定性信息。
/// </summary>
public sealed class MemoryFactRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string NpcName { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string Scope { get; set; } = NpcMemoryFactScopes.Persistent;

    public string Category { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string SourceEventType { get; set; } = string.Empty;

    public string GameDate { get; set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Dictionary<string, string> Metadata { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// memory_update 工具写入事实时使用的输入模型。
/// </summary>
public sealed class MemoryFactUpdate
{
    public string Key { get; set; } = string.Empty;

    public string Scope { get; set; } = NpcMemoryFactScopes.Persistent;

    public string Category { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}
