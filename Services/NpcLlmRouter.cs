using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StardewMod.Models;
using StardewModdingAPI;

namespace StardewMod.Services;

/// <summary>
/// 统一管理 provider 选择、重试、tool loop 和 embedding 请求。
/// </summary>
internal sealed class NpcLlmRouter
{
    private readonly NpcLlmConfigService configService;
    private readonly IMonitor monitor;
    private readonly NpcLlmConsoleLogger logger;
    private readonly HttpClient httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public NpcLlmRouter(NpcLlmConfigService configService, IMonitor monitor, NpcLlmConsoleLogger logger)
    {
        this.configService = configService;
        this.monitor = monitor;
        this.logger = logger;
    }

    public IReadOnlyList<string> GetUsableProviderNames()
    {
        return this.configService.Current.Providers
            .Where(pair => pair.Value.IsUsableForChat())
            .Select(pair => pair.Key)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AiToolLoopResult> RunToolLoopAsync(
        string providerName,
        Func<int, CancellationToken, Task<string>> systemPromptFactory,
        string userPrompt,
        IReadOnlyList<AiToolDefinition> tools,
        Func<AiToolInvocation, CancellationToken, Task<string>> toolHandler,
        CancellationToken cancellationToken)
    {
        IAiProviderClient client = this.CreateClient(providerName);
        List<AiConversationEntry> history = new()
        {
            new AiConversationEntry
            {
                Role = "user",
                Text = userPrompt
            }
        };

        AiToolLoopResult result = new();
        int maxRounds = Math.Max(1, this.configService.Current.Router.ToolLoopMaxRounds);
        this.logger.Info("Router", $"开始 tool loop，请求将发送到 provider={providerName}，tools={tools.Count}，max_rounds={maxRounds}。", providerName);
        for (int round = 0; round < maxRounds; round++)
        {
            int promptRound = round + 1;
            string systemPrompt = await systemPromptFactory(promptRound, cancellationToken);
            this.logger.Debug("Router", $"进入第 {promptRound} 轮对话，history_entries={history.Count}。", providerName);
            this.logger.Debug("Router", $"round={promptRound} system_prompt_chars={systemPrompt.Length}, user_prompt_chars={userPrompt.Length}", providerName);
            AiConversationTurnResult turn = await this.SendWithRetryAsync(
                providerName,
                client,
                systemPrompt,
                history,
                tools,
                cancellationToken);

            result.LastAssistantText = turn.AssistantText;
            if (turn.ToolCalls.Count == 0)
            {
                this.logger.Info("Router", $"模型结束 tool loop，assistant_chars={turn.AssistantText.Length}。", providerName);
                return result;
            }

            this.logger.Info("Router", $"模型返回 {turn.ToolCalls.Count} 个工具调用。", providerName);

            history.Add(new AiConversationEntry
            {
                Role = "assistant",
                Text = turn.AssistantText,
                ToolCalls = turn.ToolCalls.ToList()
            });

            foreach (AiToolInvocation invocation in turn.ToolCalls)
            {
                result.ToolCalls.Add($"{invocation.Name}#{invocation.Id}");
            }

            IReadOnlyDictionary<string, AiToolDefinition> toolDefinitionMap = tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
            for (int invocationIndex = 0; invocationIndex < turn.ToolCalls.Count;)
            {
                AiToolInvocation currentInvocation = turn.ToolCalls[invocationIndex];
                if (!toolDefinitionMap.TryGetValue(currentInvocation.Name, out AiToolDefinition? currentDefinition) ||
                    !currentDefinition.AllowLocalParallelExecution)
                {
                    ToolExecutionResult executionResult = await this.ExecuteSingleToolInvocationAsync(
                        providerName,
                        currentInvocation,
                        toolHandler,
                        cancellationToken);
                    history.Add(executionResult.HistoryEntry);
                    invocationIndex++;
                    continue;
                }

                List<AiToolInvocation> parallelInvocations = new();
                while (invocationIndex < turn.ToolCalls.Count &&
                       toolDefinitionMap.TryGetValue(turn.ToolCalls[invocationIndex].Name, out AiToolDefinition? parallelDefinition) &&
                       parallelDefinition.AllowLocalParallelExecution)
                {
                    parallelInvocations.Add(turn.ToolCalls[invocationIndex]);
                    invocationIndex++;
                }

                if (parallelInvocations.Count == 1)
                {
                    ToolExecutionResult singleParallelResult = await this.ExecuteSingleToolInvocationAsync(
                        providerName,
                        parallelInvocations[0],
                        toolHandler,
                        cancellationToken);
                    history.Add(singleParallelResult.HistoryEntry);
                    continue;
                }

                this.logger.Info(
                    "Router",
                    $"本轮并发执行 {parallelInvocations.Count} 个并行安全工具：{string.Join(", ", parallelInvocations.Select(invocation => invocation.Name))}",
                    providerName);
                ToolExecutionResult[] parallelResults = await Task.WhenAll(parallelInvocations.Select(invocation =>
                    this.ExecuteSingleToolInvocationAsync(providerName, invocation, toolHandler, cancellationToken)));
                foreach (ToolExecutionResult parallelResult in parallelResults)
                {
                    history.Add(parallelResult.HistoryEntry);
                }
            }
        }

        this.logger.Warn("Router", $"tool loop 超过 {maxRounds} 轮上限，返回最后一轮结果。", providerName);
        return result;
    }

    private async Task<ToolExecutionResult> ExecuteSingleToolInvocationAsync(
        string providerName,
        AiToolInvocation invocation,
        Func<AiToolInvocation, CancellationToken, Task<string>> toolHandler,
        CancellationToken cancellationToken)
    {
        this.logger.Debug("Router", $"执行工具 {invocation.Name}#{invocation.Id}，args={this.logger.Summarize(invocation.ArgumentsJson, 320)}", providerName);
        string toolResult = await toolHandler(invocation, cancellationToken);
        this.logger.Debug("Router", $"工具 {invocation.Name}#{invocation.Id} 返回={this.logger.Summarize(toolResult, 320)}", providerName);
        return new ToolExecutionResult(new AiConversationEntry
        {
            Role = "tool",
            ToolCallId = invocation.Id,
            ToolName = invocation.Name,
            Text = toolResult
        });
    }

    public async Task<float[]> CreateEmbeddingAsync(string providerName, string input, CancellationToken cancellationToken)
    {
        if (!this.configService.Current.Providers.TryGetValue(providerName, out AiProviderConfig? providerConfig))
        {
            throw new InvalidOperationException($"未找到 embedding provider：{providerName}");
        }

        string model = providerConfig.Model;
        int effectiveTimeout = providerConfig.TimeoutSeconds ?? this.configService.Current.Router.DefaultTimeoutSeconds;

        this.logger.Debug(
            "Embedding",
            $"请求 embedding，provider={providerName} model={model} chars={input.Length} timeout={effectiveTimeout}s",
            providerName);
        IAiProviderClient client = this.CreateClient(providerName);
        float[] vector = await this.SendWithRetryAsync(
            providerName,
            (innerToken) => client.CreateEmbeddingAsync(model, input, innerToken),
            providerConfig.TimeoutSeconds,
            cancellationToken);
        this.logger.Debug("Embedding", $"embedding 返回维度={vector.Length}", providerName);
        return vector;
    }

    private async Task<AiConversationTurnResult> SendWithRetryAsync(
        string providerName,
        IAiProviderClient client,
        string systemPrompt,
        IReadOnlyList<AiConversationEntry> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        return await this.SendWithRetryAsync(
            providerName,
            (innerToken) => client.SendConversationAsync(systemPrompt, history, tools, innerToken),
            null,
            cancellationToken);
    }

    private async Task<T> SendWithRetryAsync<T>(
        string providerName,
        Func<CancellationToken, Task<T>> action,
        int? timeoutSecondsOverride,
        CancellationToken cancellationToken)
    {
        if (!this.configService.Current.Providers.TryGetValue(providerName, out AiProviderConfig? provider))
        {
            throw new InvalidOperationException($"未找到 provider：{providerName}");
        }

        int attempts = Math.Max(1, this.configService.Current.Router.MaxRetryCount + 1);
        Exception? lastError = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int timeoutSeconds = timeoutSecondsOverride ?? provider.TimeoutSeconds ?? this.configService.Current.Router.DefaultTimeoutSeconds;
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
            try
            {
                this.logger.Debug("Router", $"provider={providerName} 第 {attempt}/{attempts} 次请求，timeout={timeoutSeconds}s", providerName);
                return await action(timeoutCts.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = new TimeoutException($"provider {providerName} 请求超时。", ex);
                if (attempt < attempts)
                {
                    this.logger.Warn("Router", $"第 {attempt} 次请求超时，准备重试。", providerName);
                }
                else
                {
                    this.logger.Error("Router", $"第 {attempt} 次请求超时，达到重试上限。", providerName);
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < attempts)
                {
                    this.logger.Warn("Router", $"第 {attempt} 次请求失败：{ex.Message}，准备重试。", providerName);
                }
                else
                {
                    this.logger.Error("Router", $"第 {attempt} 次请求失败：{ex.Message}，达到重试上限。", providerName);
                }
            }

            if (attempt < attempts)
            {
                int backoff = Math.Max(100, this.configService.Current.Router.RetryBackoffMilliseconds);
                await Task.Delay(backoff * attempt, cancellationToken);
            }
        }

        this.logger.Error("Router", $"请求彻底失败：{lastError?.Message ?? "未知错误"}", providerName);
        throw lastError ?? new InvalidOperationException($"provider {providerName} 请求失败。");
    }

    private IAiProviderClient CreateClient(string providerName)
    {
        if (!this.configService.Current.Providers.TryGetValue(providerName, out AiProviderConfig? provider))
        {
            throw new InvalidOperationException($"未找到 provider：{providerName}");
        }

        return provider.Kind switch
        {
            "openai" => new OpenAiCompatibleClient(providerName, provider, this.httpClient, this.monitor, this.logger),
            "anthropic" => new AnthropicCompatibleClient(providerName, provider, this.httpClient, this.monitor, this.logger),
            _ => throw new InvalidOperationException($"不支持的 provider kind：{provider.Kind}")
        };
    }
}

internal sealed class AiToolLoopResult
{
    public string LastAssistantText { get; set; } = string.Empty;

