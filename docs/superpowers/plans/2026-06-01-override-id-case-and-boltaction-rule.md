# Override ID case-insensitivity + BoltAction default rule — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make miscased override item-IDs work everywhere, and auto-tag every bolt-action rifle as `BoltActionSniperRifle` from code.

**Architecture:** One post-merge canonicalization pass folds every item-ID-bearing override field to the DB's canonical casing (fixes lookups AND write-back). One code-level property rule (`BoltAction:true → BoltActionSniperRifle`) is appended inside `DefaultWeaponRuleFactory.Build`, so both the SPT loader and the Inspector inherit it through single choke points. The Inspector's stale duplicate rule-builder is consolidated onto the factory.

**Tech Stack:** .NET 9 class library, xUnit + FluentAssertions, `System.Text.Json`.

**Spec:** `docs/superpowers/specs/2026-06-01-override-id-case-and-boltaction-rule-design.md`

---

### Task 1: OverrideIdCanonicalizer

**Goal:** New pure pass that rewrites every item-ID-bearing field in `OverriddenSettings` to the DB's canonical casing, in place.

**Files:**
- Create: `AddMissingQuestRequirements/Pipeline/Override/OverrideIdCanonicalizer.cs`
- Test: `AddMissingQuestRequirements.Tests/Pipeline/Override/OverrideIdCanonicalizerTests.cs`

**Acceptance Criteria:**
- [ ] Uppercase `ManualTypeOverrides` / `ManualAttachmentTypeOverrides` key → rewritten to canonical DB id; value (type names) untouched.
- [ ] `CanBeUsedAs` / `AttachmentCanBeUsedAs` keys AND set members canonicalized.
- [ ] `IncludedWeapons` / `ExcludedWeapons` / `IncludedMods` / `ExcludedMods` and bundle inner lists canonicalized.
- [ ] Type-name entry (`"BoltActionSniperRifle"`) and unknown id pass through unchanged.
- [ ] Key collision after canonicalization does not throw (last-write-wins for string maps; union for alias maps).

**Verify:** `dotnet test --filter "FullyQualifiedName~OverrideIdCanonicalizerTests"` → all pass.

**Steps:**

- [ ] **Step 1: Write the failing tests**

Create `AddMissingQuestRequirements.Tests/Pipeline/Override/OverrideIdCanonicalizerTests.cs`:

