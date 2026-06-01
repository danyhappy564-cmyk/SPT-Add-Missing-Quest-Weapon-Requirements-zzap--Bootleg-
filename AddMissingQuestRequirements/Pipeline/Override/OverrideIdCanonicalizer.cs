using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Database;

namespace AddMissingQuestRequirements.Pipeline.Override;

/// <summary>
/// Normalizes every item-ID-bearing field in an <see cref="OverriddenSettings"/>
/// to the item database's canonical casing, in place. Authors may type item IDs in
/// any case in *Overrides.jsonc; SPT template IDs are case-sensitive both for
/// internal lookups and for IDs written back into quests.json, so a miscased ID is
/// folded to the DB's exact casing before the pipeline runs.
/// <para>
/// Type-name entries (e.g. "BoltActionSniperRifle") and IDs unknown to the DB are
/// left unchanged — only entries matching a known item ID case-insensitively are
/// rewritten.
/// </para>
/// </summary>
public static class OverrideIdCanonicalizer
{
    public static void Normalize(OverriddenSettings settings, IItemDatabase db)
    {
        var canon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in db.Items.Keys)
        {
            // First-seen wins on a case-insensitive collision (not expected for SPT IDs).
            canon.TryAdd(id, id);
        }

        string Canon(string id)
        {
            return canon.TryGetValue(id, out var c) ? c : id;
        }

        CanonicalizeKeys(settings.ManualTypeOverrides, Canon);
        CanonicalizeKeys(settings.ManualAttachmentTypeOverrides, Canon);

        CanonicalizeAliasMap(settings.CanBeUsedAs, Canon);
        CanonicalizeAliasMap(settings.AttachmentCanBeUsedAs, Canon);

        foreach (var entries in settings.QuestOverrides.Values)
        {
            foreach (var entry in entries)
            {
                CanonicalizeList(entry.IncludedWeapons, Canon);
                CanonicalizeList(entry.ExcludedWeapons, Canon);
                CanonicalizeList(entry.IncludedMods, Canon);
                CanonicalizeList(entry.ExcludedMods, Canon);

                foreach (var bundle in entry.IncludedModBundles)
                {
                    CanonicalizeList(bundle, Canon);
                }

                foreach (var bundle in entry.ExcludedModBundles)
                {
                    CanonicalizeList(bundle, Canon);
                }
            }
        }
    }

    // Rewrite dict KEYS through canon, preserving values. Last write wins on collision.
    private static void CanonicalizeKeys(Dictionary<string, string> map, Func<string, string> canon)
    {
        if (map.Count == 0)
        {
            return;
        }

        var snapshot = map.ToList();
        map.Clear();
        foreach (var (key, value) in snapshot)
        {
            map[canon(key)] = value;
        }
    }

    // Rewrite both KEYS and SET MEMBERS through canon. Union sets on key collision.
    private static void CanonicalizeAliasMap(Dictionary<string, HashSet<string>> map, Func<string, string> canon)
    {
        if (map.Count == 0)
        {
            return;
        }

        var snapshot = map.ToList();
        map.Clear();
        foreach (var (key, members) in snapshot)
        {
            var canonMembers = new HashSet<string>(members.Select(canon));
            var canonKey = canon(key);
            if (map.TryGetValue(canonKey, out var existing))
            {
                existing.UnionWith(canonMembers);
            }
            else
            {
                map[canonKey] = canonMembers;
            }
        }
    }

    // Rewrite list ENTRIES in place.
    private static void CanonicalizeList(List<string> list, Func<string, string> canon)
    {
        for (var i = 0; i < list.Count; i++)
        {
            list[i] = canon(list[i]);
        }
    }
}