    public List<string> ToolCalls { get; } = new();
}

internal readonly record struct ToolExecutionResult(AiConversationEntry HistoryEntry);

internal sealed class AiConversationEntry
{
    public string Role { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string ToolCallId { get; set; } = string.Empty;

    public string ToolName { get; set; } = string.Empty;

    public List<AiToolInvocation> ToolCalls { get; set; } = new();
}

internal sealed class AiToolDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public NpcToolKind ToolKind { get; init; } = NpcToolKind.Query;

    public NpcToolDispatchPolicy DispatchPolicy { get; init; } = NpcToolDispatchPolicy.None;

    public bool AllowLocalParallelExecution { get; init; }

    public string ParallelCallDescription { get; init; } = string.Empty;

    public bool SupportsNpcBroadcast { get; init; }

    public JsonElement InputSchema { get; init; }
}

internal sealed class AiToolInvocation
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name { get; init; } = string.Empty;

    public string ArgumentsJson { get; init; } = "{}";
}

internal sealed class AiConversationTurnResult
{
    public string AssistantText { get; set; } = string.Empty;

    public List<AiToolInvocation> ToolCalls { get; } = new();
}

internal interface IAiProviderClient
{
    Task<AiConversationTurnResult> SendConversationAsync(
        string systemPrompt,
        IReadOnlyList<AiConversationEntry> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken);

