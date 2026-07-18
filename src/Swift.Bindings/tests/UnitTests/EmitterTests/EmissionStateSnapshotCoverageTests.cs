// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Keeps the two emission-state snapshots honest as the code they mirror grows.
/// </summary>
/// <remarks>
/// <para>
/// A retry of emission only matches a clean run if everything the abandoned attempt mutated is put
/// back first. Both snapshots therefore owe full coverage of the state they capture — and that is
/// exactly the kind of obligation that decays quietly: someone adds a dedup set or an emitted-once
/// latch, nobody remembers the snapshot, and a contained fault starts shifting a collision suffix
/// on some unrelated member. The symptom surfaces nowhere near the field that caused it.
/// </para>
/// <para>
/// Both snapshots walk fields reflectively, so a new field is never skipped outright. The real
/// decay mode is subtler: an unrecognised collection shape falls through to reference-only restore,
/// which puts the same instance back still holding the discarded attempt's entries. These tests
/// pin that boundary structurally, then prove the round trip behaviorally.
/// </para>
/// </remarks>
public class EmissionStateSnapshotCoverageTests
{
    /// <summary>
    /// The structural guard. A field holding entries must get a handler that restores those entries,
    /// not merely the reference to the collection holding them.
    /// </summary>
    [Fact]
    public void EveryCollectionFieldOnTheEmissionContext_GetsAContentRestoringHandler()
    {
        var referenceOnly = ModuleEmissionStateSnapshot.DescribeCoverage()
            .Where(f => !f.RestoresContents && HoldsEntries(f.FieldType))
            .Select(f => $"{f.Field} ({f.FieldType.Name})")
            .ToList();

        Assert.True(
            referenceOnly.Count == 0,
            "These emission-context fields hold entries but are restored by reference only, so a discarded " +
            "attempt's entries survive into the retry and change its output: " + string.Join(", ", referenceOnly));
    }

    /// <summary>Anti-vacuity for the guard above: it must actually be inspecting a populated type.</summary>
    [Fact]
    public void TheCoverageDescriptionSpansTheWholeEmissionContext()
    {
        var described = ModuleEmissionStateSnapshot.DescribeCoverage();
        var actualFields = typeof(ModuleEmissionContext).GetFields(Instance);

        Assert.Equal(actualFields.Length, described.Count);
        Assert.Equal(actualFields.Length, ModuleEmissionStateSnapshot.CoveredFieldCount);
        Assert.Contains(described, f => f.RestoresContents);
    }

    /// <summary>
    /// The behavioral guard. Dirty every field the harness knows how to dirty, restore, and require
    /// the state to come back exactly.
    /// </summary>
    [Fact]
    public void ModuleEmissionStateSnapshot_RestoresEveryFieldItDirties()
    {
        var context = new ModuleEmissionContext();
        var snapshot = ModuleEmissionStateSnapshot.Capture(context);

        var before = DescribeInstanceState(context);
        var dirtied = DirtyEveryField(context);
        Assert.True(dirtied > 20, $"only {dirtied} fields were dirtied; the round trip below would prove little");
        Assert.NotEqual(before, DescribeInstanceState(context));

        snapshot.Restore();

        Assert.Equal(before, DescribeInstanceState(context));
    }

    /// <summary>Restoring twice must be as safe as once — the loop can re-enter after a second fault.</summary>
    [Fact]
    public void ModuleEmissionStateSnapshot_RestoreIsIdempotent()
    {
        var context = new ModuleEmissionContext();
        var snapshot = ModuleEmissionStateSnapshot.Capture(context);
        var before = DescribeInstanceState(context);

        DirtyEveryField(context);
        snapshot.Restore();
        snapshot.Restore();

        Assert.Equal(before, DescribeInstanceState(context));
    }

