// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the async View pattern manifest sidecar loader. A manifest changes what the
/// generator emits, so the contract under test is twofold: a well-formed entry must survive
/// the crossing intact, and anything the loader cannot make sense of must be dropped with a
/// warning rather than thrown — developer input should be readable, not fatal.
/// </summary>
public class AsyncPatternManifestLoaderTests : IDisposable
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger("Test");

    private readonly string _tempDir;

    public AsyncPatternManifestLoaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"AsyncPatternManifestTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    #region Helpers

    /// <summary>A complete, well-formed pattern. <paramref name="extraFields"/> is spliced in
    /// ahead of the required keys so a test can add or override one field without restating
    /// the whole descriptor.</summary>
    private static string ValidPattern(string viewName = "PayloadView", string extraFields = "")
        => $$"""
            {
              {{extraFields}}
              "moduleName": "TestModule",
              "viewName": "{{viewName}}",
              "sessionClassName": "SBW_TestModule_{{viewName}}_Session",
              "sessionFields": [
                { "name": "monitor", "swiftType": "PayloadMonitor" }
              ],
              "flattenedParams": [
                { "name": "label", "kind": "String", "swiftAbiType": "String", "csharpPInvokeType": "IntPtr" },
                {
                  "name": "preferFastPath", "kind": "Bool", "swiftAbiType": "Int32",
                  "csharpPInvokeType": "int", "swiftConversion": "!= 0", "csharpConversion": "? 1 : 0",
                  "defaultValue": false
                }
              ],
              "constructionChain": [
                {
                  "variableName": "monitor", "swiftTypeName": "PayloadMonitor",
                  "isAsync": true, "throws": false, "factoryMethod": "make",
                  "args": [ { "paramLabel": "label", "kind": "FlattenedParam", "value": "label" } ]
                }
              ],
              "resultCallback": {
                "sourceFieldName": "monitor",
                "awaitMethodName": "result",
                "resultCases": [
                  { "swiftCase": "completed", "code": 0, "carriesPayload": true },
                  { "swiftCase": "cancelled", "code": 2 }
                ],
                "payload": {
                  "kind": "Class",
                  "swiftTypeName": "ClassPayload",
                  "csharpTypeName": "global::TestModule.ClassPayload"
                }
              },
              "viewInitArgs": [
                { "paramLabel": "monitor", "kind": "ChainReference", "value": "monitor" }
              ]
            }
            """;

    private string WriteManifest(string patternsJson)
    {
        var path = Path.Combine(_tempDir, $"manifest_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, $$"""{ "patterns": [ {{patternsJson}} ] }""");
        return path;
    }

    private IReadOnlyDictionary<string, AsyncViewPattern> LoadPatterns(string patternsJson)
        => AsyncPatternManifestLoader.Load(WriteManifest(patternsJson), Log);

    #endregion

    #region No manifest at all

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoPathSupplied_LoadsNothing(string path)
    {
        // The flag being absent has to mean "behave exactly as before", so the loader's answer
        // for an unsupplied path must be indistinguishable from having no patterns at all.
        Assert.Null(AsyncPatternManifestLoader.Load(path, Log));
    }

    [Fact]
    public void MissingFile_IsReportedRatherThanThrown()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.json");

        Assert.Null(AsyncPatternManifestLoader.Load(path, Log));
    }

    [Fact]
    public void MalformedJson_IsReportedRatherThanThrown()
    {
        var path = Path.Combine(_tempDir, "broken.json");
        File.WriteAllText(path, "{ this is not json ");

        Assert.Null(AsyncPatternManifestLoader.Load(path, Log));
    }

    [Fact]
    public void ManifestWithNoPatterns_LoadsNothing()
    {
        var path = Path.Combine(_tempDir, "empty.json");
        File.WriteAllText(path, """{ "patterns": [] }""");

        Assert.Null(AsyncPatternManifestLoader.Load(path, Log));
    }

    #endregion

    #region A well-formed entry survives the crossing

    [Fact]
    public void ValidPattern_IsKeyedByModuleAndViewName()
    {
        var patterns = LoadPatterns(ValidPattern());

        Assert.NotNull(patterns);
        Assert.Equal(new[] { "TestModule.PayloadView" }, patterns.Keys);
    }

    [Fact]
    public void ValidPattern_CarriesItsDescriptorThroughIntact()
    {
        var pattern = LoadPatterns(ValidPattern())["TestModule.PayloadView"];

        Assert.Equal("PayloadView", pattern.ViewName);
        Assert.Equal("SBW_TestModule_PayloadView_Session", pattern.SessionClassName);
        Assert.Equal(new[] { "monitor" }, pattern.SessionFields.Select(f => f.Name));
        Assert.Equal(new[] { "label", "preferFastPath" }, pattern.FlattenedParams.Select(p => p.Name));
        Assert.Single(pattern.ConstructionChain);
        Assert.True(pattern.ConstructionChain[0].IsAsync);
        Assert.Equal(new[] { "monitor" }, pattern.ViewInitArgs!.Select(a => a.Value));
    }

    [Fact]
    public void BoolDefault_CrossesAsADeclaredValueNotAsAbsence()
    {
        // `false` and "no default declared" are different states — one emits `= false` at the
        // C# call site, the other keeps the parameter required — so the slot has to distinguish
        // them rather than collapsing a declared `false` into null.
        var pattern = LoadPatterns(ValidPattern())["TestModule.PayloadView"];

        var declared = pattern.FlattenedParams.Single(p => p.Name == "preferFastPath");
        var undeclared = pattern.FlattenedParams.Single(p => p.Name == "label");

        Assert.False(declared.DefaultValue);
        Assert.Null(undeclared.DefaultValue);
    }

    [Fact]
    public void ResultPayload_CrossesWithItsOwnershipKind()
    {
        // The kind picks the whole ownership protocol on both sides of the callback, so it is
        // the one field a manifest cannot be allowed to lose in translation.
        var pattern = LoadPatterns(ValidPattern())["TestModule.PayloadView"];

        var payload = pattern.ResultCallback!.Payload;
        Assert.NotNull(payload);
        Assert.Equal(AsyncResultPayloadKind.Class, payload.Kind);
        Assert.Equal("global::TestModule.ClassPayload", payload.CSharpTypeName);

        var completed = pattern.ResultCallback.ResultCases.Single(c => c.SwiftCase == "completed");
        var cancelled = pattern.ResultCallback.ResultCases.Single(c => c.SwiftCase == "cancelled");
        Assert.True(completed.CarriesPayload);
        Assert.False(cancelled.CarriesPayload);
        Assert.Equal(2, cancelled.Code);
    }

    [Theory]
    [InlineData("Class", AsyncResultPayloadKind.Class)]
    [InlineData("Struct", AsyncResultPayloadKind.Struct)]
    public void PayloadKind_IsReadFromTheManifest(string kind, AsyncResultPayloadKind expected)
    {
        var json = ValidPattern().Replace("\"kind\": \"Class\"", $"\"kind\": \"{kind}\"");

        var pattern = LoadPatterns(json)["TestModule.PayloadView"];

        Assert.Equal(expected, pattern.ResultCallback!.Payload!.Kind);
    }

    #endregion

    #region Anything unusable is dropped, not thrown

    [Fact]
    public void NullPatternEntry_IsSkippedWithoutLosingItsSiblings()
    {
        // JSON permits a bare `null` wherever an object is expected, so one typo must not cost
        // the reader every other pattern in the file.
        var patterns = LoadPatterns($"null, {ValidPattern()}");

        Assert.NotNull(patterns);
        Assert.Equal(new[] { "TestModule.PayloadView" }, patterns.Keys);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void BlankExtraSwiftImport_DropsThePattern(string importJson)
    {
        // A blank import reaches the bridge emitter as a bare `import` line, which is Swift that
        // does not compile — better to lose the pattern here than to emit unbuildable source.
        var json = ValidPattern(extraFields: $"\"extraSwiftImports\": [ {importJson} ],");

        Assert.Null(LoadPatterns(json));
    }

    [Fact]
    public void UsableExtraSwiftImports_AreKept()
    {
        var json = ValidPattern(extraFields: "\"extraSwiftImports\": [ \"Combine\" ],");

        var pattern = LoadPatterns(json)["TestModule.PayloadView"];

        Assert.Equal(new[] { "Combine" }, pattern.ExtraSwiftImports);
    }

    [Theory]
    [InlineData("\"sessionClassName\"")]
    [InlineData("\"viewName\"")]
    [InlineData("\"moduleName\"")]
    public void EntryMissingARequiredName_IsDropped(string requiredKey)
    {
        // Blank out the key rather than deleting it, so the test exercises the loader's own
        // validation instead of relying on the deserializer to leave the property null.
        var json = ValidPattern().Replace($"{requiredKey}:", "\"unused\":");

        Assert.Null(LoadPatterns(json));
    }

    [Fact]
    public void EntryWithAnIncompleteSessionField_IsDropped()
    {
        var json = ValidPattern().Replace(
            "{ \"name\": \"monitor\", \"swiftType\": \"PayloadMonitor\" }",
            "{ \"name\": \"monitor\" }");

        Assert.Null(LoadPatterns(json));
    }

    [Fact]
    public void OneUnusableEntry_DoesNotTakeTheUsableOnesWithIt()
    {
        var broken = ValidPattern(viewName: "BrokenView").Replace("\"sessionClassName\":", "\"unused\":");

        var patterns = LoadPatterns($"{broken}, {ValidPattern(viewName: "GoodView")}");

        Assert.NotNull(patterns);
        Assert.Equal(new[] { "TestModule.GoodView" }, patterns.Keys);
    }

    [Fact]
    public void DuplicateKey_KeepsTheFirstEntry()
    {
        var first = ValidPattern();
        var second = ValidPattern().Replace("\"monitor\"", "\"laterMonitor\"");

        var patterns = LoadPatterns($"{first}, {second}");

        Assert.NotNull(patterns);
        var pattern = Assert.Single(patterns).Value;
        Assert.Equal(new[] { "monitor" }, pattern.SessionFields.Select(f => f.Name));
    }

    #endregion
}
