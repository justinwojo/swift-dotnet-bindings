// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// The lanes that keep a generated local from colliding with a projected parameter name:
/// <list type="bullet">
/// <item>parameter-DERIVED locals (<c>{p}Buffer</c>, <c>{p}NSArray</c>, …) — moved aside by
/// resolving the parameter's <see cref="ArgumentDecl.MarshallingBaseName"/>, the identifier the
/// emitters suffix, rather than its public C# name;</item>
/// <item>FIXED-spelling body locals (<c>resultPtr</c>, <c>tag</c>, <c>swiftResult</c>, …) — minted
/// through the per-member <see cref="SyntheticLocalNames"/> bundle;</item>
/// <item>the trailing cancellation token, which is a synthesized PARAMETER rather than a body
/// local, so it is resolved against the parameters already on the signature;</item>
/// <item>the EXTENSION emitters' body locals, which have no <see cref="MethodDecl"/> to resolve
/// against — the member's parameter names are already projected strings by then, so those
/// emitters seed a <see cref="SyntheticNameScope"/> from them directly.</item>
/// </list>
/// Every lane has the same contract: the preferred spelling comes back unchanged when nothing
/// collides (so emitted output is unaffected), and only the GENERATED name ever moves.
/// </summary>
public class GeneratedLocalNameTests
{
    // =====================================================================================
    //  Parameter-derived locals
    // =====================================================================================

    /// <summary>
    /// A sibling parameter spelled like one of this parameter's scratch locals forces the
    /// marshalling base aside. The suffixes below are a sample of the vocabulary the projections
    /// use; the rule is structural (sibling extends the name and continues upper-case or '_'), so
    /// it covers suffixes that do not appear here too.
    /// </summary>
    [Theory]
    [InlineData("items", "itemsNSArray")]
    [InlineData("values", "valuesBuffer")]
    [InlineData("numbers", "numbersSwift")]
    [InlineData("scores", "scoresDisposable")]
    [InlineData("labels", "labelsConverted")]
    [InlineData("map", "mapNSDict")]
    [InlineData("entries", "entriesPairs")]
    [InlineData("unique", "uniqueNSSet")]
    [InlineData("callback", "callbackHandle")]
    [InlineData("text", "text_raw")]
    public void ShadowedParameter_GetsEscapedMarshallingBase(string driver, string shadowingSibling)
    {
        var resolved = NameProvider.ResolveMarshallingBaseName(driver, new[] { driver, shadowingSibling });

        Assert.NotEqual(driver, resolved);
        Assert.StartsWith("__", resolved);
        // The sibling that caused the escape must not itself be shadowed by the escaped base.
        Assert.False(shadowingSibling.StartsWith(resolved, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("items", "count")]
    [InlineData("items", "itemsource")]      // lower-case continuation is not a generated suffix
    [InlineData("items", "item")]            // shorter, cannot be a derived local
    [InlineData("items", "items")]           // itself
    [InlineData("value", "otherValue")]      // extends a DIFFERENT name
    public void UnshadowedParameter_KeepsItsOwnName(string driver, string sibling)
    {
        Assert.Equal(driver, NameProvider.ResolveMarshallingBaseName(driver, new[] { driver, sibling }));
    }

    [Fact]
    public void EscapedBase_AvoidsASiblingAlreadyHoldingTheEscapedForm()
    {
        // Both the derived-local shape AND the plain "__items" escape are occupied.
        var resolved = NameProvider.ResolveMarshallingBaseName("items", new[] { "items", "itemsNSArray", "__items" });

        Assert.NotEqual("items", resolved);
        Assert.NotEqual("__items", resolved);
        Assert.StartsWith("__items", resolved);
    }

    [Fact]
    public void MarshallingBaseName_DefaultsToTheProjectedParameterName()
    {
        // A helper path that builds an ArgumentDecl inline never runs the dedup pass; the emitters
        // must still get a usable base rather than null.
        var arg = TestDecls.Param("items", new NamedTypeSpec("Swift.Int"));
        Assert.Equal(NameProvider.GetCSharpParameterName(arg), NameProvider.GetMarshallingBaseName(arg));
    }

    [Fact]
    public void DeduplicatedSignature_AssignsBothNamesAndKeepsPublicNamesIntact()
    {
        var driver = TestDecls.Param("items", new NamedTypeSpec("Swift.Int"));
        var shadowing = TestDecls.Param("itemsNSArray", new NamedTypeSpec("Swift.Int"));
        NameProvider.DeduplicateParameterNamesForParameterList(new[] { driver, shadowing });

        // The public surface is untouched — only the internal marshalling base moved.
        Assert.Equal("items", NameProvider.GetCSharpParameterName(driver));
        Assert.Equal("itemsNSArray", NameProvider.GetCSharpParameterName(shadowing));
        Assert.NotEqual("items", NameProvider.GetMarshallingBaseName(driver));
        Assert.Equal("itemsNSArray", NameProvider.GetMarshallingBaseName(shadowing));
    }

    // =====================================================================================
    //  Fixed-spelling body locals
    // =====================================================================================

    /// <summary>Every spelling the bundle resolves eagerly, one per generated body local.</summary>
    private static readonly string[] Spellings =
    {
        "resultPtr", "hasValuePtr", "swiftIndirectResult", "bufferPtr", "returnMetadata",
        "innerMetadata", "selfMetadata", "optionalMetadata", "resultBuffer", "tag",
        "payloadBuffer", "existentialResult", "_swiftResult", "swiftResult", "success", "handle",
    };

    public static IEnumerable<object[]> BundleSpellings => Spellings.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(BundleSpellings))]
    public void NoCollidingParameter_LocalKeepsItsPreferredSpelling(string spelling)
    {
        var locals = SyntheticLocalNames.Resolve(MethodWithParameters("count", "other"));
        Assert.Equal(spelling, locals.Local(spelling));
    }

