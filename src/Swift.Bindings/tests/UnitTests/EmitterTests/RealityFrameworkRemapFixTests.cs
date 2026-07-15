// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Diagnostics.CodeAnalysis;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Regression coverage for the four-part umbrella re-export fix bundle:
///
///   A. <see cref="ValidationRuleSet.ReferencesUnsupportedModule"/> consults the
///      Apple <c>compileImportModule</c> reverse map, so a TypeSpec qualified with
///      the umbrella module (e.g. <c>RealityKit.Entity.ChildCollection.IndexingIterator</c>)
///      is rejected when the canonical declaration in the source module
///      (<c>RealityFoundation.…</c>) was recorded as skipped.
///
///   B. <see cref="NativeIntOverloadEmitter.ResolveType"/> probes the
///      bound-generic-alias table BEFORE the bare-name + generic-arg recursion
///      fallback, so <c>Swift.SIMD3&lt;Swift.Float&gt;</c> resolves to
///      <c>simd.simd_float3</c> instead of <c>Swift.SIMD3&lt;float&gt;</c>.
///
///   C. <see cref="SwiftABIParser"/> enum-case associated-value parsing replaces
///      ABI children that are <c>TypeNameAlias</c> nodes with their resolved
///      underlying nominal, while leaving non-alias elements as-is.
///
///   D. The B12 ObjC optional fast-path in
///      <c>WrapperEmitter.Marshalling.cs</c> defers to the inner type's
///      TypeRecord ObjC flags when one exists, instead of relying purely on the
///      module-name heuristic <c>IsObjCModuleType</c>. This prevents
///      <c>material?.Handle</c>-style emission for plain Swift classes whose
///      ABI <c>printedName</c> uses an umbrella re-export module.
/// </summary>
public class RealityFrameworkRemapFixTests
{
    // --- Fix A: ValidationRuleSet umbrella source-module probe -----------------

