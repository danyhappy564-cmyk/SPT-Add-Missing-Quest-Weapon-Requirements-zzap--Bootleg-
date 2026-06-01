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
        new ItemNode { Id = "ak",     Name = "ak",      ParentId = "ar",     NodeType = "Item", Props = [] },
        new ItemNode { Id = "bayonet", Name = "bayonet", ParentId = "knife",  NodeType = "Item", Props = [] },
        new ItemNode { Id = "nade",   Name = "nade",    ParentId = "throw",  NodeType = "Item", Props = [] },
        new ItemNode { Id = "gp25",   Name = "gp25",    ParentId = "launch", NodeType = "Item", Props = [] },
    ]);

    [Fact]
    public void Factory_reproduces_inspector_ancestor_rules_plus_boltaction()
    {
        var rules = DefaultWeaponRuleFactory.Build(MakeDb(), ["Weapon", "Knife", "ThrowWeap", "Launcher"]);
        var typesPresent = rules.Select(r => r.Type).ToHashSet();

        typesPresent.Should().Contain("{directChildOf:Weapon}");
        typesPresent.Should().Contain("Knife");
        typesPresent.Should().Contain("ThrowWeap");
        typesPresent.Should().Contain("Launcher");
        typesPresent.Should().Contain("BoltActionSniperRifle");
        // Guard against the old stale Inspector behaviour (literal "Weapon" instead
        // of the DB-driven {directChildOf:Weapon}).
        typesPresent.Should().NotContain("Weapon");
    }
}
