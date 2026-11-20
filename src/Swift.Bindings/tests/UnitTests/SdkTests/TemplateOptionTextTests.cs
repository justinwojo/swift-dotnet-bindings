// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The template's help text names its own options. <c>dotnet new</c> spells a parameter symbol's
/// long option exactly as the symbol is named (<c>--appleFramework</c>), so a description naming
/// any other spelling (<c>--apple-framework</c>) sends a reader to an option that fails with
/// "not a valid option".
/// </summary>
public class TemplateOptionTextTests
{
    [Fact]
    public void EveryOptionTheHelpTextNames_IsAParameterOfTheTemplate()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var path = Path.Combine(root!, "src", "Swift.Bindings.Templates", "content", "swift-binding", ".template.config", "template.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var symbols = document.RootElement.GetProperty("symbols");
        var parameters = symbols.EnumerateObject()
            .Where(symbol => symbol.Value.TryGetProperty("type", out var type) && type.GetString() == "parameter")
            .Select(symbol => symbol.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("appleFramework", parameters);

        var named = new List<string>();
        foreach (var symbol in symbols.EnumerateObject())
        {
            if (!symbol.Value.TryGetProperty("description", out var description))
                continue;
            foreach (Match option in Regex.Matches(description.GetString() ?? string.Empty, @"--(?<name>[A-Za-z][\w-]*)"))
            {
                named.Add(option.Value);
                Assert.True(parameters.Contains(option.Groups["name"].Value),
                    $"'{symbol.Name}' help names {option.Value}, which is not an option of the template ({string.Join(", ", parameters.Select(p => "--" + p))})");
            }
        }
        Assert.NotEmpty(named);
    }
}
