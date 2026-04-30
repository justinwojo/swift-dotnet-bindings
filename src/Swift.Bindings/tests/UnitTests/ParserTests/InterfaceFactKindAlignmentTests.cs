// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BindingsGeneration.Producers;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Drift guard: the <see cref="InterfaceFactKind"/> enum and <see cref="SwiftInterfaceFacts"/>
/// must stay 1:1. Adding a fact to the record without adding the matching enum member would
/// silently drop the new fact from any producer's coverage; adding a kind with no matching
/// record property would silently no-op in <see cref="InterfaceFactsAggregator"/>.
/// <para/>
/// Implementation note: we DO NOT freeze the expected fact list here — that's exactly the
/// shape Codex's M2 audit flagged as a non-guard guard. Instead, reflect on
/// <see cref="SwiftInterfaceFacts"/> directly, subtract a small explicit allow-list of
/// known non-fact properties (today: just <c>Empty</c>), and assert the remaining set
/// equals the enum names. Adding a new helper property requires extending
/// <see cref="NonFactProperties"/> deliberately — otherwise the guard fires.
/// </summary>
public class InterfaceFactKindAlignmentTests
{
    /// <summary>Properties on <see cref="SwiftInterfaceFacts"/> that are NOT facts —
    /// e.g. the static <c>Empty</c> singleton. Update deliberately when adding helpers.</summary>
    private static readonly HashSet<string> NonFactProperties = new()
    {
        "Empty",
    };

    [Fact]
    public void InterfaceFactKindAndSwiftInterfaceFactsStayOneToOne()
    {
        var factsProperties = typeof(SwiftInterfaceFacts)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(p => !NonFactProperties.Contains(p.Name))
            .Select(p => p.Name)
            .ToHashSet();

        var kindNames = Enum.GetNames<InterfaceFactKind>().ToHashSet();

        var enumWithoutProperty = kindNames.Except(factsProperties).OrderBy(s => s).ToList();
        var propertyWithoutEnum = factsProperties.Except(kindNames).OrderBy(s => s).ToList();

        Assert.True(
            enumWithoutProperty.Count == 0 && propertyWithoutEnum.Count == 0,
            "InterfaceFactKind ↔ SwiftInterfaceFacts drift detected.\n" +
            $"  Enum values without matching property: [{string.Join(", ", enumWithoutProperty)}]\n" +
            $"  Properties without matching enum value: [{string.Join(", ", propertyWithoutEnum)}]\n" +
            "If a new helper property is intentional, add it to NonFactProperties in this test.");
    }
}
