using System.Text.Json;
using StardewMod.Models;
using StardewMod.Services.Memory;

namespace StardewMod.Services;

internal sealed partial class NpcLlmToolService
{
    private List<object> GetRecentMemories(NpcToolExecutionContext context, JsonElement args)
    {
        int limit = Math.Clamp(ReadInt(args, "limit", 5), 1, 20);
        string[] eventTypes = ReadStringArray(args, "event_types");
        List<MemoryRecord> memories = context.MemoryStore.GetRecentMemories(context.NpcName, limit, eventTypes);
        context.RecordMemoryHits(memories.Select(memory => memory.Id));
        return memories.Select(memory => new
        {
            id = memory.Id,
            event_type = memory.EventType,
            text = memory.Text,
            timestamp = memory.Timestamp,
            metadata = memory.Metadata
        }).Cast<object>().ToList();
    }

    private async Task<List<object>> SearchMemoriesAsync(NpcToolExecutionContext context, JsonElement args, CancellationToken cancellationToken)
    {
        string query = ReadString(args, "query");
        int topK = Math.Clamp(ReadInt(args, "top_k", 5), 1, 10);
        string[] eventTypes = ReadStringArray(args, "event_types");
        List<MemoryRecord> memories = await context.MemoryStore.SearchMemoriesAsync(context.NpcName, query, topK, eventTypes, cancellationToken);
        context.RecordMemoryHits(memories.Select(memory => memory.Id));
        return memories.Select(memory => new
        {
            id = memory.Id,
            event_type = memory.EventType,
            text = memory.Text,
            timestamp = memory.Timestamp,
            metadata = memory.Metadata,
            similarity = Math.Round(memory.Similarity, 4)
        }).Cast<object>().ToList();
    }

    private string UpdateMemoryFact(NpcToolExecutionContext context, JsonElement args)
    {
        string operation = ReadString(args, "operation", "upsert").Trim().ToLowerInvariant();
        string key = ReadString(args, "key").Trim();
        string scope = NpcMemoryFactScopes.Normalize(ReadString(args, "scope", NpcMemoryFactScopes.Persistent));
        if (string.IsNullOrWhiteSpace(key))
        {
            return Serialize(new { ok = false, error = "memory_update 缺少 key。" });
        }

        if (string.Equals(operation, "remove", StringComparison.OrdinalIgnoreCase))
        {
            bool removed = this.factStore.RemoveFact(context.NpcName, key, scope, context.Snapshot.GameDate, out MemoryFactRecord? removedFact);
            context.ActiveFacts = this.factStore.GetActiveFacts(context.NpcName, context.Snapshot.GameDate);
            return Serialize(new
            {
                ok = true,
                removed,
                fact = removedFact is null ? null : DescribeFact(removedFact),
                active_facts = context.ActiveFacts.Select(DescribeFact).ToList()
            });
        }

        string summary = ReadString(args, "summary").Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Serialize(new { ok = false, error = "memory_update 在 upsert 时必须提供 summary。" });
        }

        MemoryFactRecord fact = this.factStore.UpsertFact(
            context.NpcName,
            context.Snapshot.GameDate,
            context.TriggerEvent.EventType,
            new MemoryFactUpdate
            {
                Key = key,
                Scope = scope,
                Category = ReadString(args, "category").Trim(),
                Summary = summary,
                Value = ReadString(args, "value").Trim(),
                Reason = ReadString(args, "reason").Trim()
            });

        context.ActiveFacts = this.factStore.GetActiveFacts(context.NpcName, context.Snapshot.GameDate);
        return Serialize(new
        {
            ok = true,
            fact = DescribeFact(fact),
            active_facts = context.ActiveFacts.Select(DescribeFact).ToList()
        });
    }

    private static object DescribeFact(MemoryFactRecord fact)
    {
        return new
        {
            id = fact.Id,
            key = fact.Key,
            scope = NpcMemoryFactScopes.Normalize(fact.Scope),
            category = fact.Category,
            summary = fact.Summary,
            value = fact.Value,
            source_event_type = fact.SourceEventType,
            game_date = fact.GameDate,
            updated_at = fact.UpdatedAt,
            metadata = fact.Metadata
        };
    }
}
