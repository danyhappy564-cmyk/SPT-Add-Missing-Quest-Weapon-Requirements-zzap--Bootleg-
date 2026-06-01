# `applyToManualOverrides` Rule Flag Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers-extended-cc:subagent-driven-development (recommended) or superpowers-extended-cc:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an opt-in per-rule `applyToManualOverrides` boolean so user-authored non-core rules (e.g. `nameMatches:"^(HK)"`) can stack on items that already carry a `manualTypeOverrides` entry.

**Architecture:** Additive field on `TypeRule`. Threaded through `RuleEngine.CompiledRule` and `RuleMatch`. `CategorizationHelper.BuildTypeMaps` extends its suppression check to let `(IsCore || ApplyToManualOverrides)` matches through when a manual override is present. No schema migration — default `false` preserves current behavior. No flag = current heuristic-suppression contract still holds.

**Tech Stack:** .NET 9 / C# 13, `System.Text.Json`, xUnit + FluentAssertions, SPT-AKI server mod conventions.

---

## Background

`AddMissingQuestRequirements/Pipeline/Shared/CategorizationHelper.cs:160-172` suppresses non-core rule matches for any item that has a `manualTypeOverrides` entry. "Core" (`Pipeline/Rules/RuleCoreDetector.cs:22-27`) = `caliber` / `hasAncestor` / `properties` only. Original intent (commit `f92d45f`): the user wrote the override *because* the name/desc heuristic was wrong; don't re-apply it.

User-reported case: HK weapons listed in `manualTypeOverrides` to mark them as `AssaultRifle,AssaultCarbine` never join the `HK` group built by a `nameMatches:"^(HK)"` rule. User wants additive behavior.

Decision: per-rule opt-in. Rule author flips a flag on the rule when they know it should stack regardless of overrides. Lowest blast radius, escape valve preserved.

## File Map

- `AddMissingQuestRequirements/Models/TypeRule.cs` — add `ApplyToManualOverrides` init bool field.
- `AddMissingQuestRequirements/Pipeline/Rules/RuleEngine.cs` — `CompiledRule` stores flag; `RuleMatch` carries flag; `EvaluateAll` propagates it.
- `AddMissingQuestRequirements/Pipeline/Shared/CategorizationHelper.cs` — extend suppression check.
- `AddMissingQuestRequirements.Tests/Models/TypeRuleTests.cs` — **new** file, JSON binding.
- `AddMissingQuestRequirements.Tests/Pipeline/Rules/RuleEngineTests.cs` — flag propagation test.
- `AddMissingQuestRequirements.Tests/Pipeline/Weapon/WeaponCategorizerTests.cs` — HK scenario + regression (default-false still suppresses).
- `AddMissingQuestRequirements.Tests/Pipeline/Attachment/AttachmentCategorizerTests.cs` — parity test.
- `CLAUDE.md` — `§Rule-chain type detection` mention.
- `CHANGELOG.md` — new minor entry.
- `AddMissingQuestRequirements/Spt/ModMetadata.cs` — bump `2.1.0` → `2.2.0`.

---

### Task 1: Add `ApplyToManualOverrides` field to `TypeRule`

**Goal:** Model exposes optional flag, default `false`, JSON-bound via `[JsonPropertyName("applyToManualOverrides")]`.

**Files:**
- Modify: `AddMissingQuestRequirements/Models/TypeRule.cs`
- Create: `AddMissingQuestRequirements.Tests/Models/TypeRuleTests.cs`

**Acceptance Criteria:**
- [ ] `TypeRule.ApplyToManualOverrides` is an `init bool` defaulting to `false`.
- [ ] Missing key in JSON → property is `false`.
- [ ] `"applyToManualOverrides": true` deserializes to `true`.
- [ ] `"applyToManualOverrides": false` deserializes to `false`.

**Verify:** `dotnet test --filter "FullyQualifiedName~TypeRuleTests"` → 3 passing tests.

**Steps:**

- [ ] **Step 1: Write the failing test file**