    Task<float[]> CreateEmbeddingAsync(string model, string input, CancellationToken cancellationToken);
}

internal sealed class OpenAiCompatibleClient : IAiProviderClient
{
    private readonly string providerName;
    private readonly AiProviderConfig provider;
    private readonly HttpClient httpClient;
    private readonly IMonitor monitor;
    private readonly NpcLlmConsoleLogger logger;

    public OpenAiCompatibleClient(string providerName, AiProviderConfig provider, HttpClient httpClient, IMonitor monitor, NpcLlmConsoleLogger logger)
    {
        this.providerName = providerName;
        this.provider = provider;
        this.httpClient = httpClient;
        this.monitor = monitor;
        this.logger = logger;
    }

    public async Task<AiConversationTurnResult> SendConversationAsync(
        string systemPrompt,
        IReadOnlyList<AiConversationEntry> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        List<object?> messages = new()
        {
            new Dictionary<string, object?>
            {
                ["role"] = "system",
                ["content"] = systemPrompt
            }
        };

        foreach (AiConversationEntry entry in history)
        {
            if (entry.Role == "tool")
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = entry.ToolCallId,
                    ["content"] = entry.Text
                });
            }
            else if (entry.Role == "assistant" && entry.ToolCalls.Count > 0)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text,
                    ["tool_calls"] = entry.ToolCalls.Select(toolCall => new Dictionary<string, object?>
                    {
                        ["id"] = toolCall.Id,
                        ["type"] = "function",
                        ["function"] = new Dictionary<string, object?>
                        {
                            ["name"] = toolCall.Name,
                            ["arguments"] = toolCall.ArgumentsJson
                        }
                    }).ToList()
                });
            }
            else
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = entry.Role,
                    ["content"] = entry.Text
                });
            }
        }

        Dictionary<string, object?> payload = new()
        {
            ["model"] = this.provider.Model,
            ["messages"] = messages,
            ["max_tokens"] = this.provider.MaxTokens,
            ["temperature"] = this.provider.Temperature,
            ["tools"] = tools.Select(tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = BuildProviderToolDescription(tool),
                    ["parameters"] = tool.InputSchema
                }
            }).ToList()
        };

        if (this.provider.ThinkingEnabled)
        {
            payload["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = "medium"
            };
        }

        JsonDocument document = await this.PostJsonAsync(this.CombineUrl("chat/completions"), payload, cancellationToken);
        JsonElement message = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");

        AiConversationTurnResult result = new()
        {
            AssistantText = ReadOpenAiContentText(message)
        };

        if (message.TryGetProperty("tool_calls", out JsonElement toolCallsElement) && toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement toolCall in toolCallsElement.EnumerateArray())
            {
                result.ToolCalls.Add(new AiToolInvocation
                {
                    Id = toolCall.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    Name = toolCall.GetProperty("function").GetProperty("name").GetString() ?? string.Empty,
                    ArgumentsJson = toolCall.GetProperty("function").GetProperty("arguments").GetString() ?? "{}"
                });
            }
        }

        return result;
    }

    public async Task<float[]> CreateEmbeddingAsync(string model, string input, CancellationToken cancellationToken)
    {
        Dictionary<string, object?> payload = new()
        {
            ["model"] = model,
            ["input"] = input
        };

        this.logger.DebugJson("Embedding", "payload", payload, this.providerName);

        JsonDocument document = await this.PostJsonAsync(this.CombineUrl("embeddings"), payload, cancellationToken);
        JsonElement vectorArray = document.RootElement.GetProperty("data")[0].GetProperty("embedding");
        return vectorArray.EnumerateArray().Select(item => item.GetSingle()).ToArray();
    }

    private async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.provider.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        this.logger.Debug("HTTP", $"POST {url}", this.providerName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.Headers.TryGetValues("x-siliconcloud-trace-id", out IEnumerable<string>? traceIds))
        {
            string traceId = traceIds.FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(traceId))
            {
                this.logger.Debug("HTTP", $"x-siliconcloud-trace-id={traceId}", this.providerName);
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            this.logger.Warn("HTTP", $"OpenAI-compatible 返回 {(int)response.StatusCode}，body={this.logger.Summarize(body, 420)}", this.providerName);
            this.monitor.Log($"OpenAI-compatible provider {this.providerName} 返回错误：{body}", LogLevel.Warn);
            throw new InvalidOperationException($"OpenAI-compatible provider {this.providerName} 调用失败：{response.StatusCode}");
        }

        this.logger.Debug("HTTP", $"OpenAI-compatible 响应成功，chars={body.Length}", this.providerName);
        return JsonDocument.Parse(body);
    }

    private string CombineUrl(string path)
    {
        string baseUrl = this.provider.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseUrl, path, StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return baseUrl + "/" + path;
    }

    private static string ReadOpenAiContentText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement contentElement))
        {
            return string.Empty;
        }

        return contentElement.ValueKind switch
        {
            JsonValueKind.String => contentElement.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join(
                "\n",
                contentElement.EnumerateArray()
                    .Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString())
                    .Where(text => !string.IsNullOrWhiteSpace(text))),
            _ => string.Empty
        };
    }

    internal static string BuildProviderToolDescription(AiToolDefinition tool)
    {
        if (string.IsNullOrWhiteSpace(tool.ParallelCallDescription))
        {
            return tool.Description;
        }

        return $"{tool.Description}\nparallel_call_description: {tool.ParallelCallDescription}";
    }
}

