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
                        ExcludedModBundles = [["020020AB50AB500000000000"]],
                    },
                ],
            },
        };

        OverrideIdCanonicalizer.Normalize(settings, MakeDb());

        var entry = settings.QuestOverrides["q1"][0];
        entry.IncludedWeapons.Should().BeEquivalentTo(["02002010ab10ab0000000000", "BoltActionSniperRifle"]);
        entry.ExcludedWeapons.Should().BeEquivalentTo(["020020ab50ab500000000000"]);
        entry.IncludedModBundles[0].Should().BeEquivalentTo(["02002010ab10ab0000000000"]);
        entry.ExcludedModBundles[0].Should().BeEquivalentTo(["020020ab50ab500000000000"]);
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
        // Last-seen casing's value wins (insertion order: lowercase "Shotgun" entry is last).
        settings.ManualTypeOverrides["02002010ab10ab0000000000"].Should().Be("Shotgun");
    }
}