```csharp
// AddMissingQuestRequirements.Tests/Models/TypeRuleTests.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using AddMissingQuestRequirements.Models;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Models;

public class TypeRuleTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ApplyToManualOverrides_missing_defaults_to_false()
    {
        var json = """{ "conditions": {}, "type": "MyType" }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeFalse();
    }

    [Fact]
    public void ApplyToManualOverrides_true_deserializes()
    {
        var json = """{ "conditions": {}, "type": "MyType", "applyToManualOverrides": true }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeTrue();
    }

    [Fact]
    public void ApplyToManualOverrides_false_deserializes()
    {
        var json = """{ "conditions": {}, "type": "MyType", "applyToManualOverrides": false }""";
        var rule = JsonSerializer.Deserialize<TypeRule>(json, Options);
        rule!.ApplyToManualOverrides.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run — confirm failure**

```
dotnet test --filter "FullyQualifiedName~TypeRuleTests"
```
Expected: compile error `'TypeRule' does not contain a definition for 'ApplyToManualOverrides'`.

- [ ] **Step 3: Add field to `TypeRule`**

Insert after the existing `Behaviour` property (line 38) in `AddMissingQuestRequirements/Models/TypeRule.cs`:

```csharp
    /// <summary>
    /// Opt-in escape valve: when <see langword="true"/>, this rule contributes its match
    /// to items that already have a <c>manualTypeOverrides</c> entry, even if the rule
    /// is non-core (nameContains / nameMatches / pathMatches / descriptionMatches).
    /// Default <see langword="false"/> preserves the heuristic-suppression contract from commit f92d45f.
    /// </summary>
    [JsonPropertyName("applyToManualOverrides")]
    public bool ApplyToManualOverrides { get; init; } = false;
```

- [ ] **Step 4: Run — confirm pass**

```
dotnet test --filter "FullyQualifiedName~TypeRuleTests"
```
Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add AddMissingQuestRequirements/Models/TypeRule.cs \
        AddMissingQuestRequirements.Tests/Models/TypeRuleTests.cs
git commit -m "feat(rules): add applyToManualOverrides flag to TypeRule schema"
```

---

### Task 2: Thread flag through `RuleEngine` and `RuleMatch`

**Goal:** `RuleMatch` carries `ApplyToManualOverrides`. `CompiledRule` stores it once at construction. `EvaluateAll` propagates per match.

**Files:**
- Modify: `AddMissingQuestRequirements/Pipeline/Rules/RuleEngine.cs`
- Modify: `AddMissingQuestRequirements.Tests/Pipeline/Rules/RuleEngineTests.cs`

**Acceptance Criteria:**
- [ ] `RuleMatch` record has 4th positional `bool ApplyToManualOverrides`.
- [ ] When a `TypeRule.ApplyToManualOverrides = true` rule matches, the returned `RuleMatch.ApplyToManualOverrides` is `true`.
- [ ] When default-false rule matches, `RuleMatch.ApplyToManualOverrides` is `false`.

**Verify:** `dotnet test --filter "FullyQualifiedName~RuleEngineTests"` → all existing tests still pass + new test passes.

**Steps:**

- [ ] **Step 1: Write failing test**

Append to `AddMissingQuestRequirements.Tests/Pipeline/Rules/RuleEngineTests.cs` (inside `RuleEngineTests` class):

```csharp
    [Fact]
    public void EvaluateAll_propagates_ApplyToManualOverrides_flag()
    {
        var flagged = new TypeRule
        {
            Type = "Flagged",
            Conditions = new() { ["hasAncestor"] = JsonDocument.Parse("\"Weapon\"").RootElement },
            ApplyToManualOverrides = true
        };
        var unflagged = new TypeRule
        {
            Type = "Unflagged",
            Conditions = new() { ["hasAncestor"] = JsonDocument.Parse("\"Weapon\"").RootElement }
        };
        var engine = new RuleEngine([flagged, unflagged], Db);
        var matches = engine.EvaluateAll(Db.Items["ak74"]);

        matches.Single(m => m.Type == "Flagged").ApplyToManualOverrides.Should().BeTrue();
        matches.Single(m => m.Type == "Unflagged").ApplyToManualOverrides.Should().BeFalse();
    }
```

- [ ] **Step 2: Run — confirm failure**

```
dotnet test --filter "FullyQualifiedName~EvaluateAll_propagates_ApplyToManualOverrides"
```
Expected: compile error — `RuleMatch` has no `ApplyToManualOverrides` member.

- [ ] **Step 3: Extend `RuleMatch`**

In `AddMissingQuestRequirements/Pipeline/Rules/RuleEngine.cs` replace line 13:

```csharp
public sealed record RuleMatch(string Type, IReadOnlyList<string> AlsoAs, bool IsCore, bool ApplyToManualOverrides);
```

- [ ] **Step 4: Extend `CompiledRule`**

Replace the record at lines 33-36:

```csharp
    private readonly record struct CompiledRule(
        IReadOnlyList<IRuleCondition> Conditions,
        TypeRule Rule,
        bool IsCore,
        bool ApplyToManualOverrides);
```

- [ ] **Step 5: Populate flag at construction**

