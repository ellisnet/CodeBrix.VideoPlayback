using System;
using System.Collections.Generic;
using System.Linq;

namespace SimpleCbxVideoPlayer.SkiaVideo.Effects;

/// <summary>
/// The chain of lookup tables that is currently applied, and the change detection around it.
/// </summary>
/// <remarks>
/// Composing an effect chain walks tens of thousands of grid nodes, so the presenter's Effects collection
/// is rebuilt only when the SELECTION, the ORDER or a PERCENTAGE actually changes - never on every
/// keystroke or every frame. <see cref="TrySet" /> is the gate that decides.
/// </remarks>
public sealed class LutChain
{
    private IReadOnlyList<LutChainEntry> entries = [];

    /// <summary>The chain as it stands, in the order the tables are applied.</summary>
    public IReadOnlyList<LutChainEntry> Entries => entries;

    /// <summary>A string that changes exactly when the chain changes.</summary>
    public string Signature { get; private set; } = string.Empty;

    /// <summary>How many times the chain has actually changed.</summary>
    public int ChangeCount { get; private set; }

    /// <summary>Replaces the chain, if the new one differs from the old one.</summary>
    /// <param name="newEntries">The tables to apply, in order; null or empty clears the chain.</param>
    /// <returns>True when the chain changed and the effects have to be rebuilt; false when nothing moved.</returns>
    public bool TrySet(IReadOnlyList<LutChainEntry> newEntries)
    {
        IReadOnlyList<LutChainEntry> replacement = newEntries == null
            ? []
            : newEntries.Where(entry => entry != null).ToList();

        var signature = ComputeSignature(replacement);

        if (string.Equals(signature, Signature, StringComparison.Ordinal)) { return false; }

        entries = replacement;
        Signature = signature;
        ChangeCount++;
        return true;
    }

    /// <summary>Builds the signature of a chain: its files, its order and its percentages.</summary>
    /// <param name="entries">The chain to describe; null counts as empty.</param>
    /// <returns>A string that is equal for two chains exactly when they would render the same picture.</returns>
    public static string ComputeSignature(IReadOnlyList<LutChainEntry> entries)
    {
        if (entries == null || entries.Count == 0) { return string.Empty; }

        return string.Join("|", entries.Where(entry => entry != null).Select(entry => entry.Signature));
    }
}
