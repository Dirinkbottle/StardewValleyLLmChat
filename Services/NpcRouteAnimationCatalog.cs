using StardewValley;

namespace StardewMod.Services;

/// <summary>
/// 受控的原版 NPC 路由动画目录。
/// </summary>
internal static class NpcRouteAnimationCatalog
{
    private static readonly Dictionary<string, string> AliasMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["surprised"] = "surprised",
        ["surprise"] = "surprised",
        ["shocked"] = "surprised",
        ["shock"] = "surprised",
        ["startled"] = "surprised",
        ["sleep"] = "sleep",
        ["beach_change"] = "change_beach",
        ["normal_change"] = "change_normal"
    };

    public static IReadOnlyList<string> GetControlledNames()
    {
        SortedSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> formalKeys = GetFormalAnimationKeys();
        foreach (string key in formalKeys)
        {
            names.Add(key);
        }

        foreach ((string alias, string target) in AliasMap)
        {
            if (formalKeys.Contains(target))
            {
                names.Add(alias);
            }
        }

        return names.ToList();
    }

    public static bool TryResolve(string? animationName, out string resolvedAnimationName)
    {
        resolvedAnimationName = string.Empty;
        string normalized = (animationName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        HashSet<string> keys = GetFormalAnimationKeys();
        if (keys.Contains(normalized))
        {
            resolvedAnimationName = normalized;
            return true;
        }

        if (AliasMap.TryGetValue(normalized, out string? aliasTarget) && keys.Contains(aliasTarget))
        {
            resolvedAnimationName = aliasTarget;
            return true;
        }

        return false;
    }

    private static HashSet<string> GetFormalAnimationKeys()
    {
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase)
        {
            "change_beach",
            "change_normal"
        };

        try
        {
            foreach (string key in DataLoader.AnimationDescriptions(Game1.content).Keys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key.Trim().ToLowerInvariant());
                }
            }
        }
        catch
        {
        }

        return keys;
    }
}
