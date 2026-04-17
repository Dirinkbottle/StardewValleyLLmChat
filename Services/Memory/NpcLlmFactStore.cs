using System.Text;
using System.Text.Json;
using StardewMod.Models;
using StardewModdingAPI;

namespace StardewMod.Services.Memory;

/// <summary>
/// 管理结构化事实记忆。
/// 事实层用于承载高优先级、可被覆盖更新的信息，避免仅靠事件检索时把旧语境和当天语境混淆。
/// </summary>
internal sealed class NpcLlmFactStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly IModHelper helper;
    private readonly NpcLlmConsoleLogger logger;
    private readonly object fileLock = new();

    public NpcLlmFactStore(IModHelper helper, NpcLlmConsoleLogger logger)
    {
        this.helper = helper;
        this.logger = logger;
    }

    public List<MemoryFactRecord> GetActiveFacts(string npcName, string gameDate)
    {
        List<MemoryFactRecord> allFacts = this.LoadFacts(npcName);
        List<MemoryFactRecord> prunedFacts = this.PruneExpiredFacts(allFacts, gameDate);
        if (prunedFacts.Count != allFacts.Count)
        {
            this.SaveFacts(npcName, prunedFacts);
            allFacts = prunedFacts;
        }

        return allFacts
            .Where(fact => NpcMemoryFactScopes.IsActiveForGameDate(fact, gameDate))
            .OrderBy(fact => string.Equals(NpcMemoryFactScopes.Normalize(fact.Scope), NpcMemoryFactScopes.Today, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(fact => fact.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(fact => fact.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public MemoryFactRecord UpsertFact(string npcName, string gameDate, string sourceEventType, MemoryFactUpdate update)
    {
        List<MemoryFactRecord> facts = this.PruneExpiredFacts(this.LoadFacts(npcName), gameDate);
        string normalizedScope = NpcMemoryFactScopes.Normalize(update.Scope);
        string normalizedKey = update.Key.Trim();

        MemoryFactRecord fact = facts.FirstOrDefault(existing =>
                string.Equals(existing.Key, normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NpcMemoryFactScopes.Normalize(existing.Scope), normalizedScope, StringComparison.OrdinalIgnoreCase))
            ?? new MemoryFactRecord
            {
                NpcName = npcName,
                Key = normalizedKey,
                Scope = normalizedScope
            };

        fact.NpcName = npcName;
        fact.Key = normalizedKey;
        fact.Scope = normalizedScope;
        fact.Category = update.Category.Trim();
        fact.Summary = update.Summary.Trim();
        fact.Value = update.Value.Trim();
        fact.SourceEventType = sourceEventType;
        fact.GameDate = gameDate;
        fact.UpdatedAt = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(update.Reason))
        {
            fact.Metadata["reason"] = update.Reason.Trim();
        }

        int existingIndex = facts.FindIndex(existing => string.Equals(existing.Id, fact.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            facts[existingIndex] = fact;
        }
        else
        {
            facts.Add(fact);
        }

        this.SaveFacts(npcName, facts);
        this.logger.Info("Memory", $"写入结构化事实 key={fact.Key} scope={fact.Scope}", npcName);
        return fact;
    }

    public bool RemoveFact(string npcName, string key, string scope, string gameDate, out MemoryFactRecord? removed)
    {
        List<MemoryFactRecord> facts = this.PruneExpiredFacts(this.LoadFacts(npcName), gameDate);
        string normalizedScope = NpcMemoryFactScopes.Normalize(scope);
        int index = facts.FindIndex(fact =>
            string.Equals(fact.Key, key, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NpcMemoryFactScopes.Normalize(fact.Scope), normalizedScope, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            removed = null;
            return false;
        }

        removed = facts[index];
        facts.RemoveAt(index);
        this.SaveFacts(npcName, facts);
        this.logger.Info("Memory", $"删除结构化事实 key={removed.Key} scope={removed.Scope}", npcName);
        return true;
    }

    private List<MemoryFactRecord> LoadFacts(string npcName)
    {
        string path = this.GetFactsPath(npcName);
        lock (this.fileLock)
        {
            if (!File.Exists(path))
            {
                return new List<MemoryFactRecord>();
            }

            try
            {
                return JsonSerializer.Deserialize<List<MemoryFactRecord>>(File.ReadAllText(path, Encoding.UTF8))
                    ?? new List<MemoryFactRecord>();
            }
            catch
            {
                return new List<MemoryFactRecord>();
            }
        }
    }

    private void SaveFacts(string npcName, List<MemoryFactRecord> facts)
    {
        string path = this.GetFactsPath(npcName);
        lock (this.fileLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(facts, JsonOptions), Encoding.UTF8);
        }
    }

    private List<MemoryFactRecord> PruneExpiredFacts(List<MemoryFactRecord> facts, string gameDate)
    {
        return facts
            .Where(fact =>
            {
                string normalizedScope = NpcMemoryFactScopes.Normalize(fact.Scope);
                return normalizedScope == NpcMemoryFactScopes.Persistent
                    || string.Equals(fact.GameDate, gameDate, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();
    }

    private string GetFactsPath(string npcName)
    {
        string saveName = Constants.SaveFolderName ?? "NoSave";
        return Path.Combine(this.helper.DirectoryPath, "NpcMemories", saveName, npcName, "facts.json");
    }
}