    /// <summary>
    /// Collections must be restored in place, not replaced. Callers hold <c>IReadOnly*</c> views onto
    /// the very instances these fields point at, so swapping in a fresh collection would leave those
    /// views reading a detached object that never changes again.
    /// </summary>
    [Fact]
    public void ModuleEmissionStateSnapshot_RestoresCollectionsInPlace()
    {
        var context = new ModuleEmissionContext();
        var before = CollectionFieldIdentities(context);
        var snapshot = ModuleEmissionStateSnapshot.Capture(context);

        DirtyEveryField(context);
        snapshot.Restore();

        var after = CollectionFieldIdentities(context);
        foreach (var (name, original) in before)
        {
            Assert.True(
                ReferenceEquals(original, after[name]),
                $"'{name}' was replaced rather than restored in place; IReadOnly views onto it are now dead.");
        }
    }

    /// <summary>
    /// The declaration-tree snapshot carries the same obligation plus a stricter one: it must restore
    /// the very same decl objects, because dictionaries elsewhere key on their reference identity.
    /// </summary>
    [Fact]
    public void DeclEmissionStateSnapshot_RestoresEmissionStampsWithoutReplacingDecls()
    {
        var module = FixtureModuleFactory.BuildModule("SnapshotFixture");
        var snapshot = DeclEmissionStateSnapshot.Capture(module);

        var identities = AllMethods(module).ToList();
        var before = DescribeDeclState(module);

        foreach (var method in AllMethods(module))
        {
            method.WasEmitted = true;
            method.EmittedCSharpName = "Dirtied";
            method.UsesWrapperLibrary = !method.UsesWrapperLibrary;
        }

        Assert.NotEqual(before, DescribeDeclState(module));

        snapshot.Restore();

        Assert.Equal(before, DescribeDeclState(module));
        Assert.Equal(identities, AllMethods(module).ToList());
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>
    /// Whether a field's type carries entries that a retry would see. Strings enumerate but hold no
    /// entries, and the immutable collections cannot be added to in the first place.
    /// </summary>
    private static bool HoldsEntries(Type type) =>
        type != typeof(string)
        && typeof(IEnumerable).IsAssignableFrom(type)
        && type.Namespace != "System.Collections.Immutable";

    /// <summary>
    /// Writes a distinguishable value into every field it can: an entry into each collection, a flip
    /// of each bool, a bump of each number, a suffix on each string. Returns how many it touched.
    /// </summary>
    private static int DirtyEveryField(ModuleEmissionContext context)
    {
        var touched = 0;
        foreach (var field in typeof(ModuleEmissionContext).GetFields(Instance))
        {
            var value = field.GetValue(context);

            switch (value)
            {
                // Dictionaries first — one is also an ICollection of its own key/value pairs, and the
                // dictionary face is the one that takes a key directly.
                case IDictionary dictionary:
                    if (TryDirtyDictionary(field.FieldType, dictionary)) touched++;
                    break;

                // Covers List<T>, HashSet<T> and SortedSet<T> in one arm.
                case ICollection<string> strings: strings.Add(Probe); touched++; break;
                case ICollection<int> ints: ints.Add(ProbeNumber); touched++; break;
                case ICollection<long> longs: longs.Add(ProbeNumber); touched++; break;

                // Stack<T> deliberately exposes no Add.
                case Stack<string> stack: stack.Push(Probe); touched++; break;

                case StringBuilder builder: builder.Append(Probe); touched++; break;

                case bool flag when !field.IsInitOnly: field.SetValue(context, !flag); touched++; break;
                case int number when !field.IsInitOnly: field.SetValue(context, number + ProbeNumber); touched++; break;
                case string text when !field.IsInitOnly: field.SetValue(context, text + Probe); touched++; break;
                case null when !field.IsInitOnly && field.FieldType == typeof(string):
                    field.SetValue(context, Probe); touched++; break;
            }
        }

        return touched;
    }

    private const string Probe = "snapshot-probe";
    private const int ProbeNumber = 7919;

    /// <summary>
    /// Adds one entry to a dictionary whose key shape the harness can synthesise. A reference-typed
    /// value takes null, which is enough to make the dictionary non-empty; a value-typed one refuses
    /// null, so those get a concrete probe or are left alone.
    /// </summary>
    private static bool TryDirtyDictionary(Type fieldType, IDictionary dictionary)
    {
        if (!TryMakeProbe(fieldType, 0, out var key) || key is null)
            return false;

        TryMakeProbe(fieldType, 1, out var item);

        try
        {
            dictionary[key] = item;
            return true;
        }
        catch (ArgumentException)
        {
            // A value-typed value that refuses null. Its field is still covered by the structural
            // guard above; this harness simply cannot populate it.
            return false;
        }
    }

    /// <summary>
    /// Builds a value for the <paramref name="position"/>-th generic argument of a collection type,
    /// for the scalar shapes the emission context actually keys on.
    /// </summary>
    private static bool TryMakeProbe(Type collectionType, int position, out object? probe)
    {
        probe = null;
        if (!collectionType.IsGenericType)
            return false;

        var arguments = collectionType.GetGenericArguments();
        if (position >= arguments.Length)
            return false;

        var argument = arguments[position];
        if (argument == typeof(string)) { probe = Probe; return true; }
        if (argument == typeof(int)) { probe = ProbeNumber; return true; }
        if (argument == typeof(long)) { probe = (long)ProbeNumber; return true; }
        if (argument == typeof(bool)) { probe = true; return true; }

        return false;
    }

    /// <summary>Every field rendered stably — collections by their contents, scalars by value.</summary>
    private static string DescribeInstanceState(ModuleEmissionContext context) =>
        string.Join(
            Environment.NewLine,
            typeof(ModuleEmissionContext).GetFields(Instance)
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .Select(f => $"{f.Name}={Render(f.GetValue(context))}"));

    private static string Render(object? value) => value switch
    {
        null => "<null>",
        string text => text,
        StringBuilder builder => builder.ToString(),
        IDictionary dictionary => RenderDictionary(dictionary),
        IEnumerable sequence => "[" + string.Join(",", sequence.Cast<object?>().Select(Render)) + "]",
        _ => value.ToString() ?? "<null>",
    };

    /// <summary>
    /// Renders a dictionary through <see cref="IDictionaryEnumerator"/>. Enumerating a
    /// <c>Dictionary&lt;K,V&gt;</c> as a plain sequence yields <c>KeyValuePair</c>, not
    /// <c>DictionaryEntry</c>, so casting to the latter throws.
    /// </summary>
    private static string RenderDictionary(IDictionary dictionary)
    {
        var entries = new List<string>(dictionary.Count);
        var enumerator = dictionary.GetEnumerator();
        while (enumerator.MoveNext())
        {
            entries.Add($"{enumerator.Key}={Render(enumerator.Value)}");
        }

        entries.Sort(StringComparer.Ordinal);
        return "{" + string.Join(",", entries) + "}";
    }

    private static Dictionary<string, object?> CollectionFieldIdentities(ModuleEmissionContext context) =>
        typeof(ModuleEmissionContext).GetFields(Instance)
            .Where(f => f.GetValue(context) is IEnumerable and not string)
            .ToDictionary(f => f.Name, f => f.GetValue(context), StringComparer.Ordinal);

    private static IEnumerable<MethodDecl> AllMethods(ModuleDecl module) =>
        module.Methods.Concat(module.Types.SelectMany(t => t.Methods));

    private static string DescribeDeclState(ModuleDecl module) =>
        string.Join(Environment.NewLine, AllMethods(module).Select(m =>
            $"{m.Name}|emitted={m.WasEmitted}|csharp={m.EmittedCSharpName ?? "<null>"}" +
            $"|wrapperLib={m.UsesWrapperLibrary}|args={string.Join(",", m.CSSignature.Select(a => a.Name))}"));
}