    [Fact]
    public void ReferencesUnsupportedModule_UmbrellaName_WhenSourceModuleTypeSkipped_ReturnsTrue()
    {
        // Given a type whose canonical declaration lives in RealityFoundation but whose ABI
        // printedName qualifies it with the umbrella module (RealityKit.Entity.ChildCollection.
        // IndexingIterator), the validation gate must recognise the umbrella reference as
        // pointing at the same skipped type. Without this remap the gate misses and dangling
        // references reach the C# compiler.
        var moduleDecl = BuildEmptyModuleDecl("RealityFoundation");
        var skippedType = BuildStructDecl(moduleDecl,
            "RealityFoundation.Entity.ChildCollection.IndexingIterator");

        ReportCollector.Start(moduleDecl);
        try
        {
            ReportCollector.RecordTypeSkipped(skippedType, SkipReason.Unknown);

            var umbrellaRef = new NamedTypeSpec(
                "RealityKit.Entity.ChildCollection.IndexingIterator");

            Assert.True(
                ValidationRuleSet.ReferencesUnsupportedModule(umbrellaRef),
                "Umbrella RealityKit.* reference must remap to RealityFoundation.* skip set");
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    [Fact]
    public void ReferencesUnsupportedModule_UmbrellaName_WhenSourceModuleTypeNotSkipped_ReturnsFalse()
    {
        // Negative companion: with no skipped type recorded, the umbrella probe must NOT
        // false-positive. Otherwise every umbrella-qualified reference would be gated.
        var moduleDecl = BuildEmptyModuleDecl("RealityFoundation");

        ReportCollector.Start(moduleDecl);
        try
        {
            var umbrellaRef = new NamedTypeSpec(
                "RealityKit.Entity.ChildCollection.IndexingIterator");

            Assert.False(ValidationRuleSet.ReferencesUnsupportedModule(umbrellaRef));
        }
        finally
        {
            ReportCollector.Reset();
        }
    }

    // --- Fix B: NativeIntOverloadEmitter SIMD alias --------------------------

    [Fact]
    public void NativeIntOverloadEmitter_ResolveType_BoundGenericSimdAlias_ReturnsAliasFqn()
    {
        // Swift.SIMD3<Swift.Float> must resolve through the BoundGenericSimdAliases
        // table BEFORE the bare-name + generic-arg recursion fallback, otherwise the
        // int-overload emits Swift.SIMD3<float> which does not exist as a C# type.
        var typeDb = BuildTypeDbWithSimdFloat3();

        var moduleDecl = BuildEmptyModuleDecl("TestModule");
        var parentType = new ClassDecl
        {
            Name = "Owner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Owner"),
            MangledName = "$s10TestModule5OwnerC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        var simd3Float = new NamedTypeSpec("Swift.SIMD3", new NamedTypeSpec("Swift.Float"));
        var method = new MethodDecl
        {
            Name = "translate",
            MangledName = "$sTranslate",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", SwiftTypeSpec = TupleTypeSpec.Empty,
                        IsInOut = false, IsGeneric = false,
                        ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { Name = "v", PrivateName = "v", SwiftTypeSpec = simd3Float,
                        IsInOut = false, IsGeneric = false,
                        ParentDecl = parentType, ModuleDecl = moduleDecl },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        var env = new MethodEnvironment(method, typeDb);

        var resolved = NativeIntOverloadEmitter.ResolveType(simd3Float, env, isParameter: true);

        // The alias-table probe must short-circuit BEFORE the bare-name + generic-arg recursion
        // fallback, so the result is the alias TypeRecord's full C# name (System.Numerics.Vector3
        // in this test). What matters semantically is that we did NOT fall through to the
        // generic-recursion path — that would yield something like "Swift.SIMD3<...>", which
        // does not exist as a C# type and produces CS0234.
        Assert.Equal("System.Numerics.Vector3", resolved);
        Assert.DoesNotContain("SIMD3", resolved);
    }

    [Fact]
    public void NativeIntOverloadEmitter_ResolveType_NestedOptionalOfSimdAlias_PropagatesAliasName()
    {
        // Regression guard for Optional<Swift.SIMD3<Swift.Float>>: even when the outer named
        // type isn't itself an alias, the inner SIMD must still resolve through the alias
        // table (via the recursive ResolveType call at the generic-arg recursion site). The
        // Optional<...> formatter falling through without recursive aliasing would emit
        // "Swift.Optional<Swift.SIMD3<float>>" — a non-existent C# type. The primary method's
        // projection emits "System.Numerics.Vector3?", but if that projection returns null,
        // the fallback must at least keep the inner alias resolvable so the resulting overload
        // signature stays consistent with the SIMD typealias.
        var typeDb = BuildTypeDbWithSimdFloat3();

        var moduleDecl = BuildEmptyModuleDecl("TestModule");
        var parentType = new ClassDecl
        {
            Name = "Owner",
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Owner"),
            MangledName = "$s10TestModule5OwnerC",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };

        var simd3Float = new NamedTypeSpec("Swift.SIMD3", new NamedTypeSpec("Swift.Float"));
        var optSimd = new NamedTypeSpec("Swift.Optional", simd3Float);
        var method = new MethodDecl
        {
            Name = "translateMaybe",
            MangledName = "$sTranslateMaybe",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                new() { Name = "", PrivateName = "", SwiftTypeSpec = TupleTypeSpec.Empty,
                        IsInOut = false, IsGeneric = false,
                        ParentDecl = parentType, ModuleDecl = moduleDecl },
                new() { Name = "v", PrivateName = "v", SwiftTypeSpec = optSimd,
                        IsInOut = false, IsGeneric = false,
                        ParentDecl = parentType, ModuleDecl = moduleDecl },
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentType,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        var env = new MethodEnvironment(method, typeDb);

        var resolved = NativeIntOverloadEmitter.ResolveType(optSimd, env, isParameter: true);

        // The inner SIMD3 must short-circuit through the alias table on recursion. The outer
        // Optional must emit C# nullable form (T?), never the raw generic shape
        // "Swift.Optional<T>" — that's not a valid C# type and would CS0234 the int overload.
        Assert.Equal("System.Numerics.Vector3?", resolved);
        Assert.DoesNotContain("Swift.Optional", resolved);
        Assert.DoesNotContain("SIMD3", resolved);
        Assert.DoesNotContain("Swift.Float", resolved);
    }

    // --- Fix C: enum-case TypeNameAlias surgical unwrap ----------------------

    [Fact]
    public void SwiftABIParser_EnumCase_TupleAssocValues_ResolvesOnlyTypeNameAliasChildren()
    {
        // Construct an enum case `transform(name: String, matrix: simd.float4x4)` whose ABI
        // tuple has two children: a kNominal "Swift.String" and a TypeNameAlias for simd.float4x4
        // → simd.simd_float4x4. The surgical fix must (1) preserve the textually-parsed
        // String element unchanged and (2) replace the alias element with its resolved
        // underlying nominal so downstream type-database lookups succeed.
        var stringChild = CreateNode(kind: "TypeNominal", name: "String", printedName: "Swift.String");
        var float4x4Underlying = CreateNode(kind: "TypeNominal", name: "simd_float4x4",
            printedName: "simd.simd_float4x4");
        var float4x4Alias = CreateNodeWithChildren(kind: "TypeNameAlias",
            name: "float4x4", printedName: "simd.float4x4",
            children: new[] { float4x4Underlying });

        var assocTuple = CreateNodeWithChildren(kind: "Tuple", name: "Tuple",
            printedName: "(name: Swift.String, matrix: simd.float4x4)",
            children: new[] { stringChild, float4x4Alias });

        // The parser uses kFunc = "TypeFunc" and kTuple = "Tuple" — the ABI shape is:
        //   outerTypeFunc.Children = [innerTypeFunc, metatype]
        //   innerTypeFunc.Children = [returnTypeNominal, tupleOfAssocValues]
        var innerFunc = CreateNodeWithChildren(kind: "TypeFunc", name: "TypeFunc",
            printedName: "(name: Swift.String, matrix: simd.float4x4) -> Move",
            children: new[]
            {
                CreateNode(kind: "TypeNominal", name: "Move", printedName: "TestModule.Move"),
                assocTuple,
            });
        var outerFunc = CreateNodeWithChildren(kind: "TypeFunc", name: "TypeFunc",
            printedName: "(Move.Type) -> (name: Swift.String, matrix: simd.float4x4) -> Move",
            children: new[]
            {
                innerFunc,
                CreateNode(kind: "TypeNominal", name: "MoveMeta", printedName: "Move.Type"),
            });

        // EnumElements arrive as Var nodes with DeclKind="EnumElement" (HandleNode dispatches
        // on Kind first, then routes Var+EnumElement to CreateEnumCaseDecl).
        var enumCase = CreateNode(kind: "Var", declKind: "EnumElement",
            name: "transform", mangledName: "$s10TestModule4MoveO9transformyACSS_so0F4x4_aTcACmFWC");
        enumCase.Children = new[] { outerFunc };

        var enumDecl = CreateNode(kind: "TypeDecl", declKind: "Enum",
            name: "Move", mangledName: "$s10TestModule4MoveO");
        enumDecl.Children = new[] { enumCase };

        using var fixture = CreateParserWithNodes(enumDecl);
        var module = fixture.Parser.ParseModule().ModuleDecl;

        var enumDeclResult = Assert.Single(module.Types) as EnumDecl;
        Assert.NotNull(enumDeclResult);
        var caseResult = Assert.Single(enumDeclResult!.Cases);
        Assert.Equal("transform", caseResult.Name);
        Assert.Equal(2, caseResult.AssociatedValues.Count);

        var first = Assert.IsType<NamedTypeSpec>(caseResult.AssociatedValues[0]);
        Assert.Equal("Swift.String", first.Name);
        // Label is preserved from the textually-parsed tuple.
        Assert.Equal("name", first.TypeLabel);

        // The second element must be the unwrapped underlying nominal, not the alias name.
        var second = Assert.IsType<NamedTypeSpec>(caseResult.AssociatedValues[1]);
        Assert.Equal("simd.simd_float4x4", second.Name);
        Assert.Equal("matrix", second.TypeLabel);
    }

    // --- Fix E: Optional<Tuple<String, Class>> per-element extraction --------

    [Fact]
    public void OptionalTupleOfStringClass_GetReturnPlan_DecomposesPerElement()
    {
        // RealityFoundation Iterator.next() for `(name: String, animation: AnimationResource)?`.
        // The carrier's tuple value-witness metadata is derived from the wrapper element types, so
        // the class slot appears as its wrapper (AnimationResource), NOT a raw IntPtr. That is what
        // lets the carrier retain the class on copy and release it on destroy — an IntPtr slot reads
        // as POD and leaks the wire +1. Extraction binds .Some ONCE (each access re-extracts a fresh
        // +1), hands the class element through self-owning (no MarshalFromSwiftObject re-wrap), and
        // converts + disposes the SwiftString element.
        var stringProj = new StringProjection();
        var classProj = new ClassProjection("AnimationResource");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, classProj });
        var optProj = new OptionalProjection(tupleProj);

        var plan = optProj.GetReturnPlan("resultPtr", ReturnStrategy.IndirectResult);
        var setup = RenderSetup(plan);

        // Class-aware carrier metadata — the class slot is the wrapper type, never a raw IntPtr.
        Assert.Contains(setup, l => l.Contains("SwiftOptional<ValueTuple<SwiftString, AnimationResource>>"));
        Assert.DoesNotContain(setup, l => l.Contains("ValueTuple<SwiftString, IntPtr>"));

        // .Some bound exactly once (re-access would leak a +1 per element per access).
        Assert.Single(setup, l => l.Contains("= _swiftOpt.Some;"));

        // Class element passes through self-owning — NOT re-wrapped via MarshalFromSwiftObject.
        Assert.Contains(setup, l => l.Contains("= _optTuple.Item2;"));
        Assert.DoesNotContain(setup, l => l.Contains("MarshalFromSwiftObject<AnimationResource>"));

        // String element is converted and its +1 disposed in place.
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.ToString()"));
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.Dispose();"));

        // The buggy whole-tuple cast must be gone; the body lives entirely in setup.
        Assert.DoesNotContain(setup, l => l.Contains("(string, AnimationResource)?)"));
        Assert.Empty(plan.PInvokeExpression);
    }

    [Fact]
    public void TupleOfStringClass_TopLevelGetReturnPlan_LiftsClassFieldFromIntPtr()
    {
        // The per-element class lift was originally only added
        // to TupleProjection.GetReturnElementConversion (the inner-element path used by
        // Optional<Tuple>), so a direct top-level `(String, Class)` return — without an
        // Optional wrapper — still left the class field as raw IntPtr in the public
        // ValueTuple. The fix mirrors the lift in GetReturnPlan so both shapes are covered.
        var stringProj = new StringProjection();
        var classProj = new ClassProjection("AnimationResource");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, classProj });

        var plan = tupleProj.GetReturnPlan("result", ReturnStrategy.Direct);

        // The class element must be lifted via MarshalFromSwiftObject — never passed through
        // as the raw IntPtr from result.Item2.
        var setup = plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).ToList();
        Assert.Contains(setup,
            l => l.Contains("MarshalFromSwiftObject<AnimationResource>(result.Item2)"));

