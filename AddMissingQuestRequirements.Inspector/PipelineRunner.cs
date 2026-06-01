using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Attachment;
using AddMissingQuestRequirements.Pipeline.Database;
using AddMissingQuestRequirements.Pipeline.Override;
using AddMissingQuestRequirements.Pipeline.Quest;
using AddMissingQuestRequirements.Pipeline.Shared;
using AddMissingQuestRequirements.Pipeline.Weapon;
using AddMissingQuestRequirements.Reporting;
using AddMissingQuestRequirements.Util;

namespace AddMissingQuestRequirements.Inspector;

public static class PipelineRunner
{
    // Attachment categorization rules live in the core project — see
    // AddMissingQuestRequirements.Pipeline.Attachment.DefaultAttachmentRules.

    public static InspectorResult Run(LoadResult loaded, IModLogger logger)
    {
        var settings = loaded.Settings;
        var config = loaded.Config;
        var itemDb = loaded.ItemDb;

        // Fold any miscased override item IDs to the DB's canonical casing before
        // categorization (parity with the SPT loader).
        OverrideIdCanonicalizer.Normalize(settings, itemDb);

        // ── Categorize weapons ────────────────────────────────────────────────
        var weaponCategorizer = new WeaponCategorizer(
            DefaultWeaponRuleFactory.Build(itemDb, config.WeaponLikeAncestors));
        var categorization = weaponCategorizer.Categorize(itemDb, settings, config);

        // ── Categorize attachments ────────────────────────────────────────────
        var attachmentCategorizer = new AttachmentCategorizer(DefaultAttachmentRules.Rules);
        var attachmentCategorization = attachmentCategorizer.Categorize(itemDb, settings);

        // ── Snapshot weapon arrays before patching ────────────────────────────
        var prePatch = new Dictionary<ConditionNode, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var quest in loaded.QuestDb.Quests.Values)
        {
            foreach (var condition in quest.Conditions)
            {
                prePatch[condition] = [..condition.Weapon];
            }
        }

        var prePatchMods = new Dictionary<ConditionNode, (List<List<string>> Incl, List<List<string>> Excl)>(
            ReferenceEqualityComparer.Instance);
        foreach (var quest in loaded.QuestDb.Quests.Values)
        {
            foreach (var condition in quest.Conditions)
            {
                prePatchMods[condition] = (
                    condition.WeaponModsInclusive.Select(g => g.ToList()).ToList(),
                    condition.WeaponModsExclusive.Select(g => g.ToList()).ToList()
                );
            }
        }

        // ── Patch quests ──────────────────────────────────────────────────────
        var nameResolver = new ItemDbNameResolver(itemDb);
        var weaponExpander = new WeaponArrayExpander(new TypeSelector(), nameResolver);
        var modsExpander = new WeaponModsExpander(attachmentCategorization, nameResolver);
        var patcher = new QuestPatcher([weaponExpander, modsExpander], nameResolver);
        patcher.Patch(loaded.QuestDb, settings, categorization, logger);

        // ── Build result ──────────────────────────────────────────────────────
        return ReportBuilder.Build(
            settings,
            config,
            itemDb,
            loaded.QuestDb,
            prePatch,
            prePatchMods,
            categorization,
            attachmentCategorization);
    }
}
