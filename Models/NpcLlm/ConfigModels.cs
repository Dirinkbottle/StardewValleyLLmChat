namespace StardewMod.Models;

/// <summary>
/// <c>mod.toml</c> 的完整配置模型。
/// </summary>
public sealed class ModTomlConfig
{
    public AiRouterConfig Router { get; set; } = new();

    public Dictionary<string, AiProviderConfig> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AiEmbeddingConfig Embeddings { get; set; } = new();

    public AiDebugConfig Debug { get; set; } = new();

    public AiBroadcastConfig Broadcast { get; set; } = new();

    public AiPerceptionConfig Perception { get; set; } = new();
}

/// <summary>
/// Router 的全局请求行为。
/// </summary>
public sealed class AiRouterConfig
{
    public int DefaultTimeoutSeconds { get; set; } = 45;

    public int MaxRetryCount { get; set; } = 1;

    public int ToolLoopMaxRounds { get; set; } = 4;

    public int RetryBackoffMilliseconds { get; set; } = 1500;

    public bool EnableVerboseDebug { get; set; } = true;
}

/// <summary>
/// 单个 AI 提供者配置。
/// </summary>
public sealed class AiProviderConfig
{
    private const string PlaceholderApiKey = "PUT_YOUR_TOKEN_HERE";

    public string Kind { get; set; } = "openai";

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int MaxTokens { get; set; } = 1536;

    public bool ThinkingEnabled { get; set; }

    public float Temperature { get; set; } = 0.7f;

    public bool ToolCallingRequired { get; set; } = true;

    public int? TimeoutSeconds { get; set; }

    public bool HasValidApiKey()
    {
        return !string.IsNullOrWhiteSpace(this.ApiKey)
            && !string.Equals(this.ApiKey.Trim(), PlaceholderApiKey, StringComparison.OrdinalIgnoreCase);
    }

    public bool HasConnectionSettings()
    {
        return !string.IsNullOrWhiteSpace(this.BaseUrl)
            && this.HasValidApiKey();
    }

    public bool IsConfigured()
    {
        return this.HasConnectionSettings()
            && !string.IsNullOrWhiteSpace(this.Model);
    }

    public bool IsLikelyEmbeddingModel()
    {
        string model = this.Model;
        return model.Contains("embedding", StringComparison.OrdinalIgnoreCase)
            || model.Contains("embed", StringComparison.OrdinalIgnoreCase)
            || model.Contains("bge", StringComparison.OrdinalIgnoreCase)
            || model.Contains("e5", StringComparison.OrdinalIgnoreCase)
            || model.Contains("gte", StringComparison.OrdinalIgnoreCase)
            || model.Contains("bce", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsUsableForChat()
    {
        return this.IsConfigured()
            && this.ToolCallingRequired
            && !this.IsLikelyEmbeddingModel();
    }

    public bool IsUsableForEmbeddings()
    {
        return this.IsConfigured();
    }
}

/// <summary>
/// 远端 embedding 模型配置。
/// </summary>
public sealed class AiEmbeddingConfig
{
    public string ProviderName { get; set; } = string.Empty;

    public bool IsConfigured()
    {
        return !string.IsNullOrWhiteSpace(this.ProviderName);
    }
}

/// <summary>
/// 调试与落盘配置。
/// </summary>
public sealed class AiDebugConfig
{
    public bool SaveRequestSummary { get; set; } = true;

    public bool SaveResponseSummary { get; set; } = true;

    public bool SaveToolCalls { get; set; } = true;

    public bool SavePatchDiff { get; set; } = true;

    public bool SaveEmbeddingHits { get; set; } = true;
}

/// <summary>
/// NPC 邻域广播配置。
/// </summary>
public sealed class AiBroadcastConfig
{
    public int MaxHops { get; set; } = 5;
}

/// <summary>
/// NPC 感知相关配置。
/// </summary>
public sealed class AiPerceptionConfig
{
    public int NpcRadiusTiles { get; set; } = 100;
}
