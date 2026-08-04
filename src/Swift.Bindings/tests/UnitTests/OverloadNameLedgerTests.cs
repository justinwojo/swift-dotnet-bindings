// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// The model under test carries its own `#nullable enable`, so the optional-argument annotations
// below are opted into locally rather than inherited from this project's Nullable=disable.
#nullable enable

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for <see cref="OverloadNameLedger"/> — the decision model behind <c>nuke binding-tests
/// --compile-only</c>'s overload-name gate.
///
/// <para>The gate reads two lanes out of one artifact and judges them under opposite policies. An
/// OVERLOAD name that is the natural name plus digits is a policy breach: the resolver is supposed
/// to derive names from Swift argument labels or parameter types and refuse a family neither can
/// separate, never number it. A CASE-ONLY name that is the natural name plus digits is that arm's
/// designed output: two Swift spellings differing only by case carry no labels and no parameter
/// types, so there is nothing to name them by. The tests below pin both halves, plus the two
/// positive controls that keep the gate from passing vacuously when a lane stops reaching the
/// artifact at all.</para>
/// </summary>
public class OverloadNameLedgerTests
{
    private static OverloadRenameRecord Overload(string natural, string emitted, string scheme = "LabelDerived")
        => new()
        {
            DeclaringName = "Widget",
            SwiftSignature = $"{natural.ToLowerInvariant()}()",
            NaturalName = natural,
            EmittedName = emitted,
            Scheme = scheme,
        };

    private static CaseOnlyRenameRecord CaseOnly(string swiftName, string natural, string emitted)
        => new()
        {
            DeclaringName = "EndpointSettings",
            SwiftName = swiftName,
            NaturalName = natural,
            EmittedName = emitted,
            Scheme = "CaseOnlyMemberCollision",
        };

    /// <summary>A ledger whose non-subject lane is populated, so only the axis under test can fail.</summary>
    private static OverloadNameLedgerVerdict Judge(
        IEnumerable<OverloadRenameRecord>? overloads = null,
        IEnumerable<CaseOnlyRenameRecord>? caseOnly = null)
        => OverloadNameLedger.Evaluate(
            (overloads ?? new[] { Overload("Configure", "ConfigureMode") }).ToList(),
            (caseOnly ?? new[] { CaseOnly("URL", "Url", "Url2") }).ToList());

    // ---- The overload lane: numeric is a breach --------------------------------------------

    [Fact]
    public void NumericOverloadAssignment_FailsAndIsNamed()
    {
        var verdict = Judge(overloads: new[] { Overload("Configure", "Configure2") });

        Assert.False(verdict.Passed);
        var offender = Assert.Single(verdict.NumericOverloadAssignments);
        Assert.Equal("Configure2", offender.EmittedName);
        Assert.Contains(verdict.Failures, f => f.Contains("numeric suffix"));
    }

    [Fact]
    public void MultiDigitOverloadAssignment_Fails()
    {
        // The suffix is a rank, not a single character — a family of eleven reaches `Configure11`.
        var verdict = Judge(overloads: new[] { Overload("Configure", "Configure11") });

        Assert.False(verdict.Passed);
        Assert.Single(verdict.NumericOverloadAssignments);
    }

    [Fact]
    public void SemanticOverloadAssignment_Passes()
    {
        var verdict = Judge(overloads: new[]
        {
            Overload("Configure", "ConfigureWithMode"),
            Overload("Add", "AddString"),
        });

        Assert.True(verdict.Passed);
        Assert.Empty(verdict.NumericOverloadAssignments);
    }

    [Theory]
    [InlineData("Vector3")]
    [InlineData("Utf8")]
    [InlineData("Sha256")]
    public void AuthorsOwnNumberedName_IsNotAnAssignment(string name)
    {
        // Both names come from the same record, so a name that merely ENDS in a digit is caught by
        // the equality rather than by the digit: natural == emitted, nothing was assigned. This is
        // exactly what a check over emitted identifiers could not do.
        var verdict = Judge(overloads: new[] { Overload(name, name) });

        Assert.True(verdict.Passed);
        Assert.Empty(verdict.NumericOverloadAssignments);
    }

    [Fact]
    public void ShorterEmittedName_IsNotAnAssignment()
    {
        // Not a prefix relationship at all — a record whose emitted name is shorter than its natural
        // one says nothing about numbering, and must not be read as digits.
        var verdict = Judge(overloads: new[] { Overload("ConfigureMode", "Configure") });

        Assert.True(verdict.Passed);
    }

    // ---- The case-only lane: numeric is the design ------------------------------------------

