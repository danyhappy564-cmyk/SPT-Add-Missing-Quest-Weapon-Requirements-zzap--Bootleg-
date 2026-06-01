using System.Text.Json;
using AddMissingQuestRequirements.Models;

namespace AddMissingQuestRequirements.Pipeline.Weapon;

/// <summary>
/// Property/structural default weapon rules that are independent of the configured
/// WeaponLikeAncestors. Appended to <see cref="DefaultWeaponRuleFactory.Build"/>
/// output so both the SPT loader and the Inspector inherit them. Mirrors
/// <c>DefaultAttachmentRules</c>.
/// <para>
/// The BoltAction rule replaces the per-weapon manual overrides previously needed
/// for modded bolt-action snipers: any item under the <c>SniperRifle</c> node that
/// carries <c>_props.BoltAction == true</c> is tagged <c>BoltActionSniperRifle</c>.
/// The <c>hasAncestor: SniperRifle</c> guard is required because the BoltAction prop
/// is not exclusive to snipers — the TOZ-106 sawn-off is a bolt-action <i>shotgun</i>
/// and must NOT be tagged a sniper rifle. Both <c>hasAncestor</c> and <c>properties</c>
/// are core condition keys, so the rule stacks on items that carry a manual type
/// override (it produces the same type, no conflict).
/// </para>
/// </summary>
public static class DefaultWeaponRules
{
    public static IReadOnlyList<TypeRule> Rules { get; } =
    [
        new TypeRule
        {
            Comment = "Auto-tag bolt-action sniper rifles (under SniperRifle, BoltAction=true) as BoltActionSniperRifle.",
            Conditions = new Dictionary<string, JsonElement>
            {
                ["hasAncestor"] = JsonSerializer.SerializeToElement("SniperRifle"),
                ["properties"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, bool> { ["BoltAction"] = true }),
            },
            Type = "BoltActionSniperRifle",
        },
    ];
}
