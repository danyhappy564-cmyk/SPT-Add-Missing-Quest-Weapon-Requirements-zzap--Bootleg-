using System.Reflection;
using AddMissingQuestRequirements.Pipeline.Override;
using SPTarkov.Server.Core.Helpers.Server;

namespace AddMissingQuestRequirements.Spt;

/// <summary>
/// Walks <c>user/mods/</c> to enumerate every installed mod directory (self included).
/// <see cref="AddMissingQuestRequirements.Pipeline.Override.OverrideReader"/> silently
/// skips directories without a <c>MissingQuestWeapons/</c> child, so this provider
/// applies no filtering of its own.
/// </summary>
/// <remarks>
/// On SPT 4.0 <c>ModHelper.GetAbsolutePathToModFolder</c> was virtual, so tests could
/// subclass <see cref="ModHelper"/> and override it. 4.1 made it non-virtual, hence the
/// delegate constructor: the resolution step is the only thing that needs faking, and a
/// <see cref="Func{T, TResult}"/> is a smaller seam than an interface nobody else wants.
/// </remarks>
public sealed class SptModDirectoryProvider : IModDirectoryProvider
{
    private readonly Func<Assembly, string> _resolveModFolder;
    private readonly Assembly _ownAssembly;

    public SptModDirectoryProvider(ModHelper modHelper, Assembly ownAssembly)
        : this(modHelper.GetAbsolutePathToModFolder, ownAssembly)
    {
    }

    public SptModDirectoryProvider(Func<Assembly, string> resolveModFolder, Assembly ownAssembly)
    {
        _resolveModFolder = resolveModFolder;
        _ownAssembly = ownAssembly;
    }

    public IEnumerable<string> GetModDirectories()
    {
        var ownPath = _resolveModFolder(_ownAssembly);
        var parent = Path.GetDirectoryName(ownPath);
        if (parent is null)
        {
            throw new InvalidOperationException(
                $"Cannot resolve parent of mod folder '{ownPath}'.");
        }
        return Directory.GetDirectories(parent);
    }
}