Replace constructor body (lines 38-46):

```csharp
    public RuleEngine(IEnumerable<TypeRule> rules, IItemDatabase db)
    {
        _db = db;
        _compiled = [..rules.Select(r => new CompiledRule(
            [..r.Conditions.Select(kvp => ConditionFactory.Create(kvp.Key, kvp.Value))],
            r,
            RuleCoreDetector.IsCore(r.Conditions),
            r.ApplyToManualOverrides
        ))];
    }
```

- [ ] **Step 6: Emit flag from `EvaluateAll`**

Replace the `matches.Add(...)` call at line 70:

```csharp
            matches.Add(new RuleMatch(resolvedType, compiled.Rule.AlsoAs, compiled.IsCore, compiled.ApplyToManualOverrides));
```

- [ ] **Step 7: Run — confirm pass**

```
dotnet test --filter "FullyQualifiedName~RuleEngineTests"
```
Expected: all RuleEngine tests pass (compiler may also force fixing other `new RuleMatch(...)` sites — search and fix them; expected none outside this file but verify with `grep -rn "new RuleMatch(" AddMissingQuestRequirements/`).

- [ ] **Step 8: Commit**

```bash
git add AddMissingQuestRequirements/Pipeline/Rules/RuleEngine.cs \
        AddMissingQuestRequirements.Tests/Pipeline/Rules/RuleEngineTests.cs
git commit -m "feat(rules): thread applyToManualOverrides flag through RuleEngine"
```

---

### Task 3: Honor flag in `BuildTypeMaps` suppression

**Goal:** Suppression check at `CategorizationHelper.cs:163` lets `match.ApplyToManualOverrides` matches through alongside core matches.

**Files:**
- Modify: `AddMissingQuestRequirements/Pipeline/Shared/CategorizationHelper.cs`

**Acceptance Criteria:**
- [ ] Flagged non-core rule contributes types to items with manual overrides.
- [ ] Unflagged non-core rules still suppressed for overridden items.
- [ ] Core rules still always merge (unchanged behavior).
- [ ] All existing tests in `WeaponCategorizerTests` and `AttachmentCategorizerTests` pass unchanged.

**Verify:** `dotnet test --filter "FullyQualifiedName!~Integration"`

**Steps:**

- [ ] **Step 1: Edit the suppression check**

In `AddMissingQuestRequirements/Pipeline/Shared/CategorizationHelper.cs`, replace lines 155-172 (the comment block + foreach with the `if (hasManualOverride && !match.IsCore) continue;` branch). Replace the comment too — it must mention the new opt-in:

```csharp
            // Apply rule matches: when a manual override is present, only core rules
            // (caliber / hasAncestor / properties) merge in by default. Non-core rules
            // (nameContains, nameMatches, pathMatches, descriptionMatches) are suppressed
            // because the user may have overridden them deliberately. Rule authors can
            // opt a non-core rule into the merge by setting `applyToManualOverrides: true`
            // on the TypeRule — this is the escape valve for heuristic groupings (e.g. an
            // "HK" name-match rule) that should stack regardless of the user's override.
            // When no manual override is present, all matching rules fire as before.
            var matches = engine.EvaluateAll(item);
            foreach (var match in matches)
            {
                if (hasManualOverride && !match.IsCore && !match.ApplyToManualOverrides)
                {
                    continue;
                }

                foreach (var t in getTypes(match))
                {
                    typeSet.Add(t);
                }
            }
```

- [ ] **Step 2: Run full unit suite — confirm no regressions**

```
dotnet test --filter "FullyQualifiedName!~Integration"
```
Expected: all green. If any test fails, the override semantics were stricter than we modeled — re-read the failing test and surface to the user before patching it.

- [ ] **Step 3: Commit**

```bash
git add AddMissingQuestRequirements/Pipeline/Shared/CategorizationHelper.cs
git commit -m "feat(categorizer): honor applyToManualOverrides in suppression check"
```

---

### Task 4: WeaponCategorizer HK scenario + regression tests

**Goal:** Pin the user-reported behavior. One test reproduces the HK fix; one test pins that the default-false flag keeps prior suppression.

**Files:**
- Modify: `AddMissingQuestRequirements.Tests/Pipeline/Weapon/WeaponCategorizerTests.cs`

**Acceptance Criteria:**
- [ ] Test `Manual_Override_does_not_suppress_flagged_non_core_rule` proves a `nameMatches` rule with `applyToManualOverrides:true` adds its type to an overridden weapon.
- [ ] Test `Manual_Override_still_suppresses_unflagged_non_core_rule` proves the default-false flag still suppresses.

