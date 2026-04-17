using System.Text;
using System.Text.Json;
using StardewMod.Models;
using StardewModdingAPI;
using StardewValley;

namespace StardewMod.Services;

/// <summary>
/// 负责 NPC 的本地记忆、向量索引和调试日志落盘。
/// </summary>
internal sealed class NpcLlmMemoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly NpcLlmConfigService configService;
    private readonly NpcLlmRouter router;
    private readonly NpcLlmConsoleLogger logger;
    private readonly object fileLock = new();

    public NpcLlmMemoryStore(IModHelper helper, IMonitor monitor, NpcLlmConfigService configService, NpcLlmRouter router, NpcLlmConsoleLogger logger)
    {
        this.helper = helper;
        this.monitor = monitor;
        this.configService = configService;
        this.router = router;
        this.logger = logger;
    }

    public Dictionary<string, string> GetOrCreateProfile(NPC npc)
    {
        Dictionary<string, string> profile = new(StringComparer.OrdinalIgnoreCase)
        {
            ["npc_name"] = npc.Name,
            ["display_name"] = npc.displayName,
            ["default_map"] = npc.DefaultMap,
            ["default_tile"] = $"{(int)npc.DefaultPosition.X / 64},{(int)npc.DefaultPosition.Y / 64}",
            ["default_facing"] = npc.DefaultFacingDirection.ToString(),
            ["birthday"] = $"{npc.Birthday_Season} {npc.Birthday_Day}",
            ["gender"] = npc.Gender.ToString(),
            ["is_married"] = npc.isMarried().ToString(),
            ["love_interest"] = npc.loveInterest ?? string.Empty,
            ["current_location"] = npc.currentLocation?.NameOrUniqueName ?? string.Empty,
            ["display_name_tokenized"] = npc.GetTokenizedDisplayName()
        };

        string path = Path.Combine(this.GetNpcFolder(npc.Name), "profile.json");
        lock (this.fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions), Encoding.UTF8);
        }

        this.logger.Debug("Memory", $"刷新 profile.json，字段数={profile.Count}", npc.Name);
        return profile;
    }

    public MemoryRecord AppendEventRecord(string npcName, string eventType, string text, Dictionary<string, string>? metadata = null)
    {
        Dictionary<string, string> normalizedMetadata = metadata is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);
        if (Context.IsWorldReady)
        {
            normalizedMetadata.TryAdd("game_date", this.BuildGameDateString());
            normalizedMetadata.TryAdd("time", Game1.timeOfDay.ToString());
            if (Game1.currentLocation is not null)
            {
                normalizedMetadata.TryAdd("location", Game1.currentLocation.NameOrUniqueName);
            }
        }

        MemoryRecord record = new()
        {
            NpcName = npcName,
            EventType = eventType,
            Text = text,
            Metadata = normalizedMetadata
        };

        string path = Path.Combine(this.GetNpcFolder(npcName), "events.jsonl");
        this.AppendJsonLine(path, record);
        this.logger.Debug("Memory", $"写入事件 event_type={eventType} id={record.Id} text={this.logger.Summarize(text)}", npcName);
        return record;
    }

    public DayMemoryRecord AppendDayRecord(string npcName, string gameDate, string scheduleKey, string summary)
    {
        DayMemoryRecord record = new()
        {
            NpcName = npcName,
            GameDate = gameDate,
            ScheduleKey = scheduleKey,
            Summary = summary
        };

        string path = Path.Combine(this.GetNpcFolder(npcName), "days.jsonl");
        this.AppendJsonLine(path, record);
        this.logger.Info("Memory", $"写入日终摘要 game_date={gameDate} schedule_key={scheduleKey}", npcName);
        return record;
    }

    public void AppendDebugRecord(string npcName, NpcAgentDebugRecord record)
    {
        string path = Path.Combine(this.GetNpcFolder(npcName), "debug.jsonl");
        this.AppendJsonLine(path, record);
        this.logger.Debug("Memory", $"写入 debug.jsonl trigger={record.Trigger}", npcName);
    }

    public List<MemoryRecord> GetRecentMemories(string npcName, int limit, IReadOnlyCollection<string>? eventTypes = null)
    {
        HashSet<string>? expandedEventTypes = ExpandEventTypes(eventTypes);
        return this.LoadEventRecords(npcName)
            .Where(record => expandedEventTypes is null || expandedEventTypes.Contains(record.EventType))
            .OrderByDescending(record => record.Timestamp)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    public List<MemoryRecord> GetMemoriesForGameDate(string npcName, string gameDate, int maxCount)
    {
        List<MemoryRecord> records = this.LoadEventRecords(npcName)
            .Where(record =>
                record.Metadata.TryGetValue("game_date", out string? recordGameDate) &&
                string.Equals(recordGameDate, gameDate, StringComparison.OrdinalIgnoreCase))
            .OrderBy(record => record.Timestamp)
            .ToList();

        int effectiveMaxCount = Math.Max(1, maxCount);
        if (records.Count <= effectiveMaxCount)
        {
            return records;
        }

        return records
            .Skip(records.Count - effectiveMaxCount)
            .ToList();
    }

    public async Task<List<MemoryRecord>> SearchMemoriesAsync(string npcName, string query, int topK, IReadOnlyCollection<string>? eventTypes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            this.logger.Debug("Memory", $"query 为空，回退到最近记忆 limit={topK}", npcName);
            return this.GetRecentMemories(npcName, topK, eventTypes);
        }

        AiEmbeddingConfig embeddings = this.configService.Current.Embeddings;
        if (!embeddings.IsConfigured())
        {
            this.logger.Warn("Memory", "embedding 未配置，无法执行语义检索。", npcName);
            return new List<MemoryRecord>();
        }

        float[] queryVector;
        try
        {
            queryVector = await this.router.CreateEmbeddingAsync(embeddings.ProviderName, query, cancellationToken);
        }
        catch (Exception ex)
        {
            this.logger.Warn("Memory", $"生成检索 embedding 失败：{ex.Message}", npcName);
            this.monitor.Log($"为 {npcName} 检索记忆时生成 embedding 失败：{ex.Message}", LogLevel.Warn);
            return new List<MemoryRecord>();
        }

        HashSet<string>? expandedEventTypes = ExpandEventTypes(eventTypes);
        Dictionary<string, float[]> vectors = this.LoadVectors(npcName);
        List<MemoryRecord> memories = this.LoadEventRecords(npcName)
            .Where(record => expandedEventTypes is null || expandedEventTypes.Contains(record.EventType))
            .Where(record => vectors.ContainsKey(record.Id))
            .ToList();

        foreach (MemoryRecord memory in memories)
        {
            memory.Similarity = ComputeCosineSimilarity(queryVector, vectors[memory.Id]);
        }

        List<MemoryRecord> results = memories
            .OrderByDescending(memory => memory.Similarity)
            .Take(Math.Max(1, topK))
            .ToList();
        this.logger.Info("Memory", $"语义检索完成 top_k={topK} 命中={results.Count} query={this.logger.Summarize(query, 120)}", npcName);
        this.logger.Debug("Memory", $"命中 ID={string.Join(", ", results.Select(memory => memory.Id))}", npcName);
        return results;
    }

    public async Task TryEmbedRecordAsync(string npcName, MemoryRecord record, CancellationToken cancellationToken)
    {
        AiEmbeddingConfig embeddings = this.configService.Current.Embeddings;
        if (!embeddings.IsConfigured())
        {
            this.logger.Debug("Memory", "embedding 未配置，跳过向量写入。", npcName);
            return;
        }

        try
        {
            float[] vector = await this.router.CreateEmbeddingAsync(embeddings.ProviderName, record.Text, cancellationToken);
            Dictionary<string, float[]> vectors = this.LoadVectors(npcName);
            vectors[record.Id] = vector;
            this.SaveVectors(npcName, vectors);
            this.logger.Debug("Memory", $"写入向量成功 id={record.Id} dims={vector.Length}", npcName);
        }
        catch (Exception ex)
        {
            this.logger.Warn("Memory", $"写入记忆向量失败：{ex.Message}", npcName);
            this.monitor.Log($"为 {npcName} 写入记忆向量失败：{ex.Message}", LogLevel.Warn);
        }
    }

    private List<MemoryRecord> LoadEventRecords(string npcName)
    {
        string path = Path.Combine(this.GetNpcFolder(npcName), "events.jsonl");
        lock (this.fileLock)
        {
            if (!File.Exists(path))
            {
                return new List<MemoryRecord>();
            }

            List<MemoryRecord> records = new();
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    MemoryRecord? record = JsonSerializer.Deserialize<MemoryRecord>(line);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch
                {
                }
            }

            return records;
        }
    }

    private Dictionary<string, float[]> LoadVectors(string npcName)
    {
        string path = Path.Combine(this.GetNpcFolder(npcName), "vectors.json");
        lock (this.fileLock)
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            }

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(path, Encoding.UTF8))
                    ?? new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private void SaveVectors(string npcName, Dictionary<string, float[]> vectors)
    {
        string path = Path.Combine(this.GetNpcFolder(npcName), "vectors.json");
        lock (this.fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(vectors, JsonOptions), Encoding.UTF8);
        }
    }

    private void AppendJsonLine<T>(string path, T record)
    {
        lock (this.fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(record) + Environment.NewLine, Encoding.UTF8);
        }
    }

    private string GetNpcFolder(string npcName)
    {
        string saveName = Constants.SaveFolderName ?? "NoSave";
        return Path.Combine(this.helper.DirectoryPath, "NpcMemories", saveName, npcName);
    }

    private string BuildGameDateString()
    {
        return $"Year {Game1.year} {Game1.currentSeason} {Game1.dayOfMonth}";
    }

    private static HashSet<string>? ExpandEventTypes(IReadOnlyCollection<string>? eventTypes)
    {
        if (eventTypes is null || eventTypes.Count == 0)
        {
            return null;
        }

        HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string eventType in eventTypes.Where(eventType => !string.IsNullOrWhiteSpace(eventType)))
        {
            expanded.Add(eventType);
            switch (eventType.Trim().ToLowerInvariant())
            {
                case "dialogue":
                case "conversation":
                    expanded.Add("player_prompt");
                    expanded.Add("npc_reply");
                    expanded.Add("npc_sync_encounter");
                    expanded.Add("npc_to_npc_speech");
                    expanded.Add("npc_sync_reply");
                    break;
                case "player_interaction":
                    expanded.Add("player_prompt");
                    expanded.Add("gift_received");
                    break;
                case "gift":
                    expanded.Add("gift_received");
                    break;
            }
        }

        return expanded;
    }

    private static float ComputeCosineSimilarity(float[] left, float[] right)
    {
        if (left.Length == 0 || right.Length == 0 || left.Length != right.Length)
        {
            return 0f;
        }

        double dot = 0d;
        double leftNorm = 0d;
        double rightNorm = 0d;
        for (int i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftNorm += left[i] * left[i];
            rightNorm += right[i] * right[i];
        }

        if (leftNorm <= 0d || rightNorm <= 0d)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm)));
    }
}