```csharp
using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Database;
using AddMissingQuestRequirements.Pipeline.Override;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Pipeline.Override;

public class OverrideIdCanonicalizerTests
{
    // DB with two canonical (lowercase) ids.
    private static InMemoryItemDatabase MakeDb() => new(
    [
        new ItemNode { Id = "02002010ab10ab0000000000", Name = "longbow", ParentId = null, NodeType = "Item", Props = [] },
        new ItemNode { Id = "020020ab50ab500000000000", Name = "katt",    ParentId = null, NodeType = "Item", Props = [] },
    ]);

    [Fact]
    public void Canonicalizes_manual_type_override_key_preserving_value()
    {
        var settings = new OverriddenSettings
        {
            ManualTypeOverrides = new() { ["02002010AB10AB0000000000"] = "BoltActionSniperRifle,SniperRifle" },
        };

        OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        settings.ManualTypeOverrides.Should().ContainKey("02002010ab10ab0000000000");
        settings.ManualTypeOverrides.Should().NotContainKey("02002010AB10AB0000000000");
        settings.ManualTypeOverrides["02002010ab10ab0000000000"].Should().Be("BoltActionSniperRifle,SniperRifle");
    }

    [Fact]
    public void Canonicalizes_can_be_used_as_keys_and_members()
    {
        var settings = new OverriddenSettings
        {
            CanBeUsedAs = new() { ["02002010AB10AB0000000000"] = ["020020AB50AB500000000000"] },
        };

        OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        settings.CanBeUsedAs.Should().ContainKey("02002010ab10ab0000000000");
        settings.CanBeUsedAs["02002010ab10ab0000000000"].Should().Contain("020020ab50ab500000000000");
    }

    [Fact]
    public void Canonicalizes_quest_override_id_lists_but_not_type_names()
    {
        var settings = new OverriddenSettings
        {
            QuestOverrides = new()
            {
                ["q1"] =
                [
                    new QuestOverrideEntry
                    {
                        Id = "q1",
                        IncludedWeapons = ["02002010AB10AB0000000000", "BoltActionSniperRifle"],
                        ExcludedWeapons = ["020020AB50AB500000000000"],
                        IncludedModBundles = [["02002010AB10AB0000000000"]],
                    },
                ],
            },
        };

        OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        var entry = settings.QuestOverrides["q1"][0];
        entry.IncludedWeapons.Should().BeEquivalentTo(["02002010ab10ab0000000000", "BoltActionSniperRifle"]);
        entry.ExcludedWeapons.Should().BeEquivalentTo(["020020ab50ab500000000000"]);
        entry.IncludedModBundles[0].Should().BeEquivalentTo(["02002010ab10ab0000000000"]);
    }

    [Fact]
    public void Unknown_id_passes_through_unchanged()
    {
        var settings = new OverriddenSettings
        {
            ManualTypeOverrides = new() { ["NOT_IN_DB_123"] = "Smg" },
        };

        OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        settings.ManualTypeOverrides.Should().ContainKey("NOT_IN_DB_123");
    }

    [Fact]
    public void Key_collision_after_canonicalization_does_not_throw()
    {
        var settings = new OverriddenSettings
        {
            ManualTypeOverrides = new()
            {
                ["02002010AB10AB0000000000"] = "Smg",
                ["02002010ab10ab0000000000"] = "Shotgun",
            },
        };

        var act = () => OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        act.Should().NotThrow();
        settings.ManualTypeOverrides.Should().ContainKey("02002010ab10ab0000000000");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OverrideIdCanonicalizerTests"`
Expected: FAIL — `OverrideIdCanonicalizer` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `AddMissingQuestRequirements/Pipeline/Override/OverrideIdCanonicalizer.cs`:

```csharp
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
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OverrideIdCanonicalizerTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add AddMissingQuestRequirements/Pipeline/Override/OverrideIdCanonicalizer.cs AddMissingQuestRequirements.Tests/Pipeline/Override/OverrideIdCanonicalizerTests.cs
git commit -m "feat(override): canonicalize override item IDs to DB casing"
```

---

### Task 2: DefaultWeaponRules + factory append

**Goal:** Add the code-level `BoltAction:true → BoltActionSniperRifle` rule and append it to every weapon rule set via the factory.

**Files:**
- Create: `AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRules.cs`
- Modify: `AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRuleFactory.cs` (append in `Build`)
- Test: `AddMissingQuestRequirements.Tests/Pipeline/Weapon/DefaultWeaponRuleFactoryTests.cs`

**Acceptance Criteria:**
- [ ] `DefaultWeaponRules.Rules` contains exactly one rule: `properties {BoltAction:true}` → `BoltActionSniperRifle`.
- [ ] `DefaultWeaponRuleFactory.Build(...)` output includes that rule for any non-empty ancestor list.
- [ ] An item with `_props.BoltAction == true` and no override is categorized as `BoltActionSniperRifle` using only factory-built rules.

**Verify:** `dotnet test --filter "FullyQualifiedName~DefaultWeaponRuleFactoryTests"` → all pass.

**Steps:**

- [ ] **Step 1: Write the failing tests**

Create `AddMissingQuestRequirements.Tests/Pipeline/Weapon/DefaultWeaponRuleFactoryTests.cs`:

