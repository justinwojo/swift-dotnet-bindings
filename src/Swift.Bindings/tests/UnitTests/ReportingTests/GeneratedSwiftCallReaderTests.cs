// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests.ReportingTests;

public sealed class GeneratedSwiftCallReaderTests
{
    [Fact]
    public void Accessors_ClassifyOnlyFinalSwiftCallingConvention()
    {
        var scan = Scan("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            namespace Sample;
            public class Holder {
              public int A { get => AGet(); set => ASet(value); }
              public int B { get => BGet(); set => BSet(value); }
              private int AGet() => ASwift(new SwiftSelf());
              private void ASet(int value) => ACdecl(value, new SwiftSelf());
              private int BGet() => BCdecl(new SwiftSelf());
              private void BSet(int value) => BSwift(value, new SwiftSelf());
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sA")] private static partial int ASwift(SwiftSelf self);
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
              [LibraryImport("One", EntryPoint="A_C")] private static partial void ACdecl(int value, SwiftSelf self);
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
              [LibraryImport("One", EntryPoint="B_C")] private static partial int BCdecl(SwiftSelf self);
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sB")] private static partial void BSwift(int value, SwiftSelf self);
            }
            public readonly struct SwiftSelf { }
            """);

        Assert.Collection(scan.Observations.OrderBy(row => row.PublicName),
            row => { Assert.Equal("A", row.PublicName); Assert.Equal("get", row.Accessor); },
            row => { Assert.Equal("B", row.PublicName); Assert.Equal("set", row.Accessor); });
    }

    [Fact]
    public void TypedSelf_SwiftError_CommentsAndReverseDispatch_AreControls()
    {
        var scan = Scan("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            namespace Sample;
            public class Holder {
              public void Typed() => TypedImport(new SwiftSelf<int>());
              public void ErrorOnly() => ErrorImport(new SwiftError());
              public string Text() => "SwiftSelf and CallConvSwift"; // SwiftSelf self
              [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvSwift) })]
              private static void Reverse(SwiftSelf self) { }
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sTyped")] private static partial void TypedImport(SwiftSelf<int> self);
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sError")] private static partial void ErrorImport(SwiftError error);
            }
            public readonly struct SwiftSelf { }
            public readonly struct SwiftSelf<T> { }
            public readonly struct SwiftError { }
            """);

        Assert.Empty(scan.Observations);
        Assert.Empty(scan.UnresolvedSpecimens);
    }

    [Fact]
    public void AllocatingMetatype_IsDistinguishedFromInstanceSelf()
    {
        var row = Assert.Single(Scan("""
            using System;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            namespace Sample;
            public class Holder {
              public Holder() => Create(new SwiftSelf());
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sCreate")] private static partial void Create(SwiftSelf _metatypeSelf);
            }
            public readonly struct SwiftSelf { }
            """).Observations);

        Assert.Equal("allocating_metatype", row.SelfRole);
        Assert.Equal("constructor", row.DeclarationKind);
    }

    [Fact]
    public void SharedCall_PreservesOwnersButDeduplicatesNativeIdentity()
    {
        var scan = Scan("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            namespace Sample;
            public class A { public void Run() => Native.Invoke(new SwiftSelf()); }
            public class B { public void Run() => Native.Invoke(new SwiftSelf()); }
            internal static partial class Native {
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sShared")] internal static partial void Invoke(SwiftSelf self);
            }
            public readonly struct SwiftSelf { }
            """);

        Assert.Equal(2, scan.Observations.Count);
        Assert.Equal(2, scan.Observations.Select(row => row.PublicApiKey).Distinct().Count());
        Assert.Single(scan.Observations.Select(row => row.NativeCall.StableKey).Distinct());
    }

    [Fact]
    public void SameSymbolInDifferentLibraries_RemainsDifferentNativeCalls()
    {
        var scan = Scan("""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            namespace Sample;
            public class Holder {
              public void One() => N1.Invoke(new SwiftSelf());
              public void Two() => N2.Invoke(new SwiftSelf());
            }
            internal static partial class N1 {
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("One", EntryPoint="$sSame")] internal static partial void Invoke(SwiftSelf self);
            }
            internal static partial class N2 {
              [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })]
              [LibraryImport("Two", EntryPoint="$sSame")] internal static partial void Invoke(SwiftSelf self);
            }
            public readonly struct SwiftSelf { }
            """);

        Assert.Equal(2, scan.Observations.Count);
        Assert.Equal(2, scan.Observations.Select(row => row.NativeCall.StableKey).Distinct().Count());
    }

    [Fact]
    public void InvokedSwiftFunctionPointer_IsASeparateRoute()
    {
        var row = Assert.Single(Scan("""
            namespace Sample;
            public unsafe class Holder {
              public System.Action Callback { get => Get(); }
              private System.Action Get() => () => {
                var fp = (delegate* unmanaged[Swift]<void*, SwiftSelf, void>)0;
                fp((void*)0, new SwiftSelf());
              };
            }
            public readonly struct SwiftSelf { }
            """).Observations);

        Assert.Equal("get", row.Accessor);
        Assert.Equal("swift_function_pointer", row.Route);
        Assert.Equal("closure_context", row.SelfRole);
        Assert.Null(row.OriginalSwiftSymbol);
    }

    [Fact]
    public void UninvokedFunctionPointerMention_DoesNotBorrowAnUnrelatedInvocation()
    {
        var scan = Scan("""
            namespace Sample;
            public unsafe class Holder {
              public void Inspect() {
                delegate* unmanaged[Swift]<void*, SwiftSelf, void> fp = (delegate* unmanaged[Swift]<void*, SwiftSelf, void>)0;
                System.GC.KeepAlive((nint)fp);
              }
            }
            public readonly struct SwiftSelf { }
            """);

        Assert.Empty(scan.Observations);
    }

    private static GeneratedSwiftCallScanResult Scan(string source)
    {
        var directory = Directory.CreateTempSubdirectory("swiftself-reader-");
        try
        {
            var path = Path.Combine(directory.FullName, "Fixture.cs");
            File.WriteAllText(path, source);
            return GeneratedSwiftCallReader.Scan([path], directory.FullName);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
