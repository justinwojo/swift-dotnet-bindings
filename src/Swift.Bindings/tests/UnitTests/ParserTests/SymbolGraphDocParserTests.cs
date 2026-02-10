// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.IO;
using System.Text.Json;
using Xunit;

namespace BindingsGeneration.Tests;

public class SymbolGraphDocParserTests
{
    [Fact]
    public void ParseDocCommentLines_SummaryOnly()
    {
        var lines = new List<string> { "This is a summary.", "It spans two lines." };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("This is a summary. It spans two lines.", result.Summary);
        Assert.Empty(result.Parameters);
        Assert.Null(result.Returns);
        Assert.Null(result.Throws);
        Assert.Empty(result.Remarks);
    }

    [Fact]
    public void ParseDocCommentLines_SingularParameter()
    {
        var lines = new List<string>
        {
            "Encodes a value.",
            "",
            "- Parameter value: The value to encode."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("Encodes a value.", result.Summary);
        Assert.Single(result.Parameters);
        Assert.Equal("The value to encode.", result.Parameters["value"]);
    }

    [Fact]
    public void ParseDocCommentLines_PluralParameters()
    {
        var lines = new List<string>
        {
            "Combines two values.",
            "",
            "- Parameters:",
            "  - left: The left operand.",
            "  - right: The right operand."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("Combines two values.", result.Summary);
        Assert.Equal(2, result.Parameters.Count);
        Assert.Equal("The left operand.", result.Parameters["left"]);
        Assert.Equal("The right operand.", result.Parameters["right"]);
    }

    [Fact]
    public void ParseDocCommentLines_Returns()
    {
        var lines = new List<string>
        {
            "Gets the count.",
            "",
            "- Returns: The number of elements."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("The number of elements.", result.Returns);
    }

    [Fact]
    public void ParseDocCommentLines_Throws()
    {
        var lines = new List<string>
        {
            "Parses input.",
            "",
            "- Throws: `ParseError` if input is invalid."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("`ParseError` if input is invalid.", result.Throws);
    }

    [Fact]
    public void ParseDocCommentLines_RemarkDirectives()
    {
        var lines = new List<string>
        {
            "Does something.",
            "",
            "- Note: This is a note.",
            "- Important: Pay attention.",
            "- Warning: Be careful.",
            "- Complexity: O(n)"
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal(4, result.Remarks.Count);
        Assert.Equal("Note: This is a note.", result.Remarks[0]);
        Assert.Equal("Important: Pay attention.", result.Remarks[1]);
        Assert.Equal("Warning: Be careful.", result.Remarks[2]);
        Assert.Equal("Complexity: O(n)", result.Remarks[3]);
    }

    [Fact]
    public void ParseDocCommentLines_MultiLineContinuation()
    {
        var lines = new List<string>
        {
            "Summary.",
            "",
            "- Parameter value: The value",
            "  to be processed.",
            "- Returns: The result",
            "  after processing."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("The value to be processed.", result.Parameters["value"]);
        Assert.Equal("The result after processing.", result.Returns);
    }

    [Fact]
    public void ParseDocCommentLines_EmptyInput()
    {
        var result = SymbolGraphDocParser.ParseDocCommentLines(new List<string>());
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void ParseDocCommentLines_MixedFormat()
    {
        var lines = new List<string>
        {
            "Converts a value to a string.",
            "",
            "- Parameter value: The value to convert.",
            "- Returns: A string representation.",
            "- Throws: `ConversionError` on failure.",
            "- Note: Uses UTF-8 encoding."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("Converts a value to a string.", result.Summary);
        Assert.Equal("The value to convert.", result.Parameters["value"]);
        Assert.Equal("A string representation.", result.Returns);
        Assert.Equal("`ConversionError` on failure.", result.Throws);
        Assert.Single(result.Remarks);
        Assert.Equal("Note: Uses UTF-8 encoding.", result.Remarks[0]);
    }

    [Fact]
    public void ParseDocCommentLines_DirectiveWithoutBlankLine()
    {
        // Directive right after summary (no blank line separator)
        var lines = new List<string>
        {
            "Summary text.",
            "- Parameter x: The input."
        };
        var result = SymbolGraphDocParser.ParseDocCommentLines(lines);
        Assert.Equal("Summary text.", result.Summary);
        Assert.Equal("The input.", result.Parameters["x"]);
    }

    [Fact]
    public void ParseSymbolGraphs_SingleFile()
    {
        var json = CreateSymbolGraphJson("s:TestUSR", "A test function.", new[] { "A test function." });
        var path = WriteTempFile(json, ".symbols.json");
        try
        {
            var result = SymbolGraphDocParser.ParseSymbolGraphs(path);
            Assert.Single(result);
            Assert.True(result.ContainsKey("s:TestUSR"));
            Assert.Equal("A test function.", result["s:TestUSR"].Summary);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParseSymbolGraphs_DirectoryMerge()
    {
        var dir = Path.Combine(Path.GetTempPath(), "symbolgraph_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "Module.symbols.json"),
                CreateSymbolGraphJson("s:USR1", "First.", new[] { "First." }));
            File.WriteAllText(Path.Combine(dir, "Module@Extension.symbols.json"),
                CreateSymbolGraphJson("s:USR2", "Second.", new[] { "Second." }));

            var result = SymbolGraphDocParser.ParseSymbolGraphs(dir);
            Assert.Equal(2, result.Count);
            Assert.True(result.ContainsKey("s:USR1"));
            Assert.True(result.ContainsKey("s:USR2"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParseSymbolGraphs_DuplicateUSR_FirstNonEmptyWins()
    {
        var dir = Path.Combine(Path.GetTempPath(), "symbolgraph_dup_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.symbols.json"),
                CreateSymbolGraphJson("s:USR1", "First version.", new[] { "First version." }));
            File.WriteAllText(Path.Combine(dir, "B.symbols.json"),
                CreateSymbolGraphJson("s:USR1", "Second version.", new[] { "Second version." }));

            var result = SymbolGraphDocParser.ParseSymbolGraphs(dir);
            Assert.Single(result);
            // Files processed in sorted order: A.symbols.json first
            Assert.Equal("First version.", result["s:USR1"].Summary);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static string CreateSymbolGraphJson(string usr, string docText, string[] lines)
    {
        var lineObjects = string.Join(",\n", lines.Select(l => $"{{ \"text\": \"{EscapeJson(l)}\" }}"));
        return $$"""
        {
            "metadata": {},
            "module": { "name": "TestModule" },
            "symbols": [
                {
                    "identifier": {
                        "precise": "{{usr}}"
                    },
                    "docComment": {
                        "lines": [
                            {{lineObjects}}
                        ]
                    }
                }
            ],
            "relationships": []
        }
        """;
    }

    private static string EscapeJson(string text)
    {
        return text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string WriteTempFile(string content, string extension = ".json")
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }
}