    [Theory]
    [MemberData(nameof(BundleSpellings))]
    public void CollidingParameter_LocalMovesAsideAndParameterKeepsItsName(string spelling)
    {
        var method = MethodWithParameters(spelling);
        var locals = SyntheticLocalNames.Resolve(method);

        Assert.NotEqual(spelling, locals.Local(spelling));
        // The user's parameter is never the one that moves.
        Assert.Equal(spelling, NameProvider.GetCSharpParameterName(method.CSSignature[1]));
    }

    [Theory]
    [MemberData(nameof(BundleSpellings))]
    public void LocalIsIdempotentWithinAMember(string spelling)
    {
        var locals = SyntheticLocalNames.Resolve(MethodWithParameters(spelling));
        Assert.Equal(locals.Local(spelling), locals.Local(spelling));
    }

    [Theory]
    [MemberData(nameof(BundleSpellings))]
    public void BundleSpellingIsFixedBeforeAnyAdHocLocalCanTakeIt(string spelling)
    {
        // The declaring site and every reading site must agree, whichever runs first. The bundle's
        // own spellings are resolved together in the constructor, so nothing about their order is
        // observable — but the ad-hoc spellings a body mints along the way are minted lazily, and
        // one of those asking for the escaped form of a bundle name is a real race. Each variant
        // below mints the whole bundle AND its escaped forms in a different order first; the answer
        // for the spelling under test has to come out the same every time.
        var askedFirst = SyntheticLocalNames.Resolve(MethodWithParameters(spelling)).Local(spelling);

        foreach (var order in AskOrders())
        {
            var late = SyntheticLocalNames.Resolve(MethodWithParameters(spelling));
            foreach (var other in order)
            {
                late.Local(other);
                late.Local("__" + other);
            }

            Assert.Equal(askedFirst, late.Local(spelling));
        }
    }

    // =====================================================================================
    //  Composed spellings (a suffix concatenated onto a possibly-verbatim parameter name)
    // =====================================================================================

