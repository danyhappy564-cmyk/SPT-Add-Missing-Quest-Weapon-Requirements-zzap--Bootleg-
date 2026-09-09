using System.Reflection;
using AddMissingQuestRequirements.Spt;
using FluentAssertions;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;

namespace AddMissingQuestRequirements.Tests.Spt;

/// <summary>
/// This mod rewrites weapon condition arrays in place on the quest objects already in SPT's
/// database, so anything that writes quests after it silently undoes the work — completely, if it
/// replaces whole <c>Quest</c> objects rather than editing fields, because the expanded arrays live
/// on the object being discarded.
///
/// The loader used to sit at <c>TraderRegistration + 9999</c>, which put it roughly 690,000 priority
/// units ahead of SptQuestLive's <c>PostLoad + 1</c>. Every quest that mod overrides lost its
/// expansion, and neither mod logged anything about it. These tests keep the ordering from drifting
/// back.
/// </summary>
public class LoaderLoadOrderTests
{
    private static int Priority =>
        typeof(AddMissingQuestRequirementsLoader)
            .GetCustomAttribute<Injectable>()!
            .TypePriority;

    [Fact]
    public void Loader_runs_after_every_named_load_phase()
    {
        Priority.Should().BeGreaterThan(OnLoadOrder.PostLoad);
    }

    [Theory]
    // SptQuestLive's two entry points, the concrete conflict this ordering exists for.
    [InlineData(OnLoadOrder.PostLoad + 1)]
    [InlineData(OnLoadOrder.PostLoad + 2)]
    // Headroom for other mods that park themselves just past PostLoad.
    [InlineData(OnLoadOrder.PostLoad + 1000)]
    [InlineData(OnLoadOrder.PostLoad + 10000)]
    public void Loader_runs_after_mods_that_overwrite_quests_late(int otherModPriority)
    {
        Priority.Should().BeGreaterThan(otherModPriority);
    }

    [Fact]
    public void Loader_does_not_add_items_so_it_is_not_bound_by_the_preload_cutoff()
    {
        // SPT's DatabaseIntegrityService snapshots TemplateTable.Items once profiles load and throws
        // if anything appeared afterwards, which forces item-adding mods into Preload. This mod only
        // edits existing quests, so it is free to run last — and this test records why, since the
        // priority above would otherwise look like something that must be wrong.
        typeof(AddMissingQuestRequirementsLoader)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().NotContain(
                m => m.Name.Contains("AddItem", StringComparison.OrdinalIgnoreCase),
                "the loader must never add item templates, or it would need to run at Preload");
    }
}
