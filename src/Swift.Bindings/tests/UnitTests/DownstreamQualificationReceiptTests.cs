// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class DownstreamQualificationReceiptTests
{
    private static readonly string FixtureDirectory = Path.Combine(
        FindRepoRoot(), "src", "Swift.Bindings", "tests", "UnitTests", "TestData",
        "DownstreamQualificationReceipt");

    [Fact]
    public void ZeroMatchReceipt_IsValidAndCarriesOneScalarZeroPerCounter()
    {
        var output = File.ReadAllLines(Fixture("zero-match-output.txt"));
        var legacyShellValues = new[] { output.Count(line => line.Contains("[PASS]", StringComparison.Ordinal)), 0 };
        Assert.Equal(new[] { 0, 0 }, legacyShellValues); // grep -c prints 0, then `|| echo 0` prints another 0.

        var receipt = DownstreamQualificationReceipt.Load(Fixture("zero-match.jsonl"));

        var cell = Assert.Single(receipt.Cells);
        Assert.Equal(0, cell.TestsPassed);
        Assert.Equal(0, cell.TestsFailed);
        Assert.True(receipt.Passed);
    }

    [Fact]
    public void PositiveMatchReceipt_IsValid()
    {
        var markerCount = File.ReadLines(Fixture("positive-match-output.txt"))
            .Count(line => line.Contains("[PASS]", StringComparison.Ordinal));
        var receipt = DownstreamQualificationReceipt.Load(Fixture("positive-match.jsonl"));

        Assert.Equal(2, markerCount);
        Assert.Equal(markerCount, Assert.Single(receipt.Cells).TestsPassed);
        Assert.True(receipt.Passed);
    }

    [Theory]
    [InlineData("malformed.jsonl", "malformed JSON")]
    [InlineData("incomplete.jsonl", "terminal record is missing")]
    [InlineData("duplicate-terminal.jsonl", "duplicate terminal record")]
    public void InvalidReceiptFixtures_FailClosed(string fixtureName, string expectedMessage)
    {
        var exception = Assert.Throws<InvalidDataException>(
            () => DownstreamQualificationReceipt.Load(Fixture(fixtureName)));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalCountersAndOutcomeMustBeDerivedFromCells()
    {
        const string inconsistent = """
            {"kind":"cell","schema_version":1,"platform":"simulator","library":"A","outcome":"fail","tests_passed":0,"tests_failed":1}
            {"kind":"terminal","schema_version":1,"platform":"simulator","total":1,"pass":1,"fail":0,"crash":0,"skip":0,"outcome":"pass"}
            """;

        Assert.Throws<InvalidDataException>(() => DownstreamQualificationReceipt.Parse(inconsistent));
    }

    [Fact]
    public void PassingCellCannotHideFailedTestMarkers()
    {
        const string hiddenFailure = """
            {"kind":"cell","schema_version":1,"platform":"simulator","library":"A","outcome":"pass","tests_passed":1,"tests_failed":1}
            {"kind":"terminal","schema_version":1,"platform":"simulator","total":1,"pass":1,"fail":0,"crash":0,"skip":0,"outcome":"pass"}
            """;

        Assert.Throws<InvalidDataException>(() => DownstreamQualificationReceipt.Parse(hiddenFailure));
    }

    [Fact]
    public void CellAfterTerminalFailsClosed()
    {
        const string outOfOrder = """
            {"kind":"terminal","schema_version":1,"platform":"simulator","total":1,"pass":1,"fail":0,"crash":0,"skip":0,"outcome":"pass"}
            {"kind":"cell","schema_version":1,"platform":"simulator","library":"A","outcome":"pass","tests_passed":1,"tests_failed":0}
            """;

        Assert.Throws<InvalidDataException>(() => DownstreamQualificationReceipt.Parse(outOfOrder));
    }

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private static string FindRepoRoot()
    {
        string? directory = AppDomain.CurrentDomain.BaseDirectory;
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
                return directory;
            directory = Path.GetDirectoryName(directory);
        }
        throw new InvalidOperationException("Cannot find repository root.");
    }
}
