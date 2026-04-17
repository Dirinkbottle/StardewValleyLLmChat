using System.Globalization;
using System.Text;
using StardewMod.Models;
using StardewModdingAPI;

namespace StardewMod.Services;

/// <summary>
/// 读取并校验 <c>mod.toml</c>。这里只实现本模组需要的稳定子集，避免引入额外依赖。
/// </summary>
internal sealed class NpcLlmConfigService
{
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly string configPath;
    private ModTomlConfig current = new();

    public NpcLlmConfigService(IModHelper helper, IMonitor monitor)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.configPath = Path.Combine(helper.DirectoryPath, "mod.toml");
    }

    public ModTomlConfig Current => this.current;

    public string ConfigPath => this.configPath;

    public IReadOnlyList<string> ValidationErrors { get; private set; } = Array.Empty<string>();

    public void LoadOrCreate()
    {
        if (!File.Exists(this.configPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(this.configPath)!);
            File.WriteAllText(this.configPath, this.BuildDefaultToml(), Encoding.UTF8);
            this.monitor.Log($"[NPC LLM][Config] 未找到 mod.toml，已自动创建默认文件：{this.configPath}", LogLevel.Info);
        }

        this.current = this.Parse(File.ReadAllText(this.configPath, Encoding.UTF8));
        this.ValidationErrors = this.Validate(this.current);
        this.monitor.Log(
            $"[NPC LLM][Config] 已载入 mod.toml providers={this.current.Providers.Count} embeddings_provider={this.current.Embeddings.ProviderName}",
            LogLevel.Info);

        foreach ((string name, AiProviderConfig provider) in this.current.Providers.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string modelLabel = string.IsNullOrWhiteSpace(provider.Model) ? "<not-set>" : provider.Model;
            this.monitor.Log(
                $"[NPC LLM][Config][{name}] kind={provider.Kind} chat_usable={provider.IsUsableForChat()} embedding_usable={provider.IsUsableForEmbeddings()} model={modelLabel}",
                LogLevel.Info);
        }

        foreach (string error in this.ValidationErrors)
        {
            this.monitor.Log($"mod.toml 配置问题：{error}", LogLevel.Warn);
        }
    }

    public IReadOnlyList<string> GetProviderNames()
    {
        return this.current.Providers.Keys
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private ModTomlConfig Parse(string content)
    {
        ModTomlConfig config = new();
        string section = string.Empty;
        string providerName = string.Empty;

        foreach (string rawLine in content.Replace("\r", string.Empty).Split('\n'))
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            {
                continue;
            }

            int commentIndex = line.IndexOf('#');
            if (commentIndex >= 0)
            {
                line = line[..commentIndex].Trim();
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string header = line[1..^1].Trim();
                section = header;
                providerName = string.Empty;
                if (header.StartsWith("providers.", StringComparison.OrdinalIgnoreCase))
                {
                    providerName = header["providers.".Length..].Trim();
                    if (!config.Providers.ContainsKey(providerName))
                    {
                        config.Providers[providerName] = new AiProviderConfig();
                    }
                }

                continue;
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
            {
                continue;
            }

            string key = line[..equalsIndex].Trim();
            string value = line[(equalsIndex + 1)..].Trim();
            object parsed = this.ParseValue(value);

            switch (section)
            {
                case "router":
                    this.ApplyRouterValue(config.Router, key, parsed);
                    break;
                case "embeddings":
                    this.ApplyEmbeddingsValue(config.Embeddings, key, parsed);
                    break;
                case "debug":
                    this.ApplyDebugValue(config.Debug, key, parsed);
                    break;
                case "broadcast":
                    this.ApplyBroadcastValue(config.Broadcast, key, parsed);
                    break;
                case "perception":
                    this.ApplyPerceptionValue(config.Perception, key, parsed);
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(providerName) && config.Providers.TryGetValue(providerName, out AiProviderConfig? provider))
                    {
                        this.ApplyProviderValue(provider, key, parsed);
                    }

                    break;
            }
        }

        return config;
    }

    private IReadOnlyList<string> Validate(ModTomlConfig config)
    {
        List<string> errors = new();
        if (config.Providers.Count == 0)
        {
            errors.Add("至少需要配置一个 providers.<name> 节。");
        }

        if (config.Broadcast.MaxHops < 1)
        {
            errors.Add("broadcast.max_hops 必须大于等于 1。");
        }

        if (config.Perception.NpcRadiusTiles < 1)
        {
            errors.Add("perception.npc_radius_tiles 必须大于等于 1。");
        }

        foreach ((string name, AiProviderConfig provider) in config.Providers)
        {
            if (!string.Equals(provider.Kind, "openai", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(provider.Kind, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"provider '{name}' 的 kind 只支持 openai 或 anthropic，当前为：{provider.Kind}");
            }

            if (!provider.ToolCallingRequired)
            {
                errors.Add($"provider '{name}' 的 tool_calling_required 必须为 true。");
            }
        }

        if (!config.Providers.Any(pair => pair.Value.IsUsableForChat()))
        {
            errors.Add("没有可用于 NPC Agent 的聊天 provider。请至少完整配置一个 openai-compatible 或 anthropic-compatible 聊天模型。");
        }

        if (config.Embeddings.IsConfigured() && !config.Providers.ContainsKey(config.Embeddings.ProviderName))
        {
            errors.Add($"embeddings.provider_name 指向了不存在的 provider：{config.Embeddings.ProviderName}");
        }

        if (config.Embeddings.IsConfigured() &&
            config.Providers.TryGetValue(config.Embeddings.ProviderName, out AiProviderConfig? embeddingProvider) &&
            string.Equals(embeddingProvider.Kind, "anthropic", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"embeddings.provider_name 当前指向 '{config.Embeddings.ProviderName}'，但 Anthropic-compatible provider 在第一阶段不支持 embeddings。");
        }

        if (config.Embeddings.IsConfigured() &&
            config.Providers.TryGetValue(config.Embeddings.ProviderName, out AiProviderConfig? embeddingProvider2) &&
            !embeddingProvider2.IsUsableForEmbeddings())
        {
            errors.Add($"embeddings.provider_name 指向的 provider '{config.Embeddings.ProviderName}' 尚未完整配置，无法生成 embedding。");
        }

        return errors;
    }

    private object ParseValue(string rawValue)
    {
        string value = rawValue.Trim();
        if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
        {
            return value[1..^1].Replace("\\\"", "\"");
        }

        if (bool.TryParse(value, out bool boolValue))
        {
            return boolValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int intValue))
        {
            return intValue;
        }

        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float floatValue))
        {
            return floatValue;
        }

        return value;
    }

    private void ApplyRouterValue(AiRouterConfig config, string key, object value)
    {
        switch (key)
        {
            case "default_timeout_seconds":
                config.DefaultTimeoutSeconds = this.GetInt(value, config.DefaultTimeoutSeconds);
                break;
            case "max_retry_count":
                config.MaxRetryCount = this.GetInt(value, config.MaxRetryCount);
                break;
            case "tool_loop_max_rounds":
                config.ToolLoopMaxRounds = this.GetInt(value, config.ToolLoopMaxRounds);
                break;
            case "retry_backoff_milliseconds":
                config.RetryBackoffMilliseconds = this.GetInt(value, config.RetryBackoffMilliseconds);
                break;
            case "enable_verbose_debug":
                config.EnableVerboseDebug = this.GetBool(value, config.EnableVerboseDebug);
                break;
        }
    }

    private void ApplyEmbeddingsValue(AiEmbeddingConfig config, string key, object value)
    {
        switch (key)
        {
            case "provider_name":
                config.ProviderName = this.GetString(value);
                break;
        }
    }

    private void ApplyDebugValue(AiDebugConfig config, string key, object value)
    {
        switch (key)
        {
            case "save_request_summary":
                config.SaveRequestSummary = this.GetBool(value, config.SaveRequestSummary);
                break;
            case "save_response_summary":
                config.SaveResponseSummary = this.GetBool(value, config.SaveResponseSummary);
                break;
            case "save_tool_calls":
                config.SaveToolCalls = this.GetBool(value, config.SaveToolCalls);
                break;
            case "save_patch_diff":
                config.SavePatchDiff = this.GetBool(value, config.SavePatchDiff);
                break;
            case "save_embedding_hits":
                config.SaveEmbeddingHits = this.GetBool(value, config.SaveEmbeddingHits);
                break;
        }
    }

    private void ApplyBroadcastValue(AiBroadcastConfig config, string key, object value)
    {
        switch (key)
        {
            case "max_hops":
                config.MaxHops = this.GetInt(value, config.MaxHops);
                break;
        }
    }

    private void ApplyPerceptionValue(AiPerceptionConfig config, string key, object value)
    {
        switch (key)
        {
            case "npc_radius_tiles":
                config.NpcRadiusTiles = this.GetInt(value, config.NpcRadiusTiles);
                break;
        }
    }

    private void ApplyProviderValue(AiProviderConfig config, string key, object value)
    {
        switch (key)
        {
            case "kind":
                config.Kind = this.GetString(value).ToLowerInvariant();
                break;
            case "base_url":
                config.BaseUrl = this.GetString(value);
                break;
            case "api_key":
                config.ApiKey = this.GetString(value);
                break;
            case "model":
                config.Model = this.GetString(value);
                break;
            case "max_tokens":
                config.MaxTokens = this.GetInt(value, config.MaxTokens);
                break;
            case "thinking_enabled":
                config.ThinkingEnabled = this.GetBool(value, config.ThinkingEnabled);
                break;
            case "temperature":
                config.Temperature = this.GetFloat(value, config.Temperature);
                break;
            case "tool_calling_required":
                config.ToolCallingRequired = this.GetBool(value, config.ToolCallingRequired);
                break;
            case "timeout_seconds":
                config.TimeoutSeconds = this.GetInt(value, config.TimeoutSeconds ?? 0);
                break;
        }
    }

    private int GetInt(object value, int fallback)
    {
        return value switch
        {
            int result => result,
            float result => (int)result,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) => parsed,
            _ => fallback
        };
    }

    private float GetFloat(object value, float fallback)
    {
        return value switch
        {
            float result => result,
            int result => result,
            string text when float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) => parsed,
            _ => fallback
        };
    }

    private bool GetBool(object value, bool fallback)
    {
        return value switch
        {
            bool result => result,
            string text when bool.TryParse(text, out bool parsed) => parsed,
            _ => fallback
        };
    }

    private string GetString(object value)
    {
        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private string BuildDefaultToml()
    {
        return @"# NPC LLM Agent 全局配置
# 这里只放 URL、Token、模型等文本配置；游戏内菜单只做 NPC 级开关与时间段。

[router]
default_timeout_seconds = 45
max_retry_count = 1
tool_loop_max_rounds = 4
retry_backoff_milliseconds = 1500
enable_verbose_debug = true

[embeddings]
provider_name = ""embedding_default""

[debug]
save_request_summary = true
save_response_summary = true
save_tool_calls = true
save_patch_diff = true
save_embedding_hits = true

[perception]
npc_radius_tiles = 100

[broadcast]
max_hops = 5

[providers.embedding_default]
kind = ""openai""
base_url = ""https://api.openai.com/v1""
api_key = ""PUT_YOUR_TOKEN_HERE""
model = ""text-embedding-3-small""
max_tokens = 1536
thinking_enabled = false
temperature = 0.7
tool_calling_required = true
timeout_seconds = 45

[providers.openai_default]
kind = ""openai""
base_url = ""https://api.openai.com/v1""
api_key = ""PUT_YOUR_TOKEN_HERE""
model = ""gpt-4.1-mini""
max_tokens = 1536
thinking_enabled = false
temperature = 0.7
tool_calling_required = true
timeout_seconds = 45

[providers.anthropic_default]
kind = ""anthropic""
base_url = ""https://api.anthropic.com/v1""
api_key = ""PUT_YOUR_TOKEN_HERE""
model = ""claude-3-7-sonnet-latest""
max_tokens = 1536
thinking_enabled = false
temperature = 0.7
tool_calling_required = true
timeout_seconds = 45
";
    }
}
