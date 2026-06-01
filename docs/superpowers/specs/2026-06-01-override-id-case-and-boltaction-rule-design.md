# Override ID case-insensitivity + BoltAction default rule

**Date:** 2026-06-01
**Status:** Approved (design)
**Version target:** `2.2.0` → `2.3.0` (minor: bugfix + additive feature)

## TL;DR

Two related fixes for modded bolt-action sniper rifles silently failing to join
quest expansions (e.g. The Tarkov Shooter series):

1. **Fix 3 — Override ID canonicalization.** Item IDs typed in any case in
   `*Overrides.jsonc` are normalized to the database's canonical case in one pass
   after override merge, so a miscased key/value matches the DB and is written
   back into quests with the correct case the game expects.
2. **Fix 4 — BoltAction default rule.** A code-level rule
   `{ properties: { BoltAction: true } } → BoltActionSniperRifle` auto-tags every
   bolt-action rifle in the DB, removing the need for per-weapon manual overrides.

No config-file changes ship. Both entry points (SPT loader + Inspector) inherit
both fixes through single choke points.

_~8 min read · 11 sections · ≈1,500 words_

## Problem

A user tagged the Longbow (Massivesoft) with
`"02002010AB10AB0000000000": "BoltActionSniperRifle,SniperRifle"` in
`manualTypeOverrides`. The weapon was categorized as `SniperRifle` only — never
`BoltActionSniperRifle` — so it was not added to bolt-action quests. KATT AMR
(Massivesoft) showed the same; M200 (WTT Armory) worked.

### Root cause (verified against `SptDbExporter/export/items.json`, 2026-06-01)

- Longbow's real template `_id` is **lowercase** `02002010ab10ab0000000000`.
  The override key was **uppercase** `02002010AB10AB0000000000`. Grep for the
  uppercase form in `items.json` → 0 hits.
- `OverrideReader` builds `manualTypeOverrides` as `new Dictionary<string,string>()`
  (`OverrideReader.cs:43`) — default ordinal, case-sensitive comparer. The lookup
  `manualOverrides.TryGetValue(item.Id, …)` (`CategorizationHelper.cs:140`) misses.
- With the override dead, `SniperRifle` comes from the default
  `{directChildOf:Weapon}` rule (Longbow ancestry: `Weapon → SniperRifle` node).
  No rule produces `BoltActionSniperRifle` for it.
- The user's `nameContains:"bolt-action"` workaround matched by locale name
  (case-insensitive), bypassing the broken ID lookup — confirming the ID, not the
  priority, was the problem. The user later lowercased the Longbow key by hand and
  it worked; KATT/M200 remained uppercase/absent and stayed broken.
- **Blast radius:** 20 of 241 bundled `manualTypeOverrides` keys are dead purely
  from uppercase-vs-lowercase mismatch (all the `020020…`/`022022…`/`023023…`
  Massivesoft pattern), spanning Smg / Shotgun / AssaultRifle / MarksmanRifle /
  BoltActionSniperRifle types.

Longbow, KATT AMR, and M200 share identical ancestry (`Weapon → SniperRifle`,
`_props.BoltAction == true`, no `BoltActionSniperRifle` ancestor node). The
existing `hasAncestor:"BoltActionSniperRifle"` rules never match them. 22 items in
the DB carry `_props.BoltAction == true`; `PumpAction` / `IsRevolver` props do not
exist.

## Goals

- A miscased item ID anywhere in `*Overrides.jsonc` behaves identically to the
  correct-case ID — both for internal lookups and for IDs written back into
  `quests.json`.
- Every bolt-action rifle (`_props.BoltAction == true`) is tagged
  `BoltActionSniperRifle` with no per-weapon config.
- Both the SPT loader and the Inspector inherit both fixes.

## Non-goals

- Pump-action / revolver auto-detection (no DB property to key on — out of scope).
- Quest-ID case-insensitivity (quest IDs are vanilla lowercase mongo IDs; not the
  failure mode; left case-sensitive).
- Removing the now-redundant bundled `BoltActionSniperRifle` manual overrides
  (harmless; separate cleanup chore).
- Changing `validateOverrideIds` scope (still opt-in; after Fix 3 it warns only on
  genuinely-unknown IDs).

## Fix 3 — Override ID canonicalization

### Why not a raw comparer swap

Switching the lookup dictionaries to `OrdinalIgnoreCase` fixes lookups but **not
write-back**. `includedWeapons` / `canBeUsedAs` IDs are appended verbatim into a
condition's `weapon` array (`WeaponArrayExpander.cs:188`, `AddResolved`). The game
matches template IDs by exact string, so a miscased ID written into a quest fails
in-game even if our pipeline "accepted" it. Canonicalization fixes both with one
mechanism in one place.

### Design

New `AddMissingQuestRequirements/Pipeline/Override/OverrideIdCanonicalizer.cs`:

