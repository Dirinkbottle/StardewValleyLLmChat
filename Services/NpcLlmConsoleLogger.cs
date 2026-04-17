using System.Text.Json;
using StardewModdingAPI;

namespace StardewMod.Services;

/// <summary>
/// 统一格式化 NPC LLM 控制台日志。
/// SMAPI 会根据 LogLevel 自动着色，因此这里只负责分级和结构化前缀。
/// </summary>
internal sealed class NpcLlmConsoleLogger
{
    private readonly IMonitor monitor;
    private readonly Func<bool> verboseEnabled;

    public NpcLlmConsoleLogger(IMonitor monitor, Func<bool> verboseEnabled)
    {
        this.monitor = monitor;
        this.verboseEnabled = verboseEnabled;
    }

    public void Info(string area, string message, string? subject = null)
    {
        this.Write(LogLevel.Info, area, message, subject);
    }

    public void Debug(string area, string message, string? subject = null)
    {
        if (!this.verboseEnabled())
        {
            return;
        }

        this.Write(LogLevel.Debug, area, message, subject);
    }

    public void Warn(string area, string message, string? subject = null)
    {
        this.Write(LogLevel.Warn, area, message, subject);
    }

    public void Error(string area, string message, string? subject = null)
    {
        this.Write(LogLevel.Error, area, message, subject);
    }

    public void DebugJson(string area, string title, object value, string? subject = null)
    {
        if (!this.verboseEnabled())
        {
            return;
        }

        string json = JsonSerializer.Serialize(value);
        this.Write(LogLevel.Debug, area, $"{title}: {Trim(json, 900)}", subject);
    }

    public string Summarize(string? value, int maxLength = 180)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return Trim(normalized, maxLength);
    }

    private void Write(LogLevel level, string area, string message, string? subject)
    {
        string prefix = $"[NPC LLM][{area}]";
        if (!string.IsNullOrWhiteSpace(subject))
        {
            prefix += $"[{subject}]";
        }

        this.monitor.Log($"{prefix} {message}", level);
    }

    private static string Trim(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..Math.Max(0, maxLength - 3)] + "...";
    }
}
