// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Linq;
using Xunit;

using BindingsGeneration.Diagnostics;

namespace BindingsGeneration.Tests;

/// <summary>
/// The parser turns real captured swiftc stderr into structured groups. These run against the
/// checked-in captures, so they pin the parser to the compiler's actual textual shape.
/// </summary>
public class SwiftDiagnosticParserTests
{
    [Fact]
    public void Parse_Null_ReturnsEmpty() =>
        Assert.Empty(SwiftDiagnosticParser.Parse(null));

    [Fact]
    public void Parse_Empty_ReturnsEmpty() =>
        Assert.Empty(SwiftDiagnosticParser.Parse(string.Empty));

    /// <summary>
    /// The two errors in this capture both point at line 8, and a "in call to function" note follows
    /// the second. The parser must yield exactly two error primaries and attach the note to a group
    /// rather than counting it as a third failure.
    /// </summary>
    [Fact]
    public void Parse_SingleBrokenMember_YieldsTwoErrorPrimariesAndAttachesTheNote()
    {
        var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr("SingleBrokenMember"));

        var errors = groups.Where(g => g.IsError).ToList();
        Assert.Equal(2, errors.Count);
        Assert.All(errors, g => Assert.Equal(8, g.Primary.Line));
        Assert.Contains(errors, g => g.Primary.Message.Contains("cannot find 'MissingGadgetType'"));

        // The synthetic-location note ("in call to function 'load…'") rode along as evidence, not as
        // an independent group.
        Assert.Contains(groups, g => g.Notes.Any(n => n.Message.Contains("in call to function")));
        Assert.DoesNotContain(groups, g => g.Primary.Severity == DiagnosticSeverity.Note);
    }

    /// <summary>
    /// The gutter lines swiftc draws — <c> 8 |     let gadget = …</c> and the restated
    /// <c>`- error:</c> caret — carry no <c>:line:column:</c> prefix, so none of them become a
    /// diagnostic. Every parsed primary must sit at a real column the compiler reported.
    /// </summary>
    [Fact]
    public void Parse_DropsGutterAndCaretLines()
    {
        var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr("CascadeInOneMember"));

        Assert.Equal(4, groups.Count(g => g.IsError));
        Assert.All(groups, g => Assert.True(g.Primary.Column > 0));
        Assert.All(groups, g => Assert.EndsWith(".wrapper.swift", g.Primary.File));
    }

    [Fact]
    public void Parse_MissingModule_YieldsOnePositionedError()
    {
        var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr("MissingModule"));

        var group = Assert.Single(groups);
        Assert.True(group.Primary.HasPosition);
        Assert.Equal(4, group.Primary.Line);
        Assert.Contains("no such module 'CompletelyFictionalDependency'", group.Primary.Message);
    }

    /// <summary>
    /// A bare driver diagnostic with no tool prefix and no source position — swift-frontend's
    /// argument/configuration failures — is still captured as a global error, so a compile that
    /// fails before it reaches source is visible to attribution (non-zero error count) rather than
    /// silently parsing to nothing.
    /// </summary>
    [Fact]
    public void Parse_BareDriverError_YieldsOneGlobalError()
    {
        var groups = SwiftDiagnosticParser.Parse("error: unknown argument: '-Xfrobnicate'\n");

        var group = Assert.Single(groups);
        Assert.True(group.IsError);
        Assert.False(group.Primary.HasPosition);
        Assert.Contains("unknown argument", group.Primary.Message);
    }

    /// <summary>
    /// The indented caret restatement swiftc draws under a positioned error is not a bare driver
    /// error — it has leading whitespace, so the anchored bare-diagnostic pattern must not double it.
    /// </summary>
    [Fact]
    public void Parse_IndentedCaretRestatement_IsNotCountedAsABareError()
    {
        const string stderr =
            "A.swift:8:14: error: cannot find 'X' in scope\n" +
            "  8 |     let y = X()\n" +
            "    |             `- error: cannot find 'X' in scope\n";

        var groups = SwiftDiagnosticParser.Parse(stderr);

        Assert.Single(groups);   // just the one positioned error, not a second from the caret line
    }

    /// <summary>A note with no primary to attach to is dropped, never promoted to head a group.</summary>
    [Fact]
    public void Parse_NoteWithoutAPrimary_IsDropped()
    {
        var groups = SwiftDiagnosticParser.Parse("A.swift:1:1: note: expanded from macro 'M'\n");

        Assert.Empty(groups);
    }

    /// <summary>Columns are the UTF-8 byte columns swiftc reports, carried through verbatim.</summary>
    [Fact]
    public void Parse_TwoBrokenMembers_KeepsEveryErrorsOwnPosition()
    {
        var groups = SwiftDiagnosticParser.Parse(AttributionFixtures.Stderr("TwoBrokenMembers"));

        var lines = groups.Where(g => g.IsError).Select(g => g.Primary.Line).Distinct().ToList();
        Assert.Contains(7, lines);   // credit
        Assert.Contains(19, lines);  // debit
        Assert.Contains(20, lines);  // debit's undefined argument
    }
}