        // The string element must keep using ToString() so the public type matches.
        Assert.Contains(setup, l => l.Contains("result.Item1.ToString()"));

        // The PInvoke expression should compose both lifted vars into a ValueTuple.
        Assert.StartsWith("(", plan.PInvokeExpression);
        Assert.EndsWith(")", plan.PInvokeExpression);
    }

    // --- Fix F: Dictionary<Int, URL> NSDictionary integer-key unboxing ------

    [Fact]
    public void DictionaryIntUrl_ObjCBridgeReturn_UnboxesIntKeyViaNSNumber()
    {
        // Reproduces RealityFoundation `[Int: URL]` returns. Before the fix, FromNSObject for the
        // BlittableProjection key emitted `(nint)_nsKey` — invalid because NSDictionary.Keys is
        // NSObject[] and Swift Int values are stored boxed as NSNumber. The fix routes BlittableProjection
        // through NSNumberUnboxExpression which emits `((Foundation.NSNumber)_nsKey).NIntValue`
        // for nint, mirroring NSNumber's typed accessors per public-type keyword.
        var keyProj = new BlittableProjection("nint");
        var valueProj = new ObjCBridgeableProjection("Foundation.NSUrl");
        var dictProj = new DictionaryProjection(keyProj, valueProj, isParameter: false);

        Assert.True(dictProj.UsesObjCContainerBridge,
            "ObjCBridgeable value forces NSDictionary bridge; otherwise the integer-key path is unreachable.");

        var plan = dictProj.GetReturnPlan("urlsBySample", ReturnStrategy.Direct);

        // Find the dictionary-population foreach line.
        var lines = plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).ToList();
        var assignLine = Assert.Single(lines, l => l.Contains("foreach (var _nsKey in"));

        // Key must unbox via NSNumber — never via the broken (nint)_nsKey cast.
        Assert.Contains("((Foundation.NSNumber)_nsKey).NIntValue", assignLine);
        Assert.DoesNotContain("(nint)_nsKey", assignLine);

        // Value must continue to use the ObjC bridge call; the fix does not regress that path.
        Assert.Contains(".Handle", assignLine);
    }

    [Theory]
    [InlineData("nint", ".NIntValue")]
    [InlineData("nuint", ".NUIntValue")]
    [InlineData("long", ".Int64Value")]
    [InlineData("ulong", ".UInt64Value")]
    [InlineData("int", ".Int32Value")]
    [InlineData("uint", ".UInt32Value")]
    [InlineData("short", ".Int16Value")]
    [InlineData("ushort", ".UInt16Value")]
    [InlineData("byte", ".ByteValue")]
    [InlineData("sbyte", ".SByteValue")]
    [InlineData("float", ".FloatValue")]
    [InlineData("double", ".DoubleValue")]
    public void DictionaryBlittableKey_ObjCBridgeReturn_UsesMatchingNSNumberAccessor(
        string keyPublicType, string expectedAccessor)
    {
        // Verifies the NSNumber accessor table covers every blittable-keyed ObjC bridge dictionary
        // shape we expect to encounter. Each public-type keyword resolves to the Foundation.NSNumber
        // property whose return type matches that keyword (no implicit narrowing, no wrong sign).
        // (Bool is exercised separately because it projects through BoolProjection, not BlittableProjection.)
        var keyProj = new BlittableProjection(keyPublicType);
        var valueProj = new ObjCBridgeableProjection("Foundation.NSUrl");
        var dictProj = new DictionaryProjection(keyProj, valueProj, isParameter: false);

        var plan = dictProj.GetReturnPlan("dict", ReturnStrategy.Direct);
        var foreachLine = plan.SetupStatements
            .OfType<MarshalStatement.Line>()
            .Select(l => l.Code)
            .Single(l => l.Contains("foreach (var _nsKey in"));

        Assert.Contains($"((Foundation.NSNumber)_nsKey){expectedAccessor}", foreachLine);
        Assert.DoesNotContain($"({keyPublicType})_nsKey", foreachLine);
    }

    [Fact]
    public void DictionaryBoolKey_ObjCBridgeReturn_UnboxesViaBoolValue()
    {
        // Swift.Bool projects as BoolProjection (its own class
        // distinct from BlittableProjection) because the P/Invoke side requires
        // [MarshalAs(UnmanagedType.U1)]. The NSNumber unbox path must include BoolProjection
        // alongside BlittableProjection so a real [Bool: URL] NSDictionary bridge return
        // emits ((Foundation.NSNumber)_nsKey).BoolValue instead of the broken (bool)_nsKey
        // NSObject cast.
        var keyProj = new BoolProjection();
        var valueProj = new ObjCBridgeableProjection("Foundation.NSUrl");
        var dictProj = new DictionaryProjection(keyProj, valueProj, isParameter: false);

        var plan = dictProj.GetReturnPlan("dict", ReturnStrategy.Direct);
        var foreachLine = plan.SetupStatements
            .OfType<MarshalStatement.Line>()
            .Select(l => l.Code)
            .Single(l => l.Contains("foreach (var _nsKey in"));

        Assert.Contains("((Foundation.NSNumber)_nsKey).BoolValue", foreachLine);
        Assert.DoesNotContain("(bool)_nsKey", foreachLine);
    }

    [Theory]
    [InlineData("nint", "Foundation.NSNumber.FromNInt(kvp.Key)")]
    [InlineData("nuint", "Foundation.NSNumber.FromNUInt(kvp.Key)")]
    [InlineData("long", "Foundation.NSNumber.FromInt64(kvp.Key)")]
    [InlineData("ulong", "Foundation.NSNumber.FromUInt64(kvp.Key)")]
    [InlineData("int", "Foundation.NSNumber.FromInt32(kvp.Key)")]
    [InlineData("uint", "Foundation.NSNumber.FromUInt32(kvp.Key)")]
    [InlineData("short", "Foundation.NSNumber.FromInt16(kvp.Key)")]
    [InlineData("ushort", "Foundation.NSNumber.FromUInt16(kvp.Key)")]
    [InlineData("byte", "Foundation.NSNumber.FromByte(kvp.Key)")]
    [InlineData("sbyte", "Foundation.NSNumber.FromSByte(kvp.Key)")]
    [InlineData("float", "Foundation.NSNumber.FromFloat(kvp.Key)")]
    [InlineData("double", "Foundation.NSNumber.FromDouble(kvp.Key)")]
    public void DictionaryBlittableKey_ObjCBridgeParameter_BoxesViaNSNumberFactory(
        string keyPublicType, string expectedBox)
    {
        // The parameter-side ToNSObject fall-through emitted
        // (Foundation.NSObject)kvp.Key, which is an invalid primitive-to-NSObject cast.
        // The fix mirrors the return-side NSNumber unbox table — primitive blittable keys
        // are boxed via the matching Foundation.NSNumber.FromXxx factory before being
        // collected into the NSDictionary's keys array.
        var keyProj = new BlittableProjection(keyPublicType);
        var valueProj = new ObjCBridgeableProjection("Foundation.NSUrl");
        var dictProj = new DictionaryProjection(keyProj, valueProj, isParameter: true);

        var plan = dictProj.GetParameterPlan("urlsByKey");
        var keysLine = plan.SetupStatements
            .OfType<MarshalStatement.Line>()
            .Select(l => l.Code)
            .Single(l => l.Contains("urlsByKeyKeys ="));

        Assert.Contains(expectedBox, keysLine);
        Assert.DoesNotContain("(Foundation.NSObject)kvp.Key", keysLine);
    }

    [Fact]
    public void DictionaryBoolKey_ObjCBridgeParameter_BoxesViaFromBoolean()
    {
        // Same parameter-side fix for the BoolProjection arm: a [Bool: URL] parameter
        // bridge must wrap the bool key in Foundation.NSNumber.FromBoolean(...) — never
        // cast bool directly to NSObject.
        var keyProj = new BoolProjection();
        var valueProj = new ObjCBridgeableProjection("Foundation.NSUrl");
        var dictProj = new DictionaryProjection(keyProj, valueProj, isParameter: true);

        var plan = dictProj.GetParameterPlan("urlsByFlag");
        var keysLine = plan.SetupStatements
            .OfType<MarshalStatement.Line>()
            .Select(l => l.Code)
            .Single(l => l.Contains("urlsByFlagKeys ="));

        Assert.Contains("Foundation.NSNumber.FromBoolean(kvp.Key)", keysLine);
        Assert.DoesNotContain("(Foundation.NSObject)kvp.Key", keysLine);
    }

    [Fact]
    public void TupleOfStringObjCRootedClass_GetReturnPlan_LiftsClassFieldFromIntPtr()
    {
        // ObjCRootedClassProjection has the same shape as
        // ClassProjection — PInvokeType=IntPtr, GetReturnPlan wraps via MarshalFromSwiftObject,
        // GetReturnElementConversion returns null. Without including it in the tuple lift gate,
        // a (String, ARView)/(String, NSObject-rooted class) return still leaked raw IntPtr
        // into the public ValueTuple. The fix routes both ClassProjection and
        // ObjCRootedClassProjection through the same MarshalFromSwiftObject lift.
        var stringProj = new StringProjection();
        var rootedProj = new ObjCRootedClassProjection("ARKit.ARView");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, rootedProj });

        var plan = tupleProj.GetReturnPlan("result", ReturnStrategy.Direct);

        var setup = plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).ToList();
        Assert.Contains(setup,
            l => l.Contains("MarshalFromSwiftObject<ARKit.ARView>(result.Item2)"));
        Assert.Contains(setup, l => l.Contains("result.Item1.ToString()"));
    }

    [Fact]
    public void OptionalTupleOfStringObjCRootedClass_GetReturnPlan_DecomposesPerElement()
    {
        // Mirror coverage with ObjCRootedClass: the carrier's tuple metadata must use the wrapper
        // type for the ObjC-rooted slot (never a raw IntPtr), and extraction passes that element
        // through self-owning rather than re-wrapping it via MarshalFromSwiftObject.
        var stringProj = new StringProjection();
        var rootedProj = new ObjCRootedClassProjection("ARKit.ARView");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, rootedProj });
        var optProj = new OptionalProjection(tupleProj);

        var plan = optProj.GetReturnPlan("resultPtr", ReturnStrategy.IndirectResult);
        var setup = RenderSetup(plan);

        Assert.Contains(setup, l => l.Contains("SwiftOptional<ValueTuple<SwiftString, ARKit.ARView>>"));
        Assert.DoesNotContain(setup, l => l.Contains("ValueTuple<SwiftString, IntPtr>"));
        Assert.Contains(setup, l => l.Contains("= _optTuple.Item2;"));
        Assert.DoesNotContain(setup, l => l.Contains("MarshalFromSwiftObject<ARKit.ARView>"));
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.ToString()"));
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.Dispose();"));
    }

    [Fact]
    public void TupleOfStringNonFrozenStruct_GetReturnPlan_LiftsHandleFieldFromIntPtr()
    {
        // NonFrozenStructProjection has the same raw-
        // pointer-tuple-slot shape as ClassProjection/ObjCRootedClassProjection — its
        // PInvokeType is IntPtr, GetReturnPlan wraps via MarshalFromSwiftObject, and
        // GetReturnElementConversion returns null. A (String, NonFrozenStruct) tuple
        // return must lift the second field, otherwise the public ValueTuple holds a
        // raw IntPtr instead of the struct wrapper.
        var stringProj = new StringProjection();
        var nfsProj = new NonFrozenStructProjection("RealityFoundation.Transform");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, nfsProj });

        var plan = tupleProj.GetReturnPlan("result", ReturnStrategy.Direct);

        var setup = plan.SetupStatements.OfType<MarshalStatement.Line>().Select(l => l.Code).ToList();
        Assert.Contains(setup,
            l => l.Contains("MarshalFromSwiftObject<RealityFoundation.Transform>(result.Item2)"));
        Assert.Contains(setup, l => l.Contains("result.Item1.ToString()"));
    }

    [Fact]
    public void OptionalTupleOfStringNonFrozenStruct_GetReturnPlan_DecomposesPerElement()
    {
        // Same shape with NonFrozenStruct — Optional<(String, NonFrozenStruct)>'s carrier metadata
        // must describe the struct slot as its wrapper type (resilient struct read through a
        // pointer), never a raw IntPtr, and extraction passes it through self-owning.
        var stringProj = new StringProjection();
        var nfsProj = new NonFrozenStructProjection("RealityFoundation.Transform");
        var tupleProj = new TupleProjection(new ITypeProjection[] { stringProj, nfsProj });
        var optProj = new OptionalProjection(tupleProj);

        var plan = optProj.GetReturnPlan("resultPtr", ReturnStrategy.IndirectResult);
        var setup = RenderSetup(plan);

        Assert.Contains(setup, l => l.Contains("SwiftOptional<ValueTuple<SwiftString, RealityFoundation.Transform>>"));
        Assert.DoesNotContain(setup, l => l.Contains("ValueTuple<SwiftString, IntPtr>"));
        Assert.Contains(setup, l => l.Contains("= _optTuple.Item2;"));
        Assert.DoesNotContain(setup, l => l.Contains("MarshalFromSwiftObject<RealityFoundation.Transform>"));
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.ToString()"));
        Assert.Contains(setup, l => l.Contains("_optTuple.Item1.Dispose();"));
    }

    // --- Fix D: B12 ObjC optional gating defers to TypeRecord ----------------

    [Fact]
    public void IsOptionalObjCBridged_PlainSwiftClassUnderUmbrella_ReturnsFalse()
    {
        // Optional<RealityKit.Entity> where RealityKit is the umbrella for RealityFoundation.
        // The actual TypeRecord (resolved via the source-module reverse remap) has no ObjC
        // flags — the gate must consult the record and return false, so the marshaller
        // routes through ClassProjection (.Payload.DangerousGetHandle()) instead of
        // ObjCBridgedProjection (?.Handle).
        //
        // This locks the contract that B12's parity-locked sibling, IsOptionalObjCBridged,
        // already expresses; the B12 emission gate now matches by deferring to the record.
        var typeDb = BuildTypeDbWithRealityFoundationEntity();
        var optionalSpec = new NamedTypeSpec("Swift.Optional",
            new NamedTypeSpec("RealityKit.Entity"));

        Assert.False(MarshallingHelpers.IsOptionalObjCBridged(optionalSpec, typeDb),
            "Plain Swift class re-exported through umbrella must not be classified as ObjC-bridged");
    }

    [Fact]
    public void IsObjCPrefixBridgeCandidate_IsTheSharedHeuristicCore()
    {
        // F10 Stage 20: the four-clause ObjC-prefix bridge heuristic now has ONE source of truth —
        // MarshallingHelpers.IsObjCPrefixBridgeCandidate — that BOTH IsOptionalObjCBridged (the
        // marshalling-decision reader) and TypeProjectionFactory's Optional/collection-element ObjC
        // fallbacks (the projection sites) call, so they can no longer drift (constraints.md
        // "IsOptionalObjCBridged parity with TypeProjectionFactory").

        // Auto-bridged Apple ObjC class with no DB record: an optional-fallback module whose type
        // name carries an ObjC class prefix (Foundation + "NS").
        Assert.True(MarshallingHelpers.IsObjCPrefixBridgeCandidate(
            new NamedTypeSpec("Foundation.NSURL")));

        // Load-bearing value-type guard: a KNOWN Apple value type that happens to carry an ObjC-style
        // prefix in an optional-fallback module is rejected — an ObjC prefix alone does not prove a
        // class, and bridging a value type would emit the wrong ARC shape.
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(
            new NamedTypeSpec("AVFoundation.AVAudioChannelCount")));

        // Not an optional-fallback module → never a candidate, regardless of name shape.
        Assert.False(MarshallingHelpers.IsObjCPrefixBridgeCandidate(
            new NamedTypeSpec("Swift.String")));
    }

    // --- Helpers --------------------------------------------------------------

    /// <summary>
    /// Renders a return plan's setup statements to their C# text so assertions can match against
    /// the owned-carrier extraction body (a <c>using</c> carrier declaration plus per-element lines).
    /// </summary>
    private static List<string> RenderSetup(MarshalPlan plan) =>
        plan.SetupStatements.Select(s => s switch
        {
            MarshalStatement.Line line => line.Code,
            MarshalStatement.Using u => $"using {u.Type} {u.Name} = {u.InitExpression};",
            MarshalStatement.Block b => b.Header,
            _ => s.ToString() ?? string.Empty,
        }).ToList();

    private static ModuleDecl BuildEmptyModuleDecl(string name) => new()
    {
        Name = name,
        ParentDecl = null,
        ModuleDecl = null,
        Properties = new List<PropertyDecl>(),
        Methods = new List<MethodDecl>(),
        Types = new List<TypeDecl>(),
        Dependencies = new List<string>(),
        Protocols = new List<ProtocolDecl>(),
    };

    private static StructDecl BuildStructDecl(ModuleDecl moduleDecl, string moduleQualifiedName)
    {
        var typeName = SwiftTypeName.FromModuleQualifiedName(moduleQualifiedName);
        return new StructDecl
        {
            Name = typeName.Name,
            SwiftTypeName = typeName,
            MangledName = "",
            IsFrozen = false,
            GenericParameters = new List<GenericArgumentDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            Conformances = new List<TypeConformance>(),
            MetadataAccessor = "",
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
        };
    }

    private static TypeDatabase BuildTypeDbWithSimdFloat3()
    {
        var typeDb = new TypeDatabase();
        var simdModule = new ModuleTypeDatabase("simd", "/usr/lib/swift/libsimd.dylib");
        simdModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("simd.simd_float3"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System.Numerics", "Vector3"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("simd.simd_float3"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDb.AddModuleDatabase(simdModule);
        return typeDb;
    }

    private static TypeDatabase BuildTypeDbWithRealityFoundationEntity()
    {
        // Mirror reality: the type's canonical record lives in RealityFoundation. Lookups
        // that come in qualified with the umbrella RealityKit. prefix flow through
        // TypeDatabase's compileImportSourceModules reverse remap to find this record.
        var typeDb = new TypeDatabase();
        var realityModule = new ModuleTypeDatabase("RealityFoundation",
            "/System/Library/Frameworks/RealityFoundation.framework/RealityFoundation");
        realityModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("RealityFoundation", "Entity"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("RealityFoundation.Entity"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,   // No ObjCBridged, no ObjCRooted, no ObjCBridgeable
                Kind = TypeRecordKind.Class,
            });
        typeDb.AddModuleDatabase(realityModule);
        return typeDb;
    }

    // ABI node helpers (mirror UmbrellaReExportTests / TypeNameAliasParserTests)

    private static Node CreateNode(
        string kind,
        string declKind = "",
        string name = "",
        string printedName = "",
        string moduleName = "TestModule",
        string mangledName = "$s")
        => new()
        {
            Kind = kind,
            DeclKind = declKind,
            Name = name,
            MangledName = mangledName,
            PrintedName = string.IsNullOrEmpty(printedName) ? name : printedName,
            ModuleName = moduleName,
            DeclAttributes = Array.Empty<string>(),
            @static = false,
            IsInternal = false,
            GenericSig = null,
            sugared_genericSig = null,
            throwing = false,
            AccessorKind = null,
            EnumRawTypeName = null,
            paramValueOwnership = null,
            hasDefaultArg = null,
            Children = Array.Empty<Node>(),
            Conformances = Array.Empty<Node>(),
            Accessors = Array.Empty<Node>(),
        };

    private static Node CreateNodeWithChildren(
        string kind, string name, string printedName, IEnumerable<Node> children,
        string moduleName = "TestModule", string mangledName = "$s")
    {
        var node = CreateNode(kind: kind, name: name, printedName: printedName,
            moduleName: moduleName, mangledName: mangledName);
        node.Children = children;
        return node;
    }

    private static ParserFixture CreateParserWithNodes(params Node[] nodes)
    {
        var importNode = CreateNode(kind: "Import", moduleName: "TestModule", name: "TestModule");
        var allNodes = new List<Node> { importNode };
        allNodes.AddRange(nodes);

        var root = new ABIRootNode
        {
            ABIRoot = new RootNode
            {
                Kind = "Root",
                Name = "Root",
                PrintedName = "Root",
                Children = allNodes
            }
        };

        var filePath = Path.GetTempFileName();
        File.WriteAllText(filePath, Newtonsoft.Json.JsonConvert.SerializeObject(root));

        var parser = new SwiftABIParser(
            filePath,
            new TypeDatabase(),
            CreateEmptyDemanglingResults(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            SwiftInterfaceFacts.Empty);

        return new ParserFixture(parser, filePath);
    }

    private sealed class ParserFixture : System.IDisposable
    {
        public ParserFixture(SwiftABIParser parser, string filePath)
        {
            Parser = parser;
            _filePath = filePath;
        }

        public SwiftABIParser Parser { get; }
        private readonly string _filePath;

        public void Dispose()
        {
            if (File.Exists(_filePath))
                File.Delete(_filePath);
        }
    }

    private static BindingsGeneration.Demangling.DemanglingResults CreateEmptyDemanglingResults()
    {
        var ctor = typeof(BindingsGeneration.Demangling.DemanglingResults).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            new[] { typeof(BindingsGeneration.Demangling.IReduction[]), typeof(HashSet<string>) },
            modifiers: null)!;
        return (BindingsGeneration.Demangling.DemanglingResults)ctor.Invoke(
            new object[] { Array.Empty<BindingsGeneration.Demangling.IReduction>(), new HashSet<string>() });
    }
}