```csharp
public static class OverrideIdCanonicalizer
{
    // Rewrites every item-ID-bearing field in `settings` to the DB's canonical
    // casing, in place. Type-name entries and IDs unknown to the DB pass through
    // unchanged.
    public static void Normalize(OverriddenSettings settings, IItemDatabase db);
}
```

Mechanism:

1. Build `canon`: a `Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)`
   mapping each `db.Items.Keys` entry (lookup is case-insensitive) → the actual
   key (canonical case). On a case-insensitive collision (two DB IDs differing
   only by case — not expected for SPT), first-seen wins.
2. `string Canon(string id) => canon.TryGetValue(id, out var c) ? c : id;`
3. Rewrite, mutating the existing mutable collections in place (clear +
   repopulate — the `OverriddenSettings` properties are `init`-only but the
   collection instances are mutable; no property reassignment needed):
   - `ManualTypeOverrides`, `ManualAttachmentTypeOverrides` — **keys only**
     (values are type names; never touched). On key collision after
     canonicalization, last write wins + `logger.Debug` notice.
   - `CanBeUsedAs`, `AttachmentCanBeUsedAs` — **keys and set members**.
   - For each `QuestOverrideEntry` in `QuestOverrides`: `IncludedWeapons`,
     `ExcludedWeapons`, `IncludedMods`, `ExcludedMods`, and each inner list of
     `IncludedModBundles` / `ExcludedModBundles`.

Type-name entries (e.g. `"BoltActionSniperRifle"`) never match a DB ID
case-insensitively, so they pass through. IDs unknown to the DB pass through and
remain subject to existing unknown-handling (`UnknownWeaponHandling`,
`validateOverrideIds`).

### Call sites

- **Loader:** `AddMissingQuestRequirementsLoader.cs`, immediately after
  `new OverrideReader(modDirs, logger).Read()` (line ~97) and before weapon
  categorization (line ~134). `itemDb` is in scope.
- **Inspector:** top of `PipelineRunner.Run` (`PipelineRunner.cs:~40`), after
  `loaded.Settings` / `loaded.ItemDb` are bound and before categorization. This
  path also serves the watch/serve mode, so reruns are covered.

No changes to `MergeHelper`, `WeaponArrayExpander`, `WeaponModsExpander`,
`CategorizationHelper`, or any dictionary comparer.

## Fix 4 — BoltAction default rule

### Design

New `AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRules.cs`:

```csharp
public static class DefaultWeaponRules
{
    // Property/structural default weapon rules that are independent of the
    // configured WeaponLikeAncestors. Mirrors DefaultAttachmentRules.
    public static IReadOnlyList<TypeRule> Rules { get; } =
    [
        new TypeRule
        {
            Conditions = new Dictionary<string, JsonElement>
            {
                // hasAncestor guard is required: BoltAction is NOT sniper-exclusive
                // (the TOZ-106 sawn-off is a bolt-action shotgun). Both keys are core,
                // so the rule still stacks on manually-overridden items.
                ["hasAncestor"] = JsonSerializer.SerializeToElement("SniperRifle"),
                ["properties"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, bool> { ["BoltAction"] = true }),
            },
            Type = "BoltActionSniperRifle",
        },
    ];
}
```

`DefaultWeaponRuleFactory.Build` also returns `DefaultWeaponRules.Rules` on the
`ancestors.Count == 0` path, so the property defaults are genuinely independent of
`WeaponLikeAncestors`.

`DefaultWeaponRuleFactory.Build` appends `DefaultWeaponRules.Rules` to its
generated per-ancestor rules — **single source**, so every caller inherits it.

`properties` is a core leaf key (`RuleCoreDetector.cs:22`), so this rule is a core
rule. It therefore fires even on manually-overridden items
(`CategorizationHelper.cs:166` only suppresses non-core, non-`applyToManualOverrides`
matches) — it stacks, producing the same `BoltActionSniperRifle` type, with no
conflict. `IncludeParentCategories` (default on) plus the existing
`BoltActionSniperRifle → SniperRifle` parent map (bundled `config.jsonc:7`) keep
`SniperRifle` present; even without that map, `SniperRifle` still comes from the
`{directChildOf:Weapon}` default rule.

### Inspector consolidation (targeted fix of pre-existing divergence)

`PipelineRunner.BuildDefaultWeaponRules` (`PipelineRunner.cs:21`) is a stale
duplicate of the factory that hardcodes `ancestor == "Weapon"` for the subtree
check instead of the factory's DB-driven `DetectSubtreeAncestors`. Delete it and
call `DefaultWeaponRuleFactory.Build(itemDb, config.WeaponLikeAncestors)`
(`itemDb` is in scope at `PipelineRunner.cs:40`). Output is identical for the
current `weaponLikeAncestors` (`Weapon` → `{directChildOf:Weapon}`;
`Knife`/`ThrowWeap`/`Launcher` → literal), so this is behavior-preserving for
shipped config while removing the divergence and delivering the new rule.