```csharp
using System.Text.Json;
using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Database;
using AddMissingQuestRequirements.Pipeline.Weapon;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Pipeline.Weapon;

public class DefaultWeaponRuleFactoryTests
{
    // root → weapon → SniperRifle → boltgun (BoltAction=true)
    private static InMemoryItemDatabase MakeDb() => new(
    [
        new ItemNode { Id = "root",   Name = "Item",        ParentId = null,     NodeType = "Node" },
        new ItemNode { Id = "weapon", Name = "Weapon",      ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "snode",  Name = "SniperRifle", ParentId = "weapon", NodeType = "Node" },
        new ItemNode { Id = "boltgun", Name = "boltgun", ParentId = "snode", NodeType = "Item",
            Props = new() { ["BoltAction"] = JsonDocument.Parse("true").RootElement } },
    ],
    localeNames: new() { ["boltgun"] = "Modded bolt-action rifle" });

    [Fact]
    public void DefaultWeaponRules_contains_boltaction_rule()
    {
        DefaultWeaponRules.Rules.Should().ContainSingle();
        DefaultWeaponRules.Rules[0].Type.Should().Be("BoltActionSniperRifle");
        DefaultWeaponRules.Rules[0].Conditions.Should().ContainKey("properties");
    }

    [Fact]
    public void Build_output_includes_boltaction_rule()
    {
        var rules = DefaultWeaponRuleFactory.Build(MakeDb(), ["Weapon"]);
        rules.Should().Contain(r => r.Type == "BoltActionSniperRifle");
    }

    [Fact]
    public void BoltAction_item_is_tagged_without_any_override()
    {
        var rules = DefaultWeaponRuleFactory.Build(MakeDb(), ["Weapon"]);
        var result = new WeaponCategorizer(rules)
            .Categorize(MakeDb(), new OverriddenSettings(), new ModConfig());

        result.WeaponToType["boltgun"].Should().Contain("BoltActionSniperRifle");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DefaultWeaponRuleFactoryTests"`
Expected: FAIL — `DefaultWeaponRules` does not exist.

- [ ] **Step 3: Create `DefaultWeaponRules.cs`**

```csharp
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
/// for modded bolt-action snipers: every item carrying <c>_props.BoltAction == true</c>
/// is tagged <c>BoltActionSniperRifle</c>. Because <c>properties</c> is a core
/// condition key, this rule also fires on items that carry a manual type override
/// (it stacks, producing the same type).
/// </para>
/// </summary>
public static class DefaultWeaponRules
{
    public static IReadOnlyList<TypeRule> Rules { get; } =
    [
        new TypeRule
        {
            Comment = "Auto-tag every bolt-action rifle (BoltAction=true) as BoltActionSniperRifle.",
            Conditions = new Dictionary<string, JsonElement>
            {
                ["properties"] = JsonSerializer.SerializeToElement(
                    new Dictionary<string, bool> { ["BoltAction"] = true }),
            },
            Type = "BoltActionSniperRifle",
        },
    ];
}
```

- [ ] **Step 4: Append in `DefaultWeaponRuleFactory.Build`**

In `AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRuleFactory.cs`, change the end of `Build` from:

```csharp
            rules.Add(new TypeRule
            {
                Conditions = new Dictionary<string, JsonElement>
                {
                    ["hasAncestor"] = JsonSerializer.SerializeToElement(anc),
                },
                Type = type,
            });
        }

        return rules;
    }
```

to:

```csharp
            rules.Add(new TypeRule
            {
                Conditions = new Dictionary<string, JsonElement>
                {
                    ["hasAncestor"] = JsonSerializer.SerializeToElement(anc),
                },
                Type = type,
            });
        }

        // Property/structural defaults independent of ancestors (e.g. BoltAction).
        rules.AddRange(DefaultWeaponRules.Rules);

        return rules;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DefaultWeaponRuleFactoryTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Commit**

```bash
git add AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRules.cs AddMissingQuestRequirements/Pipeline/Weapon/DefaultWeaponRuleFactory.cs AddMissingQuestRequirements.Tests/Pipeline/Weapon/DefaultWeaponRuleFactoryTests.cs
git commit -m "feat(weapon): BoltAction property rule auto-tags bolt-action snipers"
```

---

### Task 3: Inspector consolidation onto the factory

**Goal:** Replace the Inspector's stale local rule-builder with `DefaultWeaponRuleFactory.Build`, so the Inspector inherits the BoltAction rule and the DB-driven subtree detection.

**Files:**
- Modify: `AddMissingQuestRequirements.Inspector/PipelineRunner.cs` (delete `BuildDefaultWeaponRules`; call factory)
- Test: `AddMissingQuestRequirements.Tests/Inspector/PipelineRunnerRulesTests.cs`

**Acceptance Criteria:**
- [ ] `PipelineRunner` no longer defines a local `BuildDefaultWeaponRules`.
- [ ] Weapon categorization in `PipelineRunner.Run` uses `DefaultWeaponRuleFactory.Build(itemDb, config.WeaponLikeAncestors)`.
- [ ] Parity guard: factory output for `["Weapon","Knife","ThrowWeap","Launcher"]` yields `{directChildOf:Weapon}` for Weapon and literal types for the flat ancestors (matching the old local behavior) plus the BoltAction rule.

**Verify:** `dotnet test --filter "FullyQualifiedName~PipelineRunnerRulesTests"` → pass; `dotnet build -c Release` → success.

**Steps:**

- [ ] **Step 1: Write the parity test**

Create `AddMissingQuestRequirements.Tests/Inspector/PipelineRunnerRulesTests.cs`:

```csharp
using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Database;
using AddMissingQuestRequirements.Pipeline.Weapon;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Inspector;

// Guards the consolidation in Task 3: the factory must reproduce the Inspector's
// previous local rule set for the shipped ancestors, plus the new BoltAction rule.
public class PipelineRunnerRulesTests
{
    // Weapon has a subtree (intermediate AssaultRifle node); the flat ancestors do not.
    private static InMemoryItemDatabase MakeDb() => new(
    [
        new ItemNode { Id = "root",   Name = "Item",         ParentId = null,     NodeType = "Node" },
        new ItemNode { Id = "weapon", Name = "Weapon",       ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "ar",     Name = "AssaultRifle", ParentId = "weapon", NodeType = "Node" },
        new ItemNode { Id = "knife",  Name = "Knife",        ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "throw",  Name = "ThrowWeap",    ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "launch", Name = "Launcher",     ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "ak",     Name = "ak",     ParentId = "ar",     NodeType = "Item", Props = [] },
        new ItemNode { Id = "bayonet", Name = "bayonet", ParentId = "knife",  NodeType = "Item", Props = [] },
        new ItemNode { Id = "nade",   Name = "nade",   ParentId = "throw",  NodeType = "Item", Props = [] },
        new ItemNode { Id = "gp25",   Name = "gp25",   ParentId = "launch", NodeType = "Item", Props = [] },
    ]);

    [Fact]
    public void Factory_reproduces_inspector_ancestor_rules_plus_boltaction()
    {
        var rules = DefaultWeaponRuleFactory.Build(MakeDb(), ["Weapon", "Knife", "ThrowWeap", "Launcher"]);
        var typesByAncestor = rules.ToDictionary(r => r.Type);

        typesByAncestor.Should().ContainKey("{directChildOf:Weapon}");
        typesByAncestor.Should().ContainKey("Knife");
        typesByAncestor.Should().ContainKey("ThrowWeap");
        typesByAncestor.Should().ContainKey("Launcher");
        typesByAncestor.Should().ContainKey("BoltActionSniperRifle");
    }
}
```

- [ ] **Step 2: Run test to verify it fails or passes**

Run: `dotnet test --filter "FullyQualifiedName~PipelineRunnerRulesTests"`
Expected: PASS already (factory behavior is established in Task 2). This test is a regression guard for the Step 3 edit; keep it.

- [ ] **Step 3: Consolidate `PipelineRunner.cs`**

In `AddMissingQuestRequirements.Inspector/PipelineRunner.cs`, delete the entire `BuildDefaultWeaponRules` method (lines ~15-31, including its doc comment) and change line ~43 from:

```csharp
        var weaponCategorizer = new WeaponCategorizer(BuildDefaultWeaponRules(config.WeaponLikeAncestors));