**Verify:** `dotnet test --filter "FullyQualifiedName~WeaponCategorizer"`

**Steps:**

- [ ] **Step 1: Add the two tests**

Insert after the existing `Manual_Override_comma_separated_types_adds_to_multiple_and_merges_catch_all` (after line 236) in `AddMissingQuestRequirements.Tests/Pipeline/Weapon/WeaponCategorizerTests.cs`:

```csharp
    [Fact]
    public void Manual_Override_does_not_suppress_flagged_non_core_rule()
    {
        // Reproduces user-reported HK case: weapons listed in manualTypeOverrides
        // never joined the "HK" group built by a nameMatches rule. With
        // applyToManualOverrides:true on the rule, the override and the rule stack.
        //
        // ak74 has locale "AKS-74U 5.45x39 assault rifle" — does NOT match "^HK",
        // so we use ak47 which has locale "AKM 7.62x39 assault rifle" — also no HK.
        // Mint a rule that matches "AKM" via nameMatches so the case is unambiguous.
        var settings = new OverriddenSettings
        {
            ManualTypeOverrides = new() { ["ak47"] = "AssaultRifle,AssaultCarbine" },
            TypeRules =
            [
                new TypeRule
                {
                    Type = "AKM_Family",
                    Conditions = new() { ["nameMatches"] = Str("^AKM") },
                    ApplyToManualOverrides = true
                }
            ]
        };
        var result = new WeaponCategorizer(DefaultRules)
            .Categorize(MakeDb(), settings, new ModConfig());

        result.WeaponToType["ak47"].Should().Contain("AssaultRifle");
        result.WeaponToType["ak47"].Should().Contain("AssaultCarbine");
        result.WeaponToType["ak47"].Should().Contain("AKM_Family");
        result.WeaponTypes["AKM_Family"].Should().Contain("ak47");
    }

    [Fact]
    public void Manual_Override_still_suppresses_unflagged_non_core_rule()
    {
        // Regression pin: with flag default-false the historical suppression still applies.
        var settings = new OverriddenSettings
        {
            ManualTypeOverrides = new() { ["ak47"] = "AssaultRifle" },
            TypeRules =
            [
                new TypeRule
                {
                    Type = "AKM_Family",
                    Conditions = new() { ["nameMatches"] = Str("^AKM") }
                }
            ]
        };
        var result = new WeaponCategorizer(DefaultRules)
            .Categorize(MakeDb(), settings, new ModConfig());

        result.WeaponToType["ak47"].Should().NotContain("AKM_Family");
        result.WeaponTypes.Should().NotContainKey("AKM_Family");
    }
```

- [ ] **Step 2: Run — confirm pass**

```
dotnet test --filter "FullyQualifiedName~WeaponCategorizer"
```
Expected: both new tests green, all prior WeaponCategorizer tests green.

- [ ] **Step 3: Commit**

```bash
git add AddMissingQuestRequirements.Tests/Pipeline/Weapon/WeaponCategorizerTests.cs
git commit -m "test(weapon): cover applyToManualOverrides merge + default suppression"
```

---

### Task 5: AttachmentCategorizer parity test

**Goal:** Flag traverses the same shared `CategorizerCore` path for attachments. One test pins parity.

**Files:**
- Modify: `AddMissingQuestRequirements.Tests/Pipeline/Attachment/AttachmentCategorizerTests.cs`

**Acceptance Criteria:**
- [ ] One test where an attachment item carries a manual type override AND a flagged non-core rule fires → both types present.

**Verify:** `dotnet test --filter "FullyQualifiedName~AttachmentCategorizer"`

**Steps:**

- [ ] **Step 1: Open the test file and locate an existing test that builds a known attachment item ID + locale.**

Use the same `DefaultRules` + DB scaffold as the other attachment tests in that file. Find the attachment item ID currently used in existing tests (e.g. a silencer or scope) and a name your test rule can regex-match.

- [ ] **Step 2: Add the parity test (template — adapt IDs to whatever the existing fixture uses):**