    [Theory]
    [InlineData("@eventUtf8Ptr")]
    [InlineData("@inBuffer")]
    public void ComposedSpellingThatNeedNotMoveKeepsItsVerbatimMarker(string spelling)
    {
        // Nothing in this scope holds the identifier, so the caller gets back exactly what it asked
        // for. Handing back the stripped form instead would rewrite the spelling of every such
        // local in the corpus without any collision to justify it.
        var scope = new SyntheticNameScope(new[] { "unrelated" });
        Assert.Equal(spelling, scope.MintComposed(spelling));
    }

    [Fact]
    public void ComposedSpellingThatCollidesComesBackEscapedAndNonVerbatim()
    {
        // The verbatim marker is only meaningful on an identifier that is a C# keyword; the escaped
        // form is not one, so it comes back plain.
        var scope = new SyntheticNameScope(new[] { "eventUtf8Ptr" });

        var minted = scope.MintComposed("@eventUtf8Ptr");

        Assert.NotEqual("@eventUtf8Ptr", minted);
        Assert.DoesNotContain("@", minted);
        Assert.False(minted == "eventUtf8Ptr", "the parameter, not the generated local, keeps the name");
    }

    [Fact]
    public void TheVerbatimMarkerDoesNotSplitOneIdentifierIntoTwoNames()
    {
        // "@x" and "x" are one identifier to the compiler. A scope that reserved it under the first
        // spelling and then escaped the second away from it would put two names on one local.
        var scope = new SyntheticNameScope();

        var withMarker = scope.MintComposed("@eventUtf8Ptr");
        var without = scope.Mint("eventUtf8Ptr");

        Assert.Equal("@eventUtf8Ptr", withMarker);
        Assert.Equal("eventUtf8Ptr", without);
    }

    [Fact]
    public void ComposedSpellingIsIdempotentLikeAnyOtherMint()
    {
        var scope = new SyntheticNameScope(new[] { "itemsBuffer" });
        Assert.Equal(scope.MintComposed("itemsBuffer"), scope.MintComposed("itemsBuffer"));
    }

    /// <summary>
    /// A few distinct orders to mint in — declaration order, its reverse, and a deterministic
    /// interleave — so the test actually varies what "asks first" means.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> AskOrders()
    {
        var forward = Spellings.ToList();
        yield return forward;

        var reversed = forward.AsEnumerable().Reverse().ToList();
        yield return reversed;

        // Deterministic interleave: last, first, second-to-last, second, ...
        var interleaved = new List<string>();
        for (int lo = 0, hi = forward.Count - 1; lo <= hi; lo++, hi--)
        {
            interleaved.Add(forward[hi]);
            if (lo != hi)
                interleaved.Add(forward[lo]);
        }
        yield return interleaved;
    }

    [Fact]
    public void AllBodyLocalsAreDistinctEvenWhenSeveralCollide()
    {
        var spellings = Spellings.ToList();
        var locals = SyntheticLocalNames.Resolve(MethodWithParameters(spellings.ToArray()));

        var minted = spellings.Select(locals.Local).ToList();
        Assert.Equal(minted.Count, minted.Distinct(StringComparer.Ordinal).Count());
        // And none of them landed back on a parameter.
        Assert.Empty(minted.Intersect(spellings, StringComparer.Ordinal));
    }

    [Fact]
    public void BodyLocalsAlsoAvoidAnEscapedMarshallingBase()
    {
        // A parameter whose derived locals were shadowed carries a SECOND identifier in the body
        // (the escaped base it is aliased to), which the fixed-spelling locals must avoid too.
        var driver = TestDecls.Param("tag", new NamedTypeSpec("Swift.Int"));
        var shadowing = TestDecls.Param("tagBuffer", new NamedTypeSpec("Swift.Int"));
        var method = TestDecls.Method("m", parameters: new[] { driver, shadowing });
        NameProvider.DeduplicateParameterNames(method.CSSignature);

        var locals = SyntheticLocalNames.Resolve(method);
        var escapedBase = NameProvider.GetMarshallingBaseName(driver);

        Assert.NotEqual("tag", locals.Tag);
        Assert.NotEqual(escapedBase, locals.Tag);
    }