```

to:

```csharp
        var weaponCategorizer = new WeaponCategorizer(
            DefaultWeaponRuleFactory.Build(itemDb, config.WeaponLikeAncestors));
```

(`itemDb` is already bound at the top of `Run`. The `using AddMissingQuestRequirements.Pipeline.Weapon;` import is already present.)

- [ ] **Step 4: Build + run the test**

Run: `dotnet build -c Release` → success (no unused-method or missing-import errors).
Run: `dotnet test --filter "FullyQualifiedName~PipelineRunnerRulesTests"` → PASS.

- [ ] **Step 5: Commit**

```bash
git add AddMissingQuestRequirements.Inspector/PipelineRunner.cs AddMissingQuestRequirements.Tests/Inspector/PipelineRunnerRulesTests.cs
git commit -m "refactor(inspector): use DefaultWeaponRuleFactory, drop stale local builder"
```

---

### Task 4: Wire canonicalization into both entry points

**Goal:** Call `OverrideIdCanonicalizer.Normalize` after override merge in both the SPT loader and the Inspector, before categorization.

**Files:**
- Modify: `AddMissingQuestRequirements/Spt/AddMissingQuestRequirementsLoader.cs` (after settings built, before `ValidateOverrideIds`)
- Modify: `AddMissingQuestRequirements.Inspector/PipelineRunner.cs` (top of `Run`, before categorization)

**Acceptance Criteria:**
- [ ] Loader calls `OverrideIdCanonicalizer.Normalize(settings, itemDb)` between the `settings` assembly block and `ValidateOverrideIds`.
- [ ] `PipelineRunner.Run` calls `OverrideIdCanonicalizer.Normalize(settings, itemDb)` before weapon categorization.
- [ ] Full unit suite stays green.

**Verify:** `dotnet build -c Release` → success; `dotnet test --filter "FullyQualifiedName!~Integration"` → all pass.

**Steps:**

- [ ] **Step 1: Edit the loader**

In `AddMissingQuestRequirements/Spt/AddMissingQuestRequirementsLoader.cs`, immediately after the `settings` object initializer closes (the line `};` ending the `new OverriddenSettings { … }` block, ~line 112) and before the `var questOverrideCount = …` line, insert:

```csharp
            // Fold any miscased override item IDs (e.g. uppercase Massivesoft IDs)
            // to the DB's canonical casing — fixes both lookups and write-back.
            OverrideIdCanonicalizer.Normalize(settings, itemDb);
```

Add the import at the top of the file if not present:

```csharp
using AddMissingQuestRequirements.Pipeline.Override;
```

- [ ] **Step 2: Edit the Inspector runner**

In `AddMissingQuestRequirements.Inspector/PipelineRunner.cs`, at the start of `Run`, after `var itemDb = loaded.ItemDb;` and before the `// ── Categorize weapons` block, insert:

```csharp
        // Fold any miscased override item IDs to the DB's canonical casing before
        // categorization (parity with the SPT loader).
        OverrideIdCanonicalizer.Normalize(settings, itemDb);
```

Add the import at the top if not present:

```csharp
using AddMissingQuestRequirements.Pipeline.Override;
```

- [ ] **Step 3: Build + full unit suite**