### No config changes

The bundled `BoltActionSniperRifle` entries in `manualTypeOverrides` become
redundant but harmless (core rule stacks; same type). Left in place.

## Files

| File | Change |
|------|--------|
| `Pipeline/Override/OverrideIdCanonicalizer.cs` | **new** — `Normalize` pass |
| `Pipeline/Weapon/DefaultWeaponRules.cs` | **new** — `Rules` (BoltAction rule) |
| `Pipeline/Weapon/DefaultWeaponRuleFactory.cs` | append `DefaultWeaponRules.Rules` in `Build` |
| `Spt/AddMissingQuestRequirementsLoader.cs` | call `OverrideIdCanonicalizer.Normalize` after read |
| `Inspector/PipelineRunner.cs` | call `Normalize`; drop local `BuildDefaultWeaponRules`, use factory |
| `Spt/ModMetadata.cs` | version `2.2.0` → `2.3.0` |

## Edge cases

- **Type-name vs ID in included/excluded lists:** disambiguated by DB membership —
  only case-insensitive DB-ID matches get canonicalized; type names pass through.
- **Unknown IDs:** preserved unchanged (late-loading-mod aliases stay valid);
  existing unknown-handling unchanged.
- **Canonicalization key collision** in `ManualTypeOverrides` (two miscased keys →
  same canonical ID): last write wins, `Debug`-logged.
- **`canBeUsedAs` unknown members:** preserved; `ExpandTransitiveGroups` still only
  promotes known IDs to graph keys (`CategorizationHelper.cs:94`).
- **BoltAction on a non-sniper** (the TOZ-106 sawn-off is a bolt-action *shotgun*,
  `_props.BoltAction == true` under the `Shotgun` node): the rule ANDs
  `hasAncestor: SniperRifle` with the property, so non-sniper bolt-actions are NOT
  tagged `BoltActionSniperRifle`. 21 of 22 DB bolt-actions are snipers; the TOZ-106
  is the lone exclusion and is correctly skipped.

## Testing

### Fix 3 — `OverrideIdCanonicalizerTests`
- Uppercase `manualTypeOverrides` key → rewritten to canonical lowercase DB ID.
- Uppercase `canBeUsedAs` key and member → both canonicalized.
- Uppercase entries in `includedWeapons` / `excludedWeapons` / `includedMods` /
  `excludedMods` / bundles → canonicalized.
- Type-name entry (`"BoltActionSniperRifle"`) → unchanged.
- Unknown ID → unchanged.
- Attachment maps (`ManualAttachmentTypeOverrides`, `AttachmentCanBeUsedAs`)
  canonicalized.
- Collision case → last wins, no throw.

### Fix 4 — `WeaponCategorizerTests` / `DefaultWeaponRuleFactoryTests`
- An item with `_props.BoltAction == true` and no override → types include
  `BoltActionSniperRifle`.
- Same item with a manual override of a different type → both the override type and
  `BoltActionSniperRifle` present (core rule stacks, not suppressed).
- `DefaultWeaponRuleFactory.Build` output contains the BoltAction rule.
- Inspector parity: factory output for `["Weapon","Knife","ThrowWeap","Launcher"]`
  matches the previous local-method output (regression guard for the consolidation).

### Integration (`Category=Integration`, real slice)
- KATT AMR (`020020ab50ab500000000000`) and M200 (`68fd4feab87d77a5aaf6bf64`) →
  `BoltActionSniperRifle` present after categorization (no override needed).
- `BoltActionSniperRifle` member count rises from baseline 3 → ~22.

## Verification (before/after)

Baseline backed up at
`K:\SPT4\SPT\user\mods\AddMissingQuestRequirements\_baseline-2026-06-01-pre-fix\`
(debug report HTML+JSON). Baseline anchors:

| Weapon | Baseline types |
|--------|----------------|
| Longbow | `BoltActionSniperRifle, SniperRifle, cal_762x39` (live workaround) |
| KATT AMR | `SniperRifle` |
| M200 | `SniperRifle` |
| `BoltActionSniperRifle` members | 3 |

Post-fix the rebuilt debug report must show KATT + M200 gaining
`BoltActionSniperRifle`, the member count rising to ~22, and Longbow still tagged
(now via the property rule — the nameContains workaround becomes removable).

## Risks

- **Behavior change for Inspector** from the factory consolidation — mitigated:
  identical output for current ancestors, guarded by a parity test.
- **Over-broad BoltAction tagging** — see edge cases; accepted, monitored via the
  before/after report diff.
- **Performance** — `Normalize` is one O(items + overrides) pass at startup;
  negligible.
