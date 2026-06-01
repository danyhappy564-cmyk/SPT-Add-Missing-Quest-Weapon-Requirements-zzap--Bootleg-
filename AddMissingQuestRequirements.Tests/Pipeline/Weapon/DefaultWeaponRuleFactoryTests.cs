using System.Text.Json;
using AddMissingQuestRequirements.Models;
using AddMissingQuestRequirements.Pipeline.Database;
using AddMissingQuestRequirements.Pipeline.Weapon;
using FluentAssertions;

namespace AddMissingQuestRequirements.Tests.Pipeline.Weapon;

public class DefaultWeaponRuleFactoryTests
{
    private static InMemoryItemDatabase BuildDb()
    {
        // Tree:
        //   root (Node)
        //   ├── Weapon (Node)
        //   │   └── AssaultRifle (Node)
        //   │       └── m4a1 (Item)  — subtree: directChildOf:Weapon fires
        //   ├── Knife (Node)
        //   │   └── bayonet (Item)   — no subtree: literal "Knife"
        //   └── Lonely (Node)         — no descendant items: literal "Lonely"
        var items = new Dictionary<string, ItemNode>
        {
            ["root"]     = new() { Id = "root",     Name = "Item",         NodeType = "Node" },
            ["weap"]     = new() { Id = "weap",     Name = "Weapon",       ParentId = "root", NodeType = "Node" },
            ["ar"]       = new() { Id = "ar",       Name = "AssaultRifle", ParentId = "weap", NodeType = "Node" },
            ["m4a1"]     = new() { Id = "m4a1",     Name = "M4A1",         ParentId = "ar",   NodeType = "Item" },
            ["knife"]    = new() { Id = "knife",    Name = "Knife",        ParentId = "root", NodeType = "Node" },
            ["bayonet"]  = new() { Id = "bayonet",  Name = "Bayonet",      ParentId = "knife", NodeType = "Item" },
            ["lonely"]   = new() { Id = "lonely",   Name = "Lonely",       ParentId = "root", NodeType = "Node" },
        };
        return InMemoryItemDatabase.FromItemsOnly(items);
    }

    // root → weapon → SniperRifle → boltgun (BoltAction=true)
    //               → Shotgun     → boltshotty (BoltAction=true)  — must NOT become a sniper
    private static InMemoryItemDatabase MakeDb() => new(
    [
        new ItemNode { Id = "root",    Name = "Item",        ParentId = null,     NodeType = "Node" },
        new ItemNode { Id = "weapon",  Name = "Weapon",      ParentId = "root",   NodeType = "Node" },
        new ItemNode { Id = "snode",   Name = "SniperRifle", ParentId = "weapon", NodeType = "Node" },
        new ItemNode { Id = "boltgun", Name = "boltgun",     ParentId = "snode",  NodeType = "Item",
            Props = new() { ["BoltAction"] = JsonDocument.Parse("true").RootElement } },
        new ItemNode { Id = "shnode",  Name = "Shotgun",     ParentId = "weapon", NodeType = "Node" },
        new ItemNode { Id = "boltshotty", Name = "boltshotty", ParentId = "shnode", NodeType = "Item",
            Props = new() { ["BoltAction"] = JsonDocument.Parse("true").RootElement } },
    ],
    localeNames: new() { ["boltgun"] = "Modded bolt-action rifle", ["boltshotty"] = "TOZ-106-like bolt-action shotgun" });

    [Fact]
    public void WeaponAncestor_WithSubtree_EmitsDirectChildOfTemplate()
    {
        var db = BuildDb();
        var rules = DefaultWeaponRuleFactory.Build(db, ["Weapon"]);
        // 1 ancestor rule + 1 BoltAction rule appended by DefaultWeaponRules
        rules.Should().HaveCount(2);
        rules[0].Type.Should().Be("{directChildOf:Weapon}");
        rules[0].Conditions["hasAncestor"].GetString().Should().Be("Weapon");
    }

    [Fact]
    public void KnifeAncestor_DirectLeavesOnly_EmitsLiteralType()
    {
        var db = BuildDb();
        var rules = DefaultWeaponRuleFactory.Build(db, ["Knife"]);
        // 1 ancestor rule + 1 BoltAction rule appended by DefaultWeaponRules
        rules.Should().HaveCount(2);
        rules[0].Type.Should().Be("Knife");
        rules[0].Conditions["hasAncestor"].GetString().Should().Be("Knife");
    }

    [Fact]
    public void LonelyAncestor_NoDescendantItems_EmitsLiteralType()
    {
        var db = BuildDb();
        var rules = DefaultWeaponRuleFactory.Build(db, ["Lonely"]);
        // 1 ancestor rule + 1 BoltAction rule appended by DefaultWeaponRules
        rules.Should().HaveCount(2);
        rules[0].Type.Should().Be("Lonely");
    }

    [Fact]
    public void MultipleAncestors_EmitOneRuleEachInInputOrder()
    {
        var db = BuildDb();
        var rules = DefaultWeaponRuleFactory.Build(db, ["Knife", "Weapon", "Lonely"]);
        // 3 ancestor rules + 1 BoltAction rule appended by DefaultWeaponRules
        rules.Should().HaveCount(4);
        rules[0].Type.Should().Be("Knife");
        rules[1].Type.Should().Be("{directChildOf:Weapon}");
        rules[2].Type.Should().Be("Lonely");
    }

    [Fact]
    public void EmptyAncestors_StillSurfacesPropertyDefaults()
    {
        var db = BuildDb();
        // Property/structural defaults are independent of WeaponLikeAncestors.
        var rules = DefaultWeaponRuleFactory.Build(db, []);
        rules.Should().HaveCount(1);
        rules[0].Type.Should().Be("BoltActionSniperRifle");
    }

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

    [Fact]
    public void BoltAction_shotgun_is_not_tagged_BoltActionSniperRifle()
    {
        // The BoltAction prop is not exclusive to snipers (TOZ-106 is a bolt-action
        // shotgun). The hasAncestor:SniperRifle guard must exclude it.
        var rules = DefaultWeaponRuleFactory.Build(MakeDb(), ["Weapon"]);
        var result = new WeaponCategorizer(rules)
            .Categorize(MakeDb(), new OverriddenSettings(), new ModConfig());

        result.WeaponToType["boltshotty"].Should().NotContain("BoltActionSniperRifle");
    }
}