Run: `dotnet build -c Release`
Expected: success.
Run: `dotnet test --filter "FullyQualifiedName!~Integration"`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add AddMissingQuestRequirements/Spt/AddMissingQuestRequirementsLoader.cs AddMissingQuestRequirements.Inspector/PipelineRunner.cs
git commit -m "feat(pipeline): run override ID canonicalization in loader and inspector"
```

---

### Task 5: Version bump + integration smoke

**Goal:** Bump the mod version and add an integration assertion that real-DB bolt-actions gain `BoltActionSniperRifle` with no override.

**Files:**
- Modify: `AddMissingQuestRequirements/Spt/ModMetadata.cs` (`2.2.0` → `2.3.0`)
- Modify: `AddMissingQuestRequirements.Tests/Integration/RealDataSmokeTests.cs` (add assertion)
- Modify: `CHANGELOG.md` (add 2.3.0 entry)

**Acceptance Criteria:**
- [ ] `ModMetadata.Version` is `2.3.0`.
- [ ] Integration test asserts a known bolt-action (KATT AMR `020020ab50ab500000000000`) is categorized `BoltActionSniperRifle` when the real slice is present; no-ops cleanly when absent.
- [ ] `CHANGELOG.md` has a `2.3.0` entry covering both fixes.

**Verify:** `dotnet test --filter "Category=Integration"` → pass (or skip when slice absent); `dotnet build -c Release` → success.

**Steps:**

- [ ] **Step 1: Bump version**

In `AddMissingQuestRequirements/Spt/ModMetadata.cs` line 18:

```csharp
    public override Version Version { get; init; } = new("2.3.0");
```

- [ ] **Step 2: Add the integration fact (matching the file's existing pattern)**

`RealDataSmokeTests.cs` is `[Trait("Category", "Integration")]` at the class level, uses `private static bool TryGetDb(out InMemoryItemDatabase db)` to resolve+skip, and `private static IReadOnlyList<TypeRule> DefaultWeaponRulesFor(ModConfig config, IItemDatabase db) => DefaultWeaponRuleFactory.Build(db, config.WeaponLikeAncestors)`. Add this `[Fact]` to the class, reusing those exact helpers — do NOT add a new trait (the class-level one applies) and do NOT invent a loader:

```csharp
    // ── Test 12: Real bolt-action gains BoltActionSniperRifle with no override ─

    [Fact]
    public void RealBoltAction_IsTaggedBoltActionSniperRifle_WithoutOverride()
    {
        if (!TryGetDb(out var db)) return;
        // KATT AMR (Massivesoft). Skip if this mod is not in the loaded slice.
        const string kattId = "020020ab50ab500000000000";
        if (!db.Items.ContainsKey(kattId)) return;

        var config = new ModConfig();
        var result = new WeaponCategorizer(DefaultWeaponRulesFor(config, db))
            .Categorize(db, new OverriddenSettings(), config);

        result.WeaponToType.Should().ContainKey(kattId);
        result.WeaponToType[kattId].Should().Contain("BoltActionSniperRifle",
            "the BoltAction property rule must tag modded bolt-actions with no manual override");
    }
```

- [ ] **Step 3: Add CHANGELOG entry**

Prepend a `2.3.0` section to `CHANGELOG.md` (match the file's existing entry format):

```markdown
## 2.3.0

### Fixed
- Override item IDs are now matched case-insensitively against the item database
  and folded to the DB's canonical casing. Miscased IDs (e.g. uppercase Massivesoft
  template IDs) in `manualTypeOverrides`, `canBeUsedAs`, and quest `included*` /
  `excluded*` lists previously no-opped silently; they now work and are written back
  into quests with the correct casing.

### Added
- Code-level default rule tags every bolt-action rifle (`_props.BoltAction == true`)
  as `BoltActionSniperRifle`, removing the need for a per-weapon manual override.
  Both the SPT loader and the Inspector inherit the rule via `DefaultWeaponRuleFactory`.