```csharp
    [Fact]
    public void Manual_Override_does_not_suppress_flagged_non_core_rule_for_attachments()
    {
        // Mirror of WeaponCategorizerTests parity test — proves the flag path travels
        // through CategorizerCore identically for attachments.
        //
        // Replace <ATT_ID> and <NAME_REGEX> with values from the file's existing fixture.
        var settings = new OverriddenSettings
        {
            ManualAttachmentTypeOverrides = new() { ["<ATT_ID>"] = "OverrideType" },
            AttachmentTypeRules =
            [
                new TypeRule
                {
                    Type = "FlaggedGroup",
                    Conditions = new()
                    {
                        ["nameMatches"] = JsonDocument.Parse("\"<NAME_REGEX>\"").RootElement
                    },
                    ApplyToManualOverrides = true
                }
            ]
        };
        var cat = new AttachmentCategorizer(DefaultRules)
            .Categorize(MakeDb(), settings);

        cat.ItemToType["<ATT_ID>"].Should().Contain("OverrideType");
        cat.ItemToType["<ATT_ID>"].Should().Contain("FlaggedGroup");
    }
```

The exact field names on the attachment result (`ItemToType` vs `AttachmentToType`) should mirror what existing tests in the file assert against — copy that shape, do not invent.

- [ ] **Step 3: Run — confirm pass**

```
dotnet test --filter "FullyQualifiedName~AttachmentCategorizer"
```
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add AddMissingQuestRequirements.Tests/Pipeline/Attachment/AttachmentCategorizerTests.cs
git commit -m "test(attachment): cover applyToManualOverrides parity"
```

---

### Task 6: Docs, CHANGELOG, version bump

**Goal:** Surface the new flag to mod authors; bump the version per repo policy.

**Files:**
- Modify: `CLAUDE.md` (§Rule-chain type detection)
- Modify: `CHANGELOG.md`
- Modify: `AddMissingQuestRequirements/Spt/ModMetadata.cs`

**Acceptance Criteria:**
- [ ] CLAUDE.md's §Rule-chain type detection mentions `applyToManualOverrides` as the escape valve for non-core rules.
- [ ] CHANGELOG has a `2.2.0` entry describing the additive flag and the user-facing benefit.
- [ ] `ModMetadata.Version` is `"2.2.0"`.

**Verify:** `dotnet build -c Release && dotnet test --filter "FullyQualifiedName!~Integration"` → both succeed; spot-check the version string in build output.

**Steps:**

- [ ] **Step 1: Update CLAUDE.md**

Find the section `### Rule-chain type detection`. After the paragraph describing how user rules merge before defaults, add:

```markdown
**`applyToManualOverrides` flag:** non-core rule matches (`nameContains` / `nameMatches` / `pathMatches` / `descriptionMatches`) are suppressed for items already listed in `manualTypeOverrides`, because the override was likely written to escape the heuristic. Set `"applyToManualOverrides": true` on a rule to opt that single rule into stacking on overridden items anyway — use this for grouping rules (e.g. an "HK family" name-match) that should always apply even when the same items carry structural-type overrides.
```

- [ ] **Step 2: Update CHANGELOG.md**

Add a new section at the top (above the previous most-recent entry):

```markdown
## 2.2.0

### Added
- `applyToManualOverrides` boolean on `TypeRule` (default `false`). When `true`, a non-core rule (`nameContains` / `nameMatches` / `pathMatches` / `descriptionMatches`) merges its type into items that have a `manualTypeOverrides` entry instead of being suppressed. Lets mod authors define grouping rules (e.g. an "HK" name-match) that stack with per-item structural overrides.
```

(Match the existing CHANGELOG heading style; if entries use different sub-sections, follow that file's convention.)

- [ ] **Step 3: Bump version**

In `AddMissingQuestRequirements/Spt/ModMetadata.cs`, change line 18:

```csharp
    public override Version Version { get; init; } = new("2.2.0");
```

- [ ] **Step 4: Verify build + tests**

```
dotnet build -c Release
dotnet test --filter "FullyQualifiedName!~Integration"
```
Expected: build success, all unit tests pass.

- [ ] **Step 5: Commit**

```bash
git add CLAUDE.md CHANGELOG.md AddMissingQuestRequirements/Spt/ModMetadata.cs
git commit -m "docs(rules): document applyToManualOverrides flag; bump 2.2.0"
```

---

## Self-Review Notes

- **Spec coverage:** every requirement (additive field, threaded through engine, honored in suppression, regression coverage for both paths, docs, version) maps to a task. No gaps.
- **No placeholders:** every code step shows actual code; Task 5 leaves `<ATT_ID>` / `<NAME_REGEX>` placeholders only because they depend on the existing fixture in a file we did not exhaustively read — the implementer must inspect the test file and substitute. Flagged inline.
- **Type consistency:** `ApplyToManualOverrides` used identically in all 4 sites (`TypeRule`, `CompiledRule`, `RuleMatch`, helper check). JSON name `applyToManualOverrides` lowercase-first everywhere.
- **Scope:** single subsystem (rule engine + categorizer). One plan.