    /// <summary>
    /// The return projections that declare a body local with a fixed spelling hand the emitter that
    /// spelling to resolve. Each one must be a spelling the per-member bundle actually resolves,
    /// otherwise the local would be minted outside the bundle's fixed order and two phases could
    /// disagree about its name.
    /// </summary>
    [Fact]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Test-only scan of the fully-rooted generator assembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Test-only scan of the fully-rooted generator assembly.")]
    public void EveryFixedReturnLocalSpellingIsResolvedByTheBundle()
    {
        var declared = typeof(ITypeProjection).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IResolvedReturnLocalProjection).IsAssignableFrom(t))
            .Select(t => t.GetField("DefaultLocalName", BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue() as string)
            .Where(name => name is not null)
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(declared);
        var bundleSpellings = Spellings.ToHashSet(StringComparer.Ordinal);
        foreach (var name in declared)
            Assert.Contains(name, bundleSpellings);
    }

    // =====================================================================================
    //  The synthesized trailing cancellation token
    // =====================================================================================

    [Fact]
    public void NoCollidingParameter_CancellationTokenKeepsItsPlainName()
    {
        Assert.Equal("cancellationToken",
            NameProvider.ResolveCancellationTokenName(MethodWithParameters("count", "label")));
    }

    [Fact]
    public void ParameterSpellingTheToken_MovesTheSynthesizedParameterAside()
    {
        var method = MethodWithParameters("cancellationToken");
        var resolved = NameProvider.ResolveCancellationTokenName(method);

        Assert.NotEqual("cancellationToken", resolved);
        // The user's parameter keeps its projected name — the appended one is what moves.
        Assert.Equal("cancellationToken", NameProvider.GetCSharpParameterName(method.CSSignature[1]));
    }

    [Fact]
    public void ResolvedTokenNameIsStableAcrossCalls()
    {
        // Independent emitters resolve it separately (signature builder, body, proxy receiver), so
        // the function must be pure in the decl.
        var method = MethodWithParameters("cancellationToken");
        Assert.Equal(
            NameProvider.ResolveCancellationTokenName(method),
            NameProvider.ResolveCancellationTokenName(method));
    }

    [Fact]
    public void TokenNameIsResolvedAgainstTheProjectedNameNotTheSwiftLabel()
    {
        // The Swift label is `token:`; the internal name — which is what projects to C# — collides.
        var arg = new ArgumentDecl
        {
            Name = "token",
            PrivateName = "cancellationToken",
            SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = null,
        };
        var method = TestDecls.Method("m", parameters: new[] { arg });

        Assert.NotEqual("cancellationToken", NameProvider.ResolveCancellationTokenName(method));
    }

    [Fact]
    public void ParameterNamedLikeTheTokenIsNotTreatedAsADebugArgument()
    {
        // Only the recognised compiler-supplied debug spellings (file:, line:, function:, ...) are
        // stripped from the emitted signature. A defaulted StaticString parameter spelled like the
        // token is not one of them, so it IS emitted, is in scope, and must force the escape.
        var debugArg = new ArgumentDecl
        {
            Name = "cancellationToken",
            PrivateName = "cancellationToken",
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = true,
            ParentDecl = null,
            ModuleDecl = null,
        };
        var method = TestDecls.Method("m", parameters: new[] { debugArg });
        Assert.NotEqual("cancellationToken", NameProvider.ResolveCancellationTokenName(method));
    }

    [Fact]
    public void GenuineDebugParameterIsNotInScopeForTheToken()
    {
        // A real debug argument (file:) is stripped from every emitted signature, so it is not in
        // scope and cannot push the appended token aside.
        var realDebugArg = new ArgumentDecl
        {
            Name = "file",
            PrivateName = "file",
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            IsInOut = false,
            IsGeneric = false,
            HasDefaultArg = true,
            ParentDecl = null,
            ModuleDecl = null,
        };
        Assert.Equal("cancellationToken",
            NameProvider.ResolveCancellationTokenName(TestDecls.Method("m", parameters: new[] { realDebugArg })));
    }

    [Fact]
    public void ZeroSizedParameterIsNotInScopeForTheToken()
    {
        // A `()` parameter is dropped by every signature builder, so it cannot shadow anything.
        var emptyArg = TestDecls.Param("cancellationToken", TupleTypeSpec.Empty);
        Assert.Equal("cancellationToken",
            NameProvider.ResolveCancellationTokenName(TestDecls.Method("m", parameters: new[] { emptyArg })));
    }

    // =====================================================================================
    //  Extension-emitter body locals
    // =====================================================================================

    /// <summary>
    /// The spellings the extension emitters declare into a member body: the class-return holder and
    /// the three a resilient-struct return needs.
    /// </summary>
    public static IEnumerable<object[]> ExtensionBodySpellings =>
        new[] { "result", "metadata", "buffer", ExtensionMarshallingHelper.IndirectResultLocalName }
            .Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(ExtensionBodySpellings))]
    public void ExtensionBodyLocalKeepsItsPreferredSpellingWhenNothingCollides(string spelling)
    {
        // Nothing may move without a collision to justify it — otherwise the change would rewrite
        // the spelling of these locals across every extension member in the corpus.
        var scope = ExtensionMarshallingHelper.BuildBodyScope(new[] { "count", "label" });
        Assert.Equal(spelling, scope.Mint(spelling));
    }

    [Theory]
    [MemberData(nameof(ExtensionBodySpellings))]
    public void ExtensionParameterSpellingABodyLocalMovesTheLocalAside(string spelling)
    {
        var scope = ExtensionMarshallingHelper.BuildBodyScope(new[] { spelling });
        Assert.NotEqual(spelling, scope.Mint(spelling));
    }

    [Fact]
    public void ExtensionReceiverIsInScopeForTheBodyLocals()
    {
        // The emitted member takes the receiver as its first parameter, so it occupies the body
        // scope exactly as a Swift-derived parameter does.
        var scope = ExtensionMarshallingHelper.BuildBodyScope(Array.Empty<string>());
        Assert.NotEqual("self", scope.Mint("self"));
    }

    [Theory]
    [InlineData(ExtensionMarshallingHelper.ReturnKind.ObjCClass)]
    [InlineData(ExtensionMarshallingHelper.ReturnKind.SwiftClass)]
    public void ClassReturnIsReadBackThroughTheLocalItWasDeclaredInto(
        ExtensionMarshallingHelper.ReturnKind category)
    {
        // A parameter spelled like the holder forces it aside; the read must follow it, otherwise
        // the body would return the parameter's value instead of the call's.
        var scope = ExtensionMarshallingHelper.BuildBodyScope(new[] { "result" });
        var body = EmitReturn(w => ExtensionMarshallingHelper.EmitReturnValueMarshalling(
            w, category, "NativeMethods.Call(result, self)", "Mod.Thing", scope));

        var holder = DeclaredLocalOnLineContaining(body, "NativeMethods.Call");
        Assert.NotEqual("result", holder);
        Assert.Contains(holder, ReturnedExpression(body), StringComparison.Ordinal);
    }

    [Fact]
    public void ResilientStructReturnWiresEachDeclaredLocalToItsOwnConsumers()
    {
        // All three of the arm's locals are spelled by parameters here, so all three move. What the
        // body does with them has to move with them: the allocation is sized from the metadata it
        // declared, the value is read back out of the buffer it allocated, and the failure path
        // frees that same buffer.
        var scope = ExtensionMarshallingHelper.BuildBodyScope(
            new[] { "metadata", "buffer", ExtensionMarshallingHelper.IndirectResultLocalName });
        var indirectResult = scope.Mint(ExtensionMarshallingHelper.IndirectResultLocalName);

        var body = EmitReturn(w => ExtensionMarshallingHelper.EmitReturnValueMarshalling(
            w, ExtensionMarshallingHelper.ReturnKind.NonFrozenStruct,
            $"NativeMethods.Call({indirectResult}, self)", "Mod.Summary", scope));

        var metadata = DeclaredLocalOnLineContaining(body, "GetTypeMetadata()");
        var buffer = DeclaredLocalOnLineContaining(body, "NativeMemory.Alloc");
        var declaredIndirect = DeclaredLocalOnLineContaining(body, "new SwiftIndirectResult");

        foreach (var local in new[] { metadata, buffer, declaredIndirect })
            Assert.DoesNotContain(local, new[] { "metadata", "buffer", "indirectResult" }, StringComparer.Ordinal);
        Assert.Equal(3, new[] { metadata, buffer, declaredIndirect }.Distinct(StringComparer.Ordinal).Count());

        Assert.Contains($"Alloc({metadata}.Size)", body, StringComparison.Ordinal);
        Assert.Contains($"SwiftIndirectResult((void*){buffer})", body, StringComparison.Ordinal);
        Assert.Contains($"MarshalFromSwift<Mod.Summary>({buffer})", body, StringComparison.Ordinal);
        Assert.Contains($"NativeMemory.Free((void*){buffer})", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndirectResultArgumentAndItsDeclarationAreOneLocal()
    {
        // The argument goes into the native call by one emitter and the declaration is written by
        // another. They share only the scope, so the mint has to be idempotent per spelling or the
        // body would pass an identifier it never declared.
        var scope = ExtensionMarshallingHelper.BuildBodyScope(
            new[] { ExtensionMarshallingHelper.IndirectResultLocalName });
        var argument = scope.Mint(ExtensionMarshallingHelper.IndirectResultLocalName);

        var body = EmitReturn(w => ExtensionMarshallingHelper.EmitReturnValueMarshalling(
            w, ExtensionMarshallingHelper.ReturnKind.NonFrozenStruct,
            $"NativeMethods.Call({argument}, self)", "Mod.Summary", scope));

        Assert.Equal(argument, DeclaredLocalOnLineContaining(body, "new SwiftIndirectResult"));
    }

    [Fact]
    public void ExtensionBodyScopeIsIdempotentAcrossSites()
    {
        var scope = ExtensionMarshallingHelper.BuildBodyScope(new[] { "result", "metadata" });
        foreach (var spelling in new[] { "result", "metadata", "buffer" })
            Assert.Equal(scope.Mint(spelling), scope.Mint(spelling));
    }

    /// <summary>Runs one emission against a throwaway writer and returns what it wrote.</summary>
    private static string EmitReturn(Action<CSharpWriter> emit)
    {
        var text = new StringWriter();
        var writer = new CSharpWriter(text);
        emit(writer);
        writer.Flush();
        return text.ToString();
    }

    /// <summary>
    /// The identifier declared on the single emitted line containing <paramref name="marker"/> —
    /// the local whose initializer that marker belongs to.
    /// </summary>
    private static string DeclaredLocalOnLineContaining(string body, string marker)
    {
        var line = body.Split('\n').Single(l => l.Contains(marker, StringComparison.Ordinal));
        var match = Regex.Match(line, @"^\s*(?:var|IntPtr)\s+(\w+)\s*=");
        Assert.True(match.Success, $"no local declaration on: {line.Trim()}");
        return match.Groups[1].Value;
    }

    /// <summary>The expression the emitted body returns.</summary>
    private static string ReturnedExpression(string body)
        => body.Split('\n').Single(l => l.TrimStart().StartsWith("return ", StringComparison.Ordinal));

    // =====================================================================================

    private static MethodDecl MethodWithParameters(params string[] parameterNames)
        => TestDecls.Method("m",
            parameters: parameterNames.Select(n => TestDecls.Param(n, new NamedTypeSpec("Swift.Int"))).ToList());
}
