// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

public class SurfaceSyntaxScannerTests
{
    [Fact]
    public void Scanner_UsesFullOwnersPlatformAndGenericShape()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "surface.cs", """
            using Alias = System.Collections.Generic.List<int>;
            namespace Example;

            public interface IThing { void Run(); }
            public partial class Outer<T>
            {
                public sealed class Visible
                {
                    public Alias? Convert<U>(ref T value, U optional = default!) => null;
                }
                private sealed class Hidden { public void Leaked() { } }
                void IThing.Run() { }
            #if INCLUDED
                public void Included() { }
            #else
                public void Excluded() { }
            #endif
            }
            """);

        var key = new SurfaceTargetKey("Lib", "Mod", "ios", "Mod");
        var scan = SurfaceSyntaxScanner.Scan("capture", key, "swift-managed", "Mod", [source], ["INCLUDED"], fixture.Root);

        var convert = Assert.Single(scan.Members, m => m.PublicKey.Name == "Convert");
        Assert.Equal(new[] { "Outer`1", "Visible" }, convert.PublicKey.EnclosingTypes);
        Assert.Equal("System.Collections.Generic.List<int>?", convert.PublicShape.Type);
        Assert.Equal("T0", convert.PublicKey.Parameters[0].Type);
        Assert.Equal("ref", convert.PublicKey.Parameters[0].Modifier);
        Assert.Equal("T1", convert.PublicKey.Parameters[1].Type);
        Assert.True(convert.PublicShape.ParameterDefaults[1].Optional);
        Assert.Contains(scan.Members, m => m.PublicKey.Name == "Included");
        Assert.DoesNotContain(scan.Members, m => m.PublicKey.Name is "Excluded" or "Leaked");
        Assert.Contains(scan.Members, m => m.PublicKey.Name == "Run"
            && m.PublicKey.ExplicitInterface == "IThing"
            && m.PublicShape.Accessibility == "explicit-interface");

        var macKey = key with { Platform = "macos", TargetName = "Mod@macos" };
        var mac = SurfaceSyntaxScanner.Scan("capture", macKey, "swift-managed", "Mod", [source], ["INCLUDED"], fixture.Root);
        Assert.NotEqual(convert.ObservationId, mac.Members.Single(m => m.PublicKey.Name == "Convert").ObservationId);
    }

    [Fact]
    public void Scanner_ConversionDestinationParticipatesInPublicKey()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "conversions.cs", """
            namespace Example;
            public readonly struct Value
            {
                public static implicit operator int(Value value) => 0;
                public static implicit operator long(Value value) => 0;
            }
            """);

        var scan = Scan(fixture, source);
        var conversions = scan.Members.Where(m => m.PublicKey.DeclarationKind == "conversion").ToList();
        Assert.Equal(2, conversions.Count);
        Assert.Equal(new[] { "int", "long" }, conversions.Select(c => c.PublicKey.ConversionDestination).Order().ToArray());
        Assert.Equal(2, conversions.Select(c => c.PublicKey.Digest).Distinct().Count());
    }

    [Fact]
    public void Scanner_RecordsAccessorVisibilityAndTombstoneState()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "properties.cs", """
            public class C
            {
                public int Value { get; protected set; }
                [System.Obsolete("Unavailable", true)]
                public int Unsupported() => throw new System.NotSupportedException();
            }
            """);

        var scan = Scan(fixture, source);
        var property = scan.Members.Single(m => m.PublicKey.Name == "Value");
        Assert.Equal(new[] { "get:public", "set:protected" }, property.PublicShape.Accessors.Select(a => $"{a.Kind}:{a.Accessibility}").ToArray());
        Assert.Equal("present-tombstoned", scan.Members.Single(m => m.PublicKey.Name == "Unsupported").State);
    }

    [Fact]
    public void Scanner_DoesNotTreatProtocolDefaultBodyAsTombstone()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "protocol-default.cs", """
            public interface IConfigurable
            {
                string Configure()
                    => throw new System.NotSupportedException("This method uses a Swift protocol extension default. Call it on the concrete type instead.");
            }
            """);

        var member = Scan(fixture, source).Members.Single(m => m.PublicKey.Name == "Configure");

        Assert.Equal("present-callable-unproven", member.State);
        Assert.False(member.PublicShape.Tombstoned);
    }

    [Theory]
    [InlineData("[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]")]
    [InlineData("")] // the C convention is the platform default; the generator writes no attribute
    public void Scanner_FollowsPublicHelperToActualImport(string callConv)
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "imports.cs", $$"""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int M(int value) => Helper(value);
                private static int Helper(int value) => PInvoke_M(value);
                {{callConv}}
                [LibraryImport("Native", EntryPoint = "SBW_M")]
                private static partial int PInvoke_M(int value);
            }
            """);

        var scan = Scan(fixture, source);
        var method = scan.Members.Single(m => m.PublicKey.Name == "M");
        var binding = Assert.Single(method.NativeBindings);
        Assert.Equal("Native", binding.Library);
        Assert.Equal("SBW_M", binding.EntryPoint);
        Assert.Equal("cdecl", binding.Convention);
        Assert.Equal("cdecl", binding.Route);
        Assert.DoesNotContain(scan.Members, m => m.PublicKey.Name is "Helper" or "PInvoke_M");
    }

    [Fact]
    public void Scanner_FollowsPublicCallIntoNestedNativeMethods()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "nested-imports.cs", """
            using System.Runtime.InteropServices;
            namespace Example;
            public static partial class C
            {
                public static int M() => NativeMethods.PInvoke_M();
                private static partial class NativeMethods
                {
                    [LibraryImport("Native", EntryPoint = "SBW_M")]
                    internal static partial int PInvoke_M();
                }
            }
            """);

        var member = Scan(fixture, source).Members.Single(m => m.PublicKey.Name == "M");
        var binding = Assert.Single(member.NativeBindings);

        Assert.Equal("Native", binding.Library);
        Assert.Equal("SBW_M", binding.EntryPoint);
        Assert.Equal("cdecl", binding.Route);
        Assert.Equal("native-syntax-resolved", member.JoinStatus);
    }

    [Fact]
    public void Scanner_PreservesGetterAndSetterNativeRoutesSeparately()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "accessor-imports.cs", """
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static int Value
                {
                    get => PInvoke_Get();
                    set => PInvoke_Set(value);
                }
                [LibraryImport("Native", EntryPoint = "SBW_Get")]
                private static partial int PInvoke_Get();
                [LibraryImport("Native", EntryPoint = "SBW_Set")]
                private static partial void PInvoke_Set(int value);
            }
            """);

        var property = Assert.Single(Scan(fixture, source).Members, m => m.PublicKey.Name == "Value");

        Assert.Equal(
            new[] { "get:SBW_Get", "set:SBW_Set" },
            property.NativeBindings.Select(b => $"{b.Accessor}:{b.EntryPoint}").Order().ToArray());
    }

    [Fact]
    public void Scanner_RecognizesSwiftRegisterTypesAndAbiModifiers()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "swift-registers.cs", """
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            public static partial class C
            {
                public static void Untyped() => PInvoke_Untyped(default, ref _error);
                public static void Typed() => PInvoke_Typed(default);
                private static SwiftError _error;

                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                [LibraryImport("Native", EntryPoint = "$sUntyped")]
                private static partial void PInvoke_Untyped(SwiftSelf self, ref SwiftError error);

                [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
                [LibraryImport("Native", EntryPoint = "$sTyped")]
                private static partial void PInvoke_Typed(SwiftSelf<C> self);
            }
            """);

        var scan = Scan(fixture, source);
        var untyped = Assert.Single(scan.Members.Single(m => m.PublicKey.Name == "Untyped").NativeBindings);
        var typed = Assert.Single(scan.Members.Single(m => m.PublicKey.Name == "Typed").NativeBindings);

        Assert.True(untyped.UntypedSwiftSelf);
        Assert.False(untyped.TypedSwiftSelf);
        Assert.True(untyped.SwiftError);
        Assert.Equal("void(SwiftSelf,ref SwiftError)", untyped.AbiSignature);
        Assert.False(typed.UntypedSwiftSelf);
        Assert.True(typed.TypedSwiftSelf);
    }

    [Fact]
    public void Scanner_DoesNotTreatMisleadingCommentsOrSiblingCallsAsImports()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "ambiguous.cs", """
            using System.Runtime.InteropServices;
            public static partial class A
            {
                // PInvoke_M(1) and [LibraryImport("Fake")] are comments, not evidence.
                public static int M() => 1;
                [LibraryImport("A", EntryPoint = "SBW_A")]
                private static partial int PInvoke_M(int value);
            }
            public static partial class B
            {
                public static int N() => PInvoke_M(1);
                [LibraryImport("B", EntryPoint = "SBW_B")]
                private static partial int PInvoke_M(int value);
            }
            """);

        var scan = Scan(fixture, source);
        Assert.Empty(scan.Members.Single(m => m.PublicKey.Name == "M").NativeBindings);
        Assert.Equal("SBW_B", Assert.Single(scan.Members.Single(m => m.PublicKey.Name == "N").NativeBindings).EntryPoint);
    }

    [Fact]
    public void ManifestSignature_KeepsNestedOwnerAndParameterShape()
    {
        var key = new SurfacePublicKey
        {
            Namespace = "Example",
            EnclosingTypes = ["Outer`1", "Inner"],
            DeclarationKind = "method",
            Name = "Map",
            GenericArity = 1,
            Parameters = [new SurfaceParameterKey("System.Tuple<int,string>", "")],
            Static = false,
        };

        Assert.Equal("Outer.Inner.Map(System.Tuple<int,string>)`2", SurfaceSyntaxScanner.BuildManifestSignature(key));
    }

    [Fact]
    public void ManifestSignature_UsesProducerSpellingForIndexerAndGenericArity()
    {
        var indexer = new SurfacePublicKey
        {
            Namespace = "Example",
            EnclosingTypes = ["Container"],
            DeclarationKind = "indexer",
            Name = "this",
            Parameters = [new SurfaceParameterKey("int", "")],
            Static = false,
        };
        var generic = new SurfacePublicKey
        {
            Namespace = "Example",
            EnclosingTypes = ["Container"],
            DeclarationKind = "method",
            Name = "Create",
            GenericArity = 2,
            Parameters = [],
            Static = true,
        };

        Assert.Equal("Container.this[int]", SurfaceSyntaxScanner.BuildManifestSignature(indexer));
        Assert.Equal("Container.Create()`2", SurfaceSyntaxScanner.BuildManifestSignature(generic));
    }

    [Fact]
    public void Scanner_HandlesNamespaceAliasesShadowedGenericsAndImplicitStaticConstants()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "scoped-generics.cs", """
            namespace Example
            {
                using Alias = System.Collections.Generic.List<int>;
                public class Outer<T>
                {
                    public const int Limit = 1;
                    public Alias Value => new();
                    public void Shadow<T>(T value) { }
                }
            }
            """);

        var scan = Scan(fixture, source);
        var shadow = scan.Members.Single(member => member.PublicKey.Name == "Shadow");

        Assert.True(scan.Members.Single(member => member.PublicKey.Name == "Limit").PublicKey.Static);
        Assert.Equal("System.Collections.Generic.List<int>", scan.Members.Single(member => member.PublicKey.Name == "Value").PublicShape.Type);
        Assert.Equal("T1", shadow.PublicKey.Parameters.Single().Type);
        Assert.Equal("Outer.Shadow(T)`2", SurfaceSyntaxScanner.BuildManifestSignature(shadow.PublicKey));
    }

    [Fact]
    public void Scanner_ConstantAndEnumValuesParticipateInPublicShape()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "constants.cs", """
            public class Values
            {
                public const int SameValue = 0x2;
                public const int ChangedValue = 3;
                public static readonly int RuntimeValue = 4;
            }
            public enum Permission
            {
                Read = 1,
                Write,
                Execute = 4,
            }
            """);

        var scan = Scan(fixture, source);

        Assert.Equal("Int32:2", scan.Members.Single(m => m.PublicKey.Name == "SameValue").PublicShape.ConstantValue);
        Assert.Equal("Int32:3", scan.Members.Single(m => m.PublicKey.Name == "ChangedValue").PublicShape.ConstantValue);
        Assert.Null(scan.Members.Single(m => m.PublicKey.Name == "RuntimeValue").PublicShape.ConstantValue);
        Assert.Equal("Int32:1", scan.Members.Single(m => m.PublicKey.Name == "Read").PublicShape.ConstantValue);
        Assert.Equal("Int32:2", scan.Members.Single(m => m.PublicKey.Name == "Write").PublicShape.ConstantValue);
        Assert.Equal("Int32:4", scan.Members.Single(m => m.PublicKey.Name == "Execute").PublicShape.ConstantValue);
    }

    [Theory]
    [InlineData("public const int Value = 1;", "public const int Value = 2;")]
    [InlineData("public enum Value { Item = 1 }", "public enum Value { Item = 2 }")]
    public void Scanner_ConstantValueChangesProduceShapeChanges(string oldMember, string tipMember)
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var oldSource = oldMember.StartsWith("public enum", StringComparison.Ordinal)
            ? oldMember
            : $"public class Container {{ {oldMember} }}";
        var tipSource = tipMember.StartsWith("public enum", StringComparison.Ordinal)
            ? tipMember
            : $"public class Container {{ {tipMember} }}";
        var request = fixture.Request(oldSource, tipSource);

        var result = SurfaceAccountingEngine.Analyze(request);

        Assert.Equal(1, result.Comparison.Summary.ShapeChanges);
        Assert.Contains(result.Comparison.Changes, change => change.Classification == "shape-change");
    }

    [Fact]
    public void Scanner_MergesThePartsOfAPartialTypeSplitAcrossFiles()
    {
        // A generated type and a hand-written bridge each declare one part; only the first names the bases.
        using var fixture = new SurfaceAccountingTestFixture();
        var main = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "Module.Types.Theme.cs", """
            namespace Example;
            public interface IThemeProtocol { }
            [System.Serializable]
            public partial class Theme : System.IDisposable, IThemeProtocol
            {
                public void Dispose() { }
            }
            """);
        var bridge = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "Module.SwiftUIBridge.cs", """
            namespace Example;
            [System.Obsolete("bridge")]
            public partial class Theme
            {
                public static void SetColor(int value) { }
            }
            """);

        var scan = SurfaceSyntaxScanner.Scan(
            "capture", SurfaceAccountingTestFixture.TargetKey, "swift-managed", "TestModule",
            [main, bridge], evidenceRoot: fixture.Root);
        var reversed = SurfaceSyntaxScanner.Scan(
            "capture", SurfaceAccountingTestFixture.TargetKey, "swift-managed", "TestModule",
            [bridge, main], evidenceRoot: fixture.Root);

        var theme = Assert.Single(scan.Members, m => m.PublicKey.DeclarationKind == "class" && m.PublicKey.Name == "Theme");
        Assert.Equal(2, theme.PublicShape.Attributes.Count);
        Assert.Equal(2, theme.PublicShape.BaseTypes.Count);
        Assert.Contains(theme.PublicShape.BaseTypes, b => b.Contains("IThemeProtocol"));
        Assert.Equal(2, theme.SourceReferences.Count);
        Assert.Contains(scan.Members, m => m.PublicKey.Name == "SetColor");
        Assert.Equal(theme.PublicShape.Digest,
            reversed.Members.Single(m => m.PublicKey.DeclarationKind == "class" && m.PublicKey.Name == "Theme").PublicShape.Digest);
    }

    [Fact]
    public void Scanner_APartialTypeKeepsEveryAttributeApplicationAndItsBasesInCompileOrder()
    {
        static SurfaceMemberObservation Theme(string first, string second)
        {
            using var fixture = new SurfaceAccountingTestFixture();
            var a = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "A.cs", "namespace Example;\n" + first);
            var b = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "B.cs", "namespace Example;\n" + second);
            var scan = SurfaceSyntaxScanner.Scan(
                "capture", SurfaceAccountingTestFixture.TargetKey, "swift-managed", "TestModule", [b, a], evidenceRoot: fixture.Root);
            return scan.Members.Single(m => m.PublicKey.DeclarationKind == "class" && m.PublicKey.Name == "Theme");
        }
        const string Marker = "[System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)] public sealed class MarkAttribute : System.Attribute { }\n";

        // Attribute order carries no meaning, so moving an attribute to the other part leaves the shape alone.
        var oneWay = Theme(
            Marker + "[System.Serializable] public partial class Theme : System.IDisposable { public void Dispose() { } }",
            "[System.Obsolete] public partial class Theme : System.ICloneable { public object Clone() => this; }");
        var otherWay = Theme(
            Marker + "[System.Obsolete] public partial class Theme : System.IDisposable { public void Dispose() { } }",
            "[System.Serializable] public partial class Theme : System.ICloneable { public object Clone() => this; }");
        Assert.Equal(oneWay.PublicShape.Digest, otherWay.PublicShape.Digest);
        // Interface order is observable, so the bases follow the parts in compile order (A.cs before B.cs).
        Assert.Equal(2, oneWay.PublicShape.BaseTypes.Count);
        Assert.Contains("IDisposable", oneWay.PublicShape.BaseTypes[0]);
        Assert.Contains("ICloneable", oneWay.PublicShape.BaseTypes[1]);

        // A repeatable attribute applied in both parts is two applications, not one.
        var once = Theme(Marker + "[Mark] public partial class Theme { }", "public partial class Theme { }");
        var twice = Theme(Marker + "[Mark] public partial class Theme { }", "[Mark] public partial class Theme { }");
        Assert.NotEqual(once.PublicShape.Digest, twice.PublicShape.Digest);
    }

    [Fact]
    public void Scanner_ReorderedInterfacesOfOneDeclarationAreAShapeChange()
    {
        static string Shape(string bases)
        {
            using var fixture = new SurfaceAccountingTestFixture();
            var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "c.cs",
                "namespace Example;\npublic interface IGet<out T> { T Get(); }\npublic class C : " + bases + " { string IGet<string>.Get() => \"\"; System.Uri IGet<System.Uri>.Get() => null!; }");
            return Scan(fixture, source).Members.Single(m => m.PublicKey.Name == "C").PublicShape.Digest;
        }

        Assert.NotEqual(Shape("IGet<string>, IGet<System.Uri>"), Shape("IGet<System.Uri>, IGet<string>"));
    }

    [Fact]
    public void Scanner_PartialPartsThatDisagreeOnAccessibilityStayAConflict()
    {
        using var fixture = new SurfaceAccountingTestFixture();
        var source = SurfaceAccountingTestFixture.WriteSource(fixture.Root, "parts.cs", """
            namespace Example;
            public class Outer
            {
                public partial class Part : System.IDisposable { public void Dispose() { } }
                protected partial class Part { }
            }
            """);

        Assert.Throws<InvalidDataException>(() => Scan(fixture, source));
    }

    private static SurfaceSyntaxScanResult Scan(SurfaceAccountingTestFixture fixture, string source)
        => SurfaceSyntaxScanner.Scan(
            "capture",
            SurfaceAccountingTestFixture.TargetKey,
            "swift-managed",
            "TestModule",
            [source],
            evidenceRoot: fixture.Root);
}