```

- [ ] **Step 4: Build + integration test**

Run: `dotnet build -c Release` → success.
Run: `dotnet test --filter "Category=Integration"` → pass (or no-op when slice absent).

- [ ] **Step 5: Commit**

```bash
git add AddMissingQuestRequirements/Spt/ModMetadata.cs AddMissingQuestRequirements.Tests/Integration/RealDataSmokeTests.cs CHANGELOG.md
git commit -m "chore(release): 2.3.0 — override ID case fix + BoltAction rule"
```

---

### Task 6: Rebuild, redeploy, before/after report diff

**Goal:** Deploy the built mod, regenerate the debug report with `debug: true`, and confirm against the saved baseline that KATT + M200 gained `BoltActionSniperRifle` and nothing regressed.

**Files:**
- None (verification task). Baseline lives at `K:\SPT4\SPT\user\mods\AddMissingQuestRequirements\_baseline-2026-06-01-pre-fix\`.

**Acceptance Criteria:**
- [ ] Post-fix report shows KATT AMR (`020020ab50ab500000000000`) and M200 (`68fd4feab87d77a5aaf6bf64`) with `BoltActionSniperRifle` in their type sets.
- [ ] `BoltActionSniperRifle` member count rises from baseline 3 toward ~22.
- [ ] No weapon that had a type in the baseline loses it (spot-check the 20 previously-dead uppercase override IDs now resolve).

**Verify:** Manual diff of the regenerated report against the baseline JSON (commands below).

**Steps:**

- [ ] **Step 1: Build + auto-deploy (Windows)**

Run: `dotnet build -c Release`
This triggers the `DeployToSptMods` target (gated on Windows) copying the DLL to `K:\SPT4\SPT\user\mods\AddMissingQuestRequirements\`.

- [ ] **Step 2: Regenerate the report**

Ensure `config/config.jsonc` has `"debug": true` in the deployed mod, then launch the SPT server once to run the loader (or run the Inspector one-shot against the same slice). The loader writes `AddMissingQuestRequirements-debug-report.json` to the mod folder.

- [ ] **Step 3: Diff against baseline**

Run (from the mod folder):

```bash
node -e '
const fs=require("fs");
const dir="K:/SPT4/SPT/user/mods/AddMissingQuestRequirements";
const base=JSON.parse(fs.readFileSync(dir+"/_baseline-2026-06-01-pre-fix/AddMissingQuestRequirements-debug-report.json"));
const now=JSON.parse(fs.readFileSync(dir+"/AddMissingQuestRequirements-debug-report.json"));
const tcount=r=>{const t=r.Types||r.types||{};const b=t["BoltActionSniperRifle"]||[];return Array.isArray(b)?b.length:Object.keys(b).length;};
const find=(r,id)=>{const W=r.Weapons||r.weapons||[];const w=W.find(x=>(x.Id||x.id)===id);return w?(w.Types||w.types||[]):null;};
console.log("BoltActionSniperRifle members: baseline",tcount(base),"-> now",tcount(now));
for(const [name,id] of [["KATT","020020ab50ab500000000000"],["M200","68fd4feab87d77a5aaf6bf64"],["Longbow","02002010ab10ab0000000000"]]){
  console.log(name, "baseline", JSON.stringify(find(base,id)), "-> now", JSON.stringify(find(now,id)));
}
'
```

Expected: member count rises (3 → ~22); KATT and M200 type sets now include `BoltActionSniperRifle`; Longbow still tagged.

- [ ] **Step 4: Record the result**

If the diff matches expectations, note it in the PR description. If any weapon lost a type, STOP and investigate before merging (the canonicalization or rule append regressed something).

---

## Notes for the implementer

- **No config files change.** The bundled `BoltActionSniperRifle` manual overrides become redundant but are intentionally left in place (the core BoltAction rule stacks harmlessly).
- **Braces always**, no expression-bodied members with logic, `var` when obvious, `sealed` concrete classes, collection expressions — match the surrounding style (see `CLAUDE.md` C# code style).
- **Why canonicalization, not a comparer swap:** override IDs are written back into `quests.json` verbatim (`WeaponArrayExpander.cs:188`); the game matches template IDs by exact case, so a comparer alone would accept a miscased ID internally but still emit a dead ID. Canonicalization fixes both.
- **Task order / dependencies:** Task 2 before Task 3 (factory must carry the BoltAction rule before the Inspector consolidates onto it). Task 1 before Task 4 (canonicalizer must exist). Task 3 before Task 4 (Task 4 edits the same `PipelineRunner.Run` Task 3 touches — sequencing avoids a merge conflict). Tasks 5-6 last.