    [Fact]
    public void NumericCaseOnlyAssignment_IsReportedNotFailed()
    {
        var verdict = Judge(caseOnly: new[] { CaseOnly("URL", "Url", "Url2") });

        Assert.True(verdict.Passed);
        Assert.Empty(verdict.NumericOverloadAssignments);
        var reported = Assert.Single(verdict.NumericCaseOnlyAssignments);
        Assert.Equal("Url2", reported.EmittedName);
        Assert.Equal("URL", reported.SwiftName);
    }

    [Fact]
    public void CaseOnlyLane_DoesNotDiluteTheOverloadVerdict()
    {
        // A numeric case-only record sitting alongside a numeric overload record must not make the
        // breach any less of one — the lanes are judged independently.
        var verdict = Judge(
            overloads: new[] { Overload("Configure", "Configure2") },
            caseOnly: new[] { CaseOnly("URL", "Url", "Url2") });

        Assert.False(verdict.Passed);
        Assert.Single(verdict.NumericOverloadAssignments);
        Assert.Single(verdict.NumericCaseOnlyAssignments);
    }

    [Fact]
    public void AdoptedCaseOnlyName_CountsInTheLaneEvenIfNotNumeric()
    {
        // A conformer adopting a protocol's already-settled name is a rename too. It belongs in the
        // lane's total; it just isn't part of the numeric subset.
        var verdict = Judge(caseOnly: new[]
        {
            CaseOnly("URL", "Url", "Url2"),
            CaseOnly("identifier", "Identifier", "IdentifierValue"),
        });

        Assert.True(verdict.Passed);
        Assert.Equal(2, verdict.CaseOnlyAssignments.Count);
        Assert.Single(verdict.NumericCaseOnlyAssignments);
    }

    // ---- Positive controls -------------------------------------------------------------------

    [Fact]
    public void EmptyOverloadLane_Fails()
    {
        var verdict = Judge(overloads: new OverloadRenameRecord[0]);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("ZERO overload-disambiguation"));
    }

    [Fact]
    public void EmptyCaseOnlyLane_Fails()
    {
        // The lane the fix added needs its own control: without it, a regression that stops
        // recording case-only renames would leave the gate green and the numeric names on the
        // public surface invisible again — the exact state this channel exists to end.
        var verdict = Judge(caseOnly: new CaseOnlyRenameRecord[0]);

        Assert.False(verdict.Passed);
        Assert.Contains(verdict.Failures, f => f.Contains("ZERO case-only"));
    }

    [Fact]
    public void BothLanesPopulatedAndSemantic_Passes()
    {
        var verdict = Judge();

        Assert.True(verdict.Passed);
        Assert.Empty(verdict.Failures);
    }

    // ---- Reading the artifact ----------------------------------------------------------------

    [Fact]
    public void Parse_ReadsBothLanesFromAReportShapedDocument()
    {
        const string json = """
        {
          "ModuleName": "TestLib",
          "OverloadRenames": [
            { "DeclaringName": "Widget", "SwiftSignature": "configure(mode:)",
              "NaturalName": "Configure", "EmittedName": "ConfigureMode", "Scheme": "LabelDerived" }
          ],
          "CaseOnlyRenames": [
            { "DeclaringName": "EndpointSettings", "SwiftName": "URL",
              "NaturalName": "Url", "EmittedName": "Url2", "Scheme": "CaseOnlyMemberCollision" }
          ]
        }
        """;

        var document = OverloadNameLedger.Parse(json);

        Assert.Equal("ConfigureMode", Assert.Single(document.OverloadRenames!).EmittedName);
        var caseOnly = Assert.Single(document.CaseOnlyRenames!);
        Assert.Equal("URL", caseOnly.SwiftName);
        Assert.Equal("Url2", caseOnly.EmittedName);
    }

    [Fact]
    public void Parse_ReportWithoutTheCaseOnlySection_YieldsAnEmptyLaneNotACrash()
    {
        // A report from before the channel existed still has to parse; the positive control above
        // is what turns its empty lane into a loud failure, rather than a deserialization crash
        // that says nothing about why.
        const string json = """
        {
          "OverloadRenames": [
            { "DeclaringName": "Widget", "SwiftSignature": "configure(mode:)",
              "NaturalName": "Configure", "EmittedName": "ConfigureMode", "Scheme": "LabelDerived" }
          ]
        }
        """;

        var document = OverloadNameLedger.Parse(json);

        Assert.Single(document.OverloadRenames!);
        Assert.Null(document.CaseOnlyRenames);
    }
}