internal sealed class AnthropicCompatibleClient : IAiProviderClient
{
    private readonly string providerName;
    private readonly AiProviderConfig provider;
    private readonly HttpClient httpClient;
    private readonly IMonitor monitor;
    private readonly NpcLlmConsoleLogger logger;

    public AnthropicCompatibleClient(string providerName, AiProviderConfig provider, HttpClient httpClient, IMonitor monitor, NpcLlmConsoleLogger logger)
    {
        this.providerName = providerName;
        this.provider = provider;
        this.httpClient = httpClient;
        this.monitor = monitor;
        this.logger = logger;
    }

    public async Task<AiConversationTurnResult> SendConversationAsync(
        string systemPrompt,
        IReadOnlyList<AiConversationEntry> history,
        IReadOnlyList<AiToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        List<object?> messages = new();
        foreach (AiConversationEntry entry in history)
        {
            if (entry.Role == "tool")
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "user",
                    ["content"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["type"] = "tool_result",
                            ["tool_use_id"] = entry.ToolCallId,
                            ["content"] = entry.Text
                        }
                    }
                });
            }
            else if (entry.Role == "assistant" && entry.ToolCalls.Count > 0)
            {
                List<object?> content = new();
                if (!string.IsNullOrWhiteSpace(entry.Text))
                {
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = entry.Text
                    });
                }

                foreach (AiToolInvocation toolCall in entry.ToolCalls)
                {
                    content.Add(new Dictionary<string, object?>
                    {
                        ["type"] = "tool_use",
                        ["id"] = toolCall.Id,
                        ["name"] = toolCall.Name,
                        ["input"] = JsonDocument.Parse(toolCall.ArgumentsJson).RootElement.Clone()
                    });
                }

                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = content
                });
            }
            else
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = entry.Role,
                    ["content"] = entry.Text
                });
            }
        }

        Dictionary<string, object?> payload = new()
        {
            ["model"] = this.provider.Model,
            ["system"] = systemPrompt,
            ["messages"] = messages,
            ["max_tokens"] = this.provider.MaxTokens,
            ["temperature"] = this.provider.Temperature,
            ["tools"] = tools.Select(tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = OpenAiCompatibleClient.BuildProviderToolDescription(tool),
                ["input_schema"] = tool.InputSchema
            }).ToList()
        };

        if (this.provider.ThinkingEnabled)
        {
            payload["thinking"] = new Dictionary<string, object?>
            {
                ["type"] = "enabled",
                ["budget_tokens"] = Math.Min(1024, Math.Max(256, this.provider.MaxTokens / 2))
            };
        }

        JsonDocument document = await this.PostJsonAsync(this.CombineUrl("messages"), payload, cancellationToken);
        AiConversationTurnResult result = new();

        if (document.RootElement.TryGetProperty("content", out JsonElement contentElement) && contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in contentElement.EnumerateArray())
            {
                string type = item.GetProperty("type").GetString() ?? string.Empty;
                if (type == "text")
                {
                    string text = item.GetProperty("text").GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        result.AssistantText = string.IsNullOrWhiteSpace(result.AssistantText)
                            ? text
                            : result.AssistantText + "\n" + text;
                    }
                }
                else if (type == "tool_use")
                {
                    result.ToolCalls.Add(new AiToolInvocation
                    {
                        Id = item.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                        Name = item.GetProperty("name").GetString() ?? string.Empty,
                        ArgumentsJson = item.GetProperty("input").GetRawText()
                    });
                }
            }
        }

        return result;
    }

    public Task<float[]> CreateEmbeddingAsync(string model, string input, CancellationToken cancellationToken)
    {
        throw new NotSupportedException($"Anthropic-compatible provider {this.providerName} 不支持 embeddings。");
    }

    private async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", this.provider.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        this.logger.Debug("HTTP", $"POST {url}", this.providerName);
        using HttpResponseMessage response = await this.httpClient.SendAsync(request, cancellationToken);
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            this.logger.Warn("HTTP", $"Anthropic-compatible 返回 {(int)response.StatusCode}，body={this.logger.Summarize(body, 420)}", this.providerName);
            this.monitor.Log($"Anthropic-compatible provider {this.providerName} 返回错误：{body}", LogLevel.Warn);
            throw new InvalidOperationException($"Anthropic-compatible provider {this.providerName} 调用失败：{response.StatusCode}");
        }

        this.logger.Debug("HTTP", $"Anthropic-compatible 响应成功，chars={body.Length}", this.providerName);
        return JsonDocument.Parse(body);
    }

    private string CombineUrl(string path)
    {
        string baseUrl = this.provider.BaseUrl.Trim().TrimEnd('/');
        if (baseUrl.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(baseUrl, path, StringComparison.OrdinalIgnoreCase))
        {
            return baseUrl;
        }

        return baseUrl + "/" + path;
    }
}
