// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BindingsGeneration;

namespace SwiftBindings.TransitionGate;

/// <summary>The negative controls a binding with a recorded bridge withdrawal is doctored into.</summary>
public enum BridgeWithdrawalControl
{
    /// <summary>The withdrawal record's key renamed: every gap it explained is unrecorded.</summary>
    UnrecordedGap,

    /// <summary>One letter of the record's reason changed: the gaps no longer carry the reason it gives.</summary>
    InconsistentRecord,

    /// <summary>A restored import's entry point renamed to a bridge the manifest does not name.</summary>
    DanglingBridgeImport,

    /// <summary>The guard calls renamed: the restored imports run with no guard in front of them.</summary>
    MissingGuard,
}

/// <summary>
/// Doctors a compiled binding whose bridges were withdrawn into one negative control of the
/// withdrawal acceptance rule, at the byte level, so the pack and consumer checks can be shown to
/// refuse it. Every edit keeps each string's length, so the metadata still reads; an edit of the
/// embedded transition manifest rehashes its locator, so the evidence stays authentic and the
/// refusal comes from what the evidence says, not from a broken hash.
/// </summary>
public static class BridgeWithdrawalControls
{
    private const string Key = "bridgeWithdrawal";

    /// <summary>
    /// <paramref name="image"/> doctored into <paramref name="control"/>. Throws when the image
    /// carries no withdrawal, or nothing the control edits.
    /// </summary>
    public static byte[] Doctor(byte[] image, string displayPath, IReadOnlyList<string> referenceDirectories, BridgeWithdrawalControl control)
    {
        var read = SwiftTransitionManifestReader.Read(image, displayPath, new SwiftTransitionScanOptions { ReferenceDirectories = referenceDirectories });
        var key = Encoding.UTF8.GetBytes(Key);
        var manifest = read.Manifests.FirstOrDefault(m => m.Bytes.AsSpan().IndexOf(key) >= 0)
            ?? throw new InvalidOperationException($"{displayPath} embeds no manifest that records a bridge withdrawal.");
        var at = image.AsSpan().IndexOf(manifest.Bytes);
        if (at < 0)
            throw new InvalidOperationException($"{displayPath} does not hold its manifest's bytes verbatim.");
        var withdrawal = BridgeTwinPlans.FromSidecars([("manifest" + BridgeTwinPlans.SidecarSuffix, manifest.Manifest.Plans)]).Withdrawals.FirstOrDefault()
            ?? throw new InvalidOperationException($"{displayPath}: the withdrawal its manifest records does not read.");

        switch (control)
        {
            case BridgeWithdrawalControl.UnrecordedGap:
            {
                var bytes = (byte[])manifest.Bytes.Clone();
                var keyAt = bytes.AsSpan().IndexOf(key);
                bytes[keyAt + key.Length - 1] = (byte)'X';
                return Rehashed(image, at, manifest.Bytes, bytes);
            }
            case BridgeWithdrawalControl.InconsistentRecord:
            {
                var bytes = (byte[])manifest.Bytes.Clone();
                var keyAt = bytes.AsSpan().IndexOf(key);
                var reason = Encoding.UTF8.GetBytes(withdrawal.Reason);
                var reasonAt = bytes.AsSpan(keyAt).IndexOf(reason);
                if (reasonAt < 0)
                    throw new InvalidOperationException($"{displayPath}: the withdrawal's reason is not spelled verbatim after its key.");
                // The first letter of the reason, swapped for another letter of the same width.
                var letter = keyAt + reasonAt + Array.FindIndex(reason, b => b is >= (byte)'a' and <= (byte)'z');
                bytes[letter] = bytes[letter] == (byte)'x' ? (byte)'y' : (byte)'x';
                return Rehashed(image, at, manifest.Bytes, bytes);
            }
            case BridgeWithdrawalControl.DanglingBridgeImport:
            {
                var entryPoint = withdrawal.Transitions[0].EntryPoint;
                if (entryPoint.Length <= SwiftTransitionScanner.BridgePrefix.Length)
                    throw new InvalidOperationException($"{displayPath}: the entry point {entryPoint} is too short to rename into a bridge.");
                var bridge = SwiftTransitionScanner.BridgePrefix
                    + string.Concat(Enumerable.Repeat("0123456789abcdef", entryPoint.Length / 16 + 1))[..(entryPoint.Length - SwiftTransitionScanner.BridgePrefix.Length)];
                return ReplacedOutside(image, NulTerminated(entryPoint), NulTerminated(bridge), at, manifest.Bytes.Length, displayPath);
            }
            case BridgeWithdrawalControl.MissingGuard:
            {
                var copy = (byte[])image.Clone();
                var renamed = 0;
                foreach (var name in new[] { "ThrowIfUnsupported", "EnsureSupported" })
                    renamed += ReplaceAll(copy, NulTerminated(name), NulTerminated("X" + name[1..]), at, manifest.Bytes.Length);
                if (renamed == 0)
                    throw new InvalidOperationException($"{displayPath} names no transition guard to remove.");
                return copy;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(control), control, null);
        }
    }

    /// <summary>The manifest at <paramref name="at"/> replaced by <paramref name="doctored"/>, and its locator's hash by the doctored manifest's.</summary>
    private static byte[] Rehashed(byte[] image, int at, byte[] original, byte[] doctored)
    {
        var copy = (byte[])image.Clone();
        doctored.CopyTo(copy, at);
        static byte[] Hex(byte[] manifest) =>
            Encoding.UTF8.GetBytes(SwiftTransitionManifestFormat.Hash(manifest)[SwiftTransitionManifestFormat.HashPrefix.Length..]);
        if (ReplaceAll(copy, Hex(original), Hex(doctored), at, original.Length) == 0)
            throw new InvalidOperationException("the assembly holds no locator spelling its manifest's hash.");
        return copy;
    }

    private static byte[] NulTerminated(string text) => Encoding.UTF8.GetBytes(text + "\0");

    private static byte[] ReplacedOutside(byte[] image, byte[] from, byte[] to, int skipFrom, int skipLength, string displayPath)
    {
        var copy = (byte[])image.Clone();
        if (ReplaceAll(copy, from, to, skipFrom, skipLength) == 0)
            throw new InvalidOperationException($"{displayPath} holds no {Encoding.UTF8.GetString(from).TrimEnd('\0')} outside its manifest.");
        return copy;
    }

    private static int ReplaceAll(byte[] image, byte[] from, byte[] to, int skipFrom, int skipLength)
    {
        var count = 0;
        for (var at = 0; at <= image.Length - from.Length;)
        {
            var next = image.AsSpan(at).IndexOf(from);
            if (next < 0)
                break;
            var hit = at + next;
            if (hit < skipFrom || hit >= skipFrom + skipLength)
            {
                to.CopyTo(image, hit);
                count++;
            }
            at = hit + from.Length;
        }
        return count;
    }
}
