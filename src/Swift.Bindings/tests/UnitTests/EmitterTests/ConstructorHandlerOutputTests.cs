// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

public class ConstructorHandlerOutputTests
{
    [Fact]
    public void Emit_GenericConstructor_SkippedBecauseCSharpDoesNotSupportGenericConstructors()
    {
        // C# does not allow generic constructors. A Swift init<T: Loadable>() on a
        // non-generic type has method-own generic params that can't be represented.
        // This gate is in MemberValidationPipeline.
        var typeDatabase = CreateTypeDatabase();
        RegisterProtocol(typeDatabase, "TestModule.Loadable", TypeRecordFlags.None);

        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            genericParameters: new List<GenericArgumentDecl>
            {
                CreateGenericArgumentWithProtocolConformance("T", "TestModule.Loadable")
            });

        var pipeline = new MemberValidationPipeline(typeDatabase);
        var result = pipeline.ValidateMethodEmission(constructor, null);

        Assert.False(result.ShouldEmit);
        Assert.Equal(SkipReason.UnsupportedSignature, result.Reason);
        Assert.Contains("generic constructors", result.Details!);
    }

    [Fact]
    public void Emit_ThrowingConstructor_EmitsSwiftErrorPath()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl, throws: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("ref SwiftError swiftError", csOutput);
        Assert.Contains("if (swiftError.Value != null)", csOutput);
        // Untyped throws uses SwiftMarshal.ThrowSwiftError (consolidates description read + release + throw)
        Assert.Contains("SwiftMarshal.ThrowSwiftError", csOutput);
        Assert.Contains("SBW_GetErrorDescription", csOutput);
        Assert.Contains("SBW_ReleaseError", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithEscapingClosure_EmitsClosureMarshalling()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);

        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));

        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("callback", closureType, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Cdecl closure wrapper: separate IntPtr params instead of SwiftClosureData
        Assert.DoesNotContain("SwiftClosureData", csOutput);
        Assert.Contains("GCHandle callbackHandle", csOutput);
        Assert.Contains("IntPtr callbackFuncPtr", csOutput);
        Assert.Contains("IntPtr callbackContext", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithUnknownParameterType_SkipsEmission()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("unknown", new NamedTypeSpec("Missing.Type"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // No binding code emitted — only unsupported comment
        Assert.DoesNotContain("public", csOutput);
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Equal(string.Empty, swiftOutput);
    }

    #region Class Constructor Tests

    [Fact]
    public void Emit_ClassConstructor_EmitsProperConstructorSignature()
    {
        // Non-frozen class constructors should emit as C# constructors, not instance methods.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("age", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Should emit constructor syntax, not instance method
        Assert.Contains("public Animal(", csOutput);
        // Should NOT contain a return type (constructors don't have one)
        Assert.DoesNotContain("TestModule.Animal Init(", csOutput);
        Assert.DoesNotContain("return ", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructor_ReturnsIntPtrDirectly()
    {
        // Class constructors return a pointer in-register (not via SwiftIndirectResult).
        // The P/Invoke returns IntPtr, which is stored in _handle via SwiftClassHandle.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("_handle = new SwiftClassHandle<Animal>", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);
        Assert.Contains("var result =", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructor_PassesThickMetatypeInTheSelfRegister()
    {
        // A class allocating initializer is @convention(method) over @thick Self.Type: the
        // metatype arrives in the self register and is the metadata swift_allocObject
        // allocates the instance from. Leaving it unset hands the allocator whatever the
        // register happened to hold, so the object is built with a garbage isa and the crash
        // lands later, on the first release.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Pin the contract, not merely the presence of the words: the metatype must be a declared
        // P/Invoke parameter typed SwiftSelf (which is what puts it in the self register rather than
        // in the next general-purpose argument register) on a CallConvSwift import, and the call must
        // pass this class's own metadata into it.
        Assert.Contains("CallConvSwift", csOutput);
        Assert.Contains("SwiftSelf _metatypeSelf)", csOutput);
        Assert.Contains(
            "new SwiftSelf((void*)SwiftObjectHelper<Animal>.GetTypeMetadata().Handle)", csOutput);
    }

    [Fact]
    public void Emit_StructConstructor_DoesNotPassMetatypeSelf()
    {
        // The positive control for the gate above. A struct initializer returns its value
        // directly and allocates nothing, so it carries no metatype — adding one here would
        // shift every argument the callee reads.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("GetTypeMetadata()", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructorWithEnumParam_UsesIntPtrInPInvoke()
    {
        // Class constructors should handle enum parameters the same as struct constructors.
        var typeDatabase = CreateTypeDatabase();
        RegisterEnumType(typeDatabase);
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("variant", new NamedTypeSpec("TestModule.Variant"), moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("public Animal(", csOutput);
        // The partial P/Invoke should use IntPtr for the enum parameter
        var lines = csOutput.Split('\n');
        var externLine = Array.Find(lines, line => line.Contains("partial", StringComparison.Ordinal) && line.Contains("PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(externLine);
        Assert.Contains("IntPtr", externLine);
        Assert.DoesNotContain("TestModule.Variant", externLine);
    }

    [Fact]
    public void Emit_FailableClassConstructor_WithoutWrapper_IsSkippedFailClosed()
    {
        // A failable CLASS init with no @_cdecl wrapper available (Direct generation mode — no
        // companion wrapper library) has no ABI-correct surface. The Swift-native allocating
        // initializer returns Optional<Self> as a single nullable pointer in the result register and
        // reads Self.Type metadata from the Swift self/context register; a plain CallConvSwift P/Invoke
        // shaped as an indirect result (SwiftIndirectResult) supplies neither, so the emitted factory
        // would allocate an object with a garbage metadata pointer and fault at the first swift_release.
        // The generator must therefore SKIP the member with an Unsupported comment and emit no factory
        // and no indirect-result P/Invoke. (A WRAPPED failable class init returns the nullable retained
        // pointer directly and is correct — see the XCFramework-mode test below.)
        var typeDatabase = CreateTypeDatabase(); // Direct mode: no AsyncLibraryName => no wrapper available
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl, isFailable: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Skipped with a loud Unsupported comment naming the failable-class-without-wrapper reason...
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("failable class initializer without a @_cdecl wrapper", csOutput);
        // ...and emits NO factory and NONE of the broken direct/indirect P/Invoke shape.
        Assert.DoesNotContain("public static bool TryCreate(", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);
    }

    [Fact]
    public void Emit_FailableClassConstructor_WithWrapper_EmitsTryCreateReturningIntPtr()
    {
        // In XCFramework mode a failable CLASS init routes through a @_cdecl free-function wrapper that
        // returns the nullable retained class pointer DIRECTLY (UnsafeMutableRawPointer?, nil == failure)
        // — exactly like a non-failable class init. So the C# P/Invoke must return IntPtr (Zero ==
        // failure) with NO leading resultPtr buffer and NO SwiftIndirectResult, and TryCreate gates on
        // IntPtr.Zero. A leading resultPtr or an indirect-result shape would shift every real argument
        // one slot and corrupt the call.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings"; // XCFramework mode => wrapper available
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl, isFailable: true);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("public static bool TryCreate(", csOutput);
        Assert.Contains("out Animal result)", csOutput);
        Assert.DoesNotContain("SwiftIndirectResult", csOutput);

        // The P/Invoke returns IntPtr directly (no leading resultPtr buffer param).
        var lines = csOutput.Split('\n');
        var externLine = Array.Find(lines, l => l.Contains("partial", StringComparison.Ordinal) && l.Contains("PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(externLine);
        Assert.Contains("IntPtr", externLine);
        Assert.DoesNotContain("resultPtr", externLine);
        Assert.DoesNotContain("SwiftIndirectResult", externLine);

        // Failure is signalled by a null pointer, not an Optional enum tag.
        Assert.Contains("IntPtr.Zero", csOutput);

        // Behavioral invariant (the whole-binding compile break this fixes): the local that receives
        // the @_cdecl P/Invoke's IntPtr return must be a DIFFERENT identifier than the `out ... result`
        // parameter. If they share the name, the IntPtr local shadows the out param (CS0136), the out
        // param is never assigned (CS0177), and `result = new Animal((SwiftHandle)result)` mixes the two
        // types (CS0029). Extract the declared P/Invoke-return local and assert it is not "result".
        var pinvokeReturnLine = Array.Find(
            lines,
            l => l.TrimStart().StartsWith("var ", StringComparison.Ordinal) && l.Contains("= PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(pinvokeReturnLine);
        var declaredLocal = pinvokeReturnLine!.TrimStart().Substring("var ".Length).Split(' ')[0];
        Assert.NotEqual("result", declaredLocal);
        // The out param itself is still assigned from the constructed instance and the nil sentinel.
        Assert.Contains("result = new Animal(", csOutput);
        Assert.Contains("result = default!;", csOutput);
    }

    [Fact]
    public void Emit_FailableClassConstructor_WithWrapper_ParamProjectedAsResult_AllIdentifiersDistinct()
    {
        // The wrapped failable-class TryCreate body coordinates THREE identifiers: the projected
        // constructor parameter(s), the `out T` result parameter, and the local that receives the
        // @_cdecl P/Invoke's IntPtr return. The out-param name and the P/Invoke return local are chosen
        // in two SEPARATE places that must agree — WrapperEmitter.FailableFactory (out param) and
        // MethodMarshalPlanBuilder.ResolveReturnLocalName (P/Invoke local). The general WithWrapper test
        // above only covers the common case where no parameter is named "result". This pins the
        // adversarial case where a Swift init? parameter ITSELF projects to the C# name "result",
        // forcing BOTH the out param and the P/Invoke local to step away from it. If either site failed
        // to avoid the collision the generated binding would not compile (CS0100 duplicate parameter,
        // CS0136 shadowing, or CS0177 unassigned out).
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings"; // XCFramework mode => wrapper available
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                // PrivateName "result" projects straight to the C# parameter name "result".
                CreateArgument("result", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        var lines = csOutput.Split('\n');

        // The factory is still emitted.
        var sigLine = Array.Find(lines, l => l.Contains("static bool TryCreate(", StringComparison.Ordinal));
        Assert.NotNull(sigLine);

        // The input parameter keeps its projected name "result"...
        Assert.Matches(@"TryCreate\([^,)]*\bresult\b", sigLine!);
        // ...and the out parameter stepped to a DISTINCT identifier (not the colliding "result").
        var outMatch = System.Text.RegularExpressions.Regex.Match(sigLine!, @"out Animal (\w+)\)");
        Assert.True(outMatch.Success, $"could not find `out Animal <name>)` in: {sigLine}");
        var outName = outMatch.Groups[1].Value;
        Assert.NotEqual("result", outName);

        // The P/Invoke return local is distinct from BOTH the input param and the out param.
        var pinvokeReturnLine = Array.Find(
            lines,
            l => l.TrimStart().StartsWith("var ", StringComparison.Ordinal) && l.Contains("= PInvoke_", StringComparison.Ordinal));
        Assert.NotNull(pinvokeReturnLine);
        var pinvokeLocal = pinvokeReturnLine!.TrimStart().Substring("var ".Length).Split(' ')[0];
        Assert.NotEqual("result", pinvokeLocal);
        Assert.NotEqual(outName, pinvokeLocal);

        // The out param is assigned on both the success and nil paths, keyed off the P/Invoke local.
        Assert.Contains($"{outName} = new Animal(", csOutput);
        Assert.Contains($"{outName} = default!;", csOutput);
        Assert.Contains($"if ({pinvokeLocal} == IntPtr.Zero)", csOutput);
    }

    [Fact]
    public void Emit_FailableClassConstructorWithConventionCClosure_IsSkippedAsUnsupported()
    {
        // A constructor taking a non-optional @convention(c) closure has no ABI-correct surface: the
        // closure parameter denies it a native thunk AND blocks the @_cdecl constructor wrapper, so
        // the only path left is a direct CallConvSwift call against the raw init symbol — which cannot
        // deliver an allocating class init's hidden metatype nor decode a failable Optional<Self>
        // return. Emitting it would compile but fault at runtime (class: nil/SIGSEGV; frozen struct:
        // uninitialized read), so the generator must SKIP the member with an Unsupported comment and
        // emit no factory. This pins that skip — it is the root-cause fix for the whole-binding
        // compile break (CS0103) and the runtime ABI fault alike.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl, typeDatabase);

        // @convention(c) (Int) -> Void  (NON-optional)
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        var conventionAttr = new TypeSpecAttribute("convention");
        conventionAttr.Parameters.Add("c");
        closureType.Attributes.Add(conventionAttr);

        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("callback", closureType, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // The member is skipped with a loud Unsupported comment naming the exact reason...
        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("@convention(c) closure parameter cannot be ABI-correctly bound in a constructor", csOutput);
        // ...and NO callable factory is emitted (the broken direct-call path never reaches the body).
        Assert.DoesNotContain("public static bool TryCreate(", csOutput);
        Assert.DoesNotContain("Marshal.GetFunctionPointerForDelegate", csOutput);
    }

    [Fact]
    public void Emit_NonFailableClassConstructorWithConventionCClosure_IsSkipped()
    {
        // The skip is keyed on the unbindable @convention(c)-closure shape, NOT on failability: an
        // allocating class init's hidden metatype cannot be delivered over the direct CallConvSwift
        // call regardless of whether the init returns Self or Optional<Self>. A non-failable init of
        // this shape is therefore just as broken and must be skipped too. This pins that the skip is
        // not accidentally gated on init? (failable) — a plain `init` with a non-optional conv-c
        // closure is skipped the same way.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("EagerLoader", moduleDecl, typeDatabase);

        // @convention(c) (Int) -> Void  (NON-optional)
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        var conventionAttr = new TypeSpecAttribute("convention");
        conventionAttr.Parameters.Add("c");
        closureType.Attributes.Add(conventionAttr);

        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: false,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("callback", closureType, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("@convention(c) closure parameter cannot be ABI-correctly bound in a constructor", csOutput);
        // No factory of either failability shape, and the closure never reaches marshalling.
        Assert.DoesNotContain("public static bool TryCreate(", csOutput);
        Assert.DoesNotContain("Marshal.GetFunctionPointerForDelegate", csOutput);
        Assert.DoesNotContain("_convC_", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithConventionCClosureAndDebugParam_IsStillSkipped()
    {
        // A #file/#line debug parameter triggers EmitDebugParamWrapper, which sets
        // UsesWrapperLibrary on the constructor. The conv-c skip must run BEFORE that wrapper and
        // must NOT gate on UsesWrapperLibrary — otherwise a constructor that pairs a non-optional
        // @convention(c) closure with a defaulted debug parameter would slip past the skip, emit a
        // wrapper with no slot-save declaration, and reference an undeclared `_delSaved` (CS0103) /
        // fault at runtime. This pins that a debug parameter cannot route the broken shape around the
        // skip.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Logger", moduleDecl, typeDatabase);

        // @convention(c) (Int32) -> Int32  (NON-optional)
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int32") }),
            returnType: new NamedTypeSpec("Swift.Int32"));
        var conventionAttr = new TypeSpecAttribute("convention");
        conventionAttr.Parameters.Add("c");
        closureType.Attributes.Add(conventionAttr);

        // #file debug parameter (HasDefaultArg + StaticString) — drives EmitDebugParamWrapper, which
        // sets UsesWrapperLibrary before the constructor reaches the (former) late skip gate.
        var debugArg = new ArgumentDecl
        {
            SwiftTypeSpec = new NamedTypeSpec("Swift.StaticString"),
            Name = "file",
            PrivateName = "file",
            HasDefaultArg = true,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };

        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("validate", closureType, moduleDecl),
                debugArg
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("@convention(c) closure parameter cannot be ABI-correctly bound in a constructor", csOutput);
        Assert.DoesNotContain("public static bool TryCreate(", csOutput);
        // The broken shape's tell — a slot restore referencing a never-declared save local.
        Assert.DoesNotContain("_delSaved", csOutput);
    }

    [Fact]
    public void Emit_MultiClosureConstructorWithConventionCClosure_AbiJsonSourced_IsSkipped()
    {
        // ABI-JSON-sourced closure specs do NOT carry the @convention(c) attribute, so the only
        // signal is the demangled CFunctionPointer node. The per-parameter classifier disables that
        // mangled fallback when a method has >1 closure (the node could belong to a different
        // parameter), so a constructor with TWO closures where one is non-optional @convention(c)
        // would slip past a count-limited check. The skip instead uses the whole-method
        // MethodHasConventionCClosure signal (count-independent), gated on the presence of a
        // non-optional closure parameter, so the multi-closure shape is still skipped. The mangled
        // name below is a real allocating-init symbol for
        //   init?(_ validate: @convention(c) (Int32) -> Int32, onDone done: (Int32) -> Void)
        // (the XC node is the @convention(c) closure, XE the Swift one); neither C# closure spec
        // carries the convention attribute here, mirroring ABI JSON.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("MultiClosureLoader", moduleDecl, typeDatabase);

        var convCClosure = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int32") }),
            returnType: new NamedTypeSpec("Swift.Int32"));
        var swiftClosure = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int32") }),
            returnType: TupleTypeSpec.Empty);

        var constructor = CreateConstructorDeclForClass(
            "init",
            parentDecl,
            moduleDecl,
            isFailable: true,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("validate", convCClosure, moduleDecl),
                CreateArgument("done", swiftClosure, moduleDecl)
            },
            // Real allocating-init mangled name carrying a CFunctionPointer (XC) closure + a Swift
            // (XE) closure — the only conv-c signal for an ABI-JSON-sourced multi-closure init.
            mangledName: "$s2mc18MultiClosureLoaderC_6onDoneACSgs5Int32VAGXC_yAGXEtcfC");

        // Sanity: the whole-method signal must actually fire for this symbol, else the test would
        // pass vacuously regardless of the skip's count-independence.
        var closureHandler = new ClosureHandler(typeDatabase);
        Assert.True(closureHandler.MethodHasConventionCClosure(constructor.MangledName),
            "the mangled name must demangle to a CFunctionPointer node");

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("// Unsupported:", csOutput);
        Assert.Contains("@convention(c) closure parameter cannot be ABI-correctly bound in a constructor", csOutput);
        Assert.DoesNotContain("public static bool TryCreate(", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithOptionalExistential_KnownProtocol_NotBlockedByExistentialGuard()
    {
        // P3: Exercises ConstructorHandler.Emit() constructor existential bypass path (line 167).
        // Optional<any KnownProtocol> should NOT set hasExistentialArg —
        // the constructor proceeds past the existential guard to normal emission.
        // It may still produce empty output due to SignatureHandler placeholder resolution
        // with a minimal TypeDatabase — this test verifies the guard path, not full emission.
        var typeDatabase = CreateTypeDatabaseWithOptionalAndProtocol();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var optionalExistentialSpec = new NamedTypeSpec("Swift.Optional");
        var existentialInner = new NamedTypeSpec("TestModule.Drawable") { IsAny = true };
        optionalExistentialSpec.GenericParameters.Add(existentialInner);

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("renderer", optionalExistentialSpec, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // The constructor should NOT be handled by ExistentialBypass (no "ExistentialBypass" report).
        // It may still produce empty output if the signature has unresolvable types
        // (UnsupportedSignature), but NOT because of UnsupportedExistential.
        // Verify it does NOT emit an ExistentialBypass wrapper pattern.
        Assert.DoesNotContain("ExistentialBypass", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithOptionalExistential_UnknownProtocol_Skipped()
    {
        // P3: Constructor with Optional<any UnknownProtocol> — no TypeRecord registered.
        // hasExistentialArg is set, triggering ExistentialBypass or skip.
        var typeDatabase = CreateTypeDatabaseWithOptional();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Widget", moduleDecl);

        // Register the parent type
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: parentDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Widget"),
                SwiftTypeName = parentDecl.SwiftTypeName,
                MetadataAccessor = "$s10TestModule6WidgetVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            })
        });

        var optionalExistentialSpec = new NamedTypeSpec("Swift.Optional");
        var existentialInner = new NamedTypeSpec("TestModule.UnknownProtocol") { IsAny = true };
        optionalExistentialSpec.GenericParameters.Add(existentialInner);

        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("renderer", optionalExistentialSpec, moduleDecl)
            });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // Constructor is skipped — no "public Widget(" constructor emitted
        Assert.DoesNotContain("public Widget(", csOutput);
    }

    #endregion

    #region @_cdecl Constructor Wrapper Integration Tests

    [Fact]
    public void Emit_PrimaryConstructor_EmitsCdeclSwiftWrapper()
    {
        // Primary constructors (not default-param overloads) must also get @_cdecl wrappers
        // when the type requires it for ABI safety (e.g., frozen struct with float fields).
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // Swift output should contain the @_cdecl wrapper
        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_TestModule_Point_init_", swiftOutput);
        Assert.Contains("resultPtr.assumingMemoryBound(to: TestModule.Point.self).initialize(to: result)", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryClassConstructor_UsesNativeThunk()
    {
        // Class constructors are thunked (not @_cdecl) — allocating init returns pointer
        // in x0 (no indirect result). Thunk puts metatype in x20 via metadata accessor.
        // Non-frozen struct params are passed as pointers (single register) — thunk-safe.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        RegisterNonFrozenStruct(typeDatabase, "TestModule.Config");
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("config", new NamedTypeSpec("TestModule.Config"), moduleDecl)
            });

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // No @_cdecl wrapper emitted — thunk handles the ABI bridging in assembly
        Assert.DoesNotContain("@_cdecl(\"", swiftOutput);
        // C# P/Invoke uses CallConvCdecl (targets the thunk, not raw Swift symbol)
        Assert.Contains("CallConvCdecl", csOutput);
        // Thunk symbol in the P/Invoke entry point
        Assert.Contains("thunk_", csOutput);
    }

    [Fact]
    public void Emit_ClassConstructorWithClosureParam_FallsBackToCdecl()
    {
        // Class constructors with closure params can't be thunked (closures need Swift
        // adapter code). Falls back to @_cdecl wrapper.
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Animal", moduleDecl, typeDatabase);
        var closureType = new ClosureTypeSpec(
            arguments: new TupleTypeSpec(new List<TypeSpec> { new NamedTypeSpec("Swift.Int") }),
            returnType: TupleTypeSpec.Empty);
        closureType.Attributes.Add(new TypeSpecAttribute("escaping"));
        var constructor = CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("handler", closureType, moduleDecl)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_TestModule_Animal_init_", swiftOutput);
        Assert.Contains("Unmanaged.passRetained(result).toOpaque()", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructorWithParam_CdeclWrapperIncludesParam()
    {
        // Frozen struct with float fields → ABI-unsafe → @_cdecl required
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("x", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("_ x: Int", swiftOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_CSharpUsesCdeclCallingConvention()
    {
        // When @_cdecl wrapper is emitted (ABI-unsafe type), C# P/Invoke should NOT use CallConvSwift
        var typeDatabase = CreateTypeDatabaseWithFloatStruct();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        // With @_cdecl wrapper, the P/Invoke should reference the wrapper library
        Assert.Contains("SBW_TestModule_Point_init_", csOutput);
        // Should NOT have CallConvSwift — the wrapper uses C calling convention
        Assert.DoesNotContain("CallConvSwift", csOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_FrozenStruct_UsesCdeclWrapper()
    {
        // Frozen struct constructor → @_cdecl wrapper required
        // (SwiftIndirectResult + Mono JIT crash)
        var typeDatabase = CreateTypeDatabase();
        typeDatabase.AsyncLibraryName = "TestModuleSwiftBindings";
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (csOutput, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        // @_cdecl wrapper should be emitted for all frozen struct constructors
        Assert.Contains("@_cdecl(\"", swiftOutput);
        Assert.Contains("SBW_", swiftOutput);
        // C# P/Invoke should use CallConvCdecl
        Assert.Contains("CallConvCdecl", csOutput);
    }

    [Fact]
    public void Emit_PrimaryConstructor_NoAsyncLibrary_NoCdeclWrapper()
    {
        // Without xcframework mode (no AsyncLibraryName), no @_cdecl wrapper.
        var typeDatabase = CreateTypeDatabase();
        // AsyncLibraryName is null — not in xcframework mode
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var constructor = CreateConstructorDecl("init", parentDecl, moduleDecl);

        var (_, swiftOutput) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("@_cdecl(\"", swiftOutput);
        Assert.DoesNotContain("SBW_", swiftOutput);
    }

    [Fact]
    public void Emit_TwoConstructorsCollidingOnProjectedKey_BothRecovered_NoUnsupportedComment()
    {
        // Two Swift constructors with different argument labels project to the same C#
        // constructor signature (labels are stripped from the projected dedup key,
        // parameter types are kept). Both are recovered as label-named static factories, so
        // nothing is dropped — and in particular no `// Unsupported: method 'init' (C#
        // signature collides …)` comment is written to the C# source. That comment would land
        // directly above whatever the emitter writes next and read as if it applied to the
        // member that follows, so its absence is asserted independently of the recovery.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Widget", moduleDecl, typeDatabase);

        // init(a: Int) and init(b: Int) — different labels, same projected key (`ctor(System.Int64)`).
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("a", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("b", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        var csOutput = EmitClass(parentDecl, typeDatabase);

        // Neither initializer is fully positional, so neither owns the bare constructor slot:
        // both bind, each under the label that distinguishes it.
        Assert.Contains("static Widget CreateWithA(", csOutput);
        Assert.Contains("static Widget CreateWithB(", csOutput);

        Assert.DoesNotContain("// Unsupported: method 'init'", csOutput);
    }

    [Fact]
    public void Emit_TwoConstructorsCollidingOnProjectedKey_BothRecoveredAsFactories_NoDuplicateCtor()
    {
        // Collision-safety pin for the non-failable constructor projected-key collision. Both
        // initializers survive as static factories, the drop that used to be recorded as a
        // DuplicateSignature skip is gone, and the rename is recorded in report.json's
        // disambiguation ledger instead — which is the lane the compile gate's overload-name
        // policy reads. Neither member may emit a `public Widget(long)`: two of those would be
        // CS0111, and one would silently make a re-ordered interface pick which init "wins".
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Widget", moduleDecl, typeDatabase);

        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("a", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("b", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });

        ReportCollector.Reset();
        ReportCollector.Start(moduleDecl);
        var csOutput = EmitClass(parentDecl, typeDatabase);
        var report = ReportCollector.Complete();
        ReportCollector.Reset();
        Assert.NotNull(report);

        // No constructor at all — every member of this family is label-named. That includes the
        // native-int convenience sugar: a factory-recovered init has no constructor to chain to, so
        // emitting `public Widget(int) : this((nint)…)` here would name one the type never declares.
        Assert.Equal(0, CountOccurrences(csOutput, "public Widget("));
        Assert.Equal(1, CountOccurrences(csOutput, "static Widget CreateWithA("));
        Assert.Equal(1, CountOccurrences(csOutput, "static Widget CreateWithB("));

        // Nothing is dropped any more...
        Assert.DoesNotContain(report.SkippedItems,
            i => i.Name == "init" && i.Reason == SkipReason.DuplicateSignature);
        // ...and both recoveries are visible to the overload-name policy, under names that
        // are not a bare numeric suffix.
        Assert.Contains(report.OverloadRenames, r => r.EmittedName == "CreateWithA");
        Assert.Contains(report.OverloadRenames, r => r.EmittedName == "CreateWithB");
    }

    [Fact]
    public void Emit_CollidingConstructors_PositionalMemberKeepsTheConstructor_RegardlessOfDeclarationOrder()
    {
        // Ownership of the bare constructor slot is decided by CONTENT — the one fully
        // positional initializer takes it — not by which member the interface happens to
        // declare first. The labeled init is declared FIRST here, so an order-based rule would
        // hand it `public Widget(long)` and make a re-ordered `.swiftinterface` silently
        // re-point every existing `new Widget(x)` call at the other Swift initializer.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Widget", moduleDecl, typeDatabase);

        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                CreateArgument("labeled", new NamedTypeSpec("Swift.Int"), moduleDecl)
            });
        CreateConstructorDeclForClass("init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl>
            {
                // An empty label with a real private name is Swift's `init(_ value: Int)`.
                new ArgumentDecl
                {
                    SwiftTypeSpec = new NamedTypeSpec("Swift.Int"),
                    Name = string.Empty,
                    PrivateName = "value",
                    IsInOut = false,
                    IsGeneric = false,
                    ParentDecl = null,
                    ModuleDecl = moduleDecl
                }
            });

        var csOutput = EmitClass(parentDecl, typeDatabase);

        // The positional member owns the bare constructor slot, so exactly one `public Widget(`
        // declares the primary `nint` signature; the second is its int convenience overload, which
        // chains to that primary rather than declaring a construction path of its own.
        var constructorLines = csOutput.Split('\n')
            .Where(line => line.Contains("public Widget("))
            .ToList();
        Assert.Equal(2, constructorLines.Count);
        Assert.Single(constructorLines, line => !line.Contains(": this("));
        Assert.Single(constructorLines, line => line.Contains(": this("));
        Assert.Contains("static Widget CreateWithLabeled(", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithBaselineAsyncClosure_SkippedWholeNoPlaceholderPInvoke()
    {
        // A baseline-shaped async closure is a SUPPORTED closure shape, so member
        // validation admits it and the unsupported-closure tombstone never sees it.
        // What it needs is the (context, startFunc) bridge, whose Swift adapter is
        // only rendered for a member promoted to an async @_cdecl method wrapper —
        // which a constructor never is. Without the handler-layer guard the wrapper
        // signature still projects a real delegate type (so the placeholder check
        // cannot see it) while the P/Invoke degrades the parameter to the
        // unsupported-type placeholder inside a [LibraryImport]. The member must be
        // dropped whole, with an honest skip, rather than half-emitted.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, new NamedTypeSpec("Swift.Int"))
        {
            IsAsync = true,
            Throws = true
        };
        var constructor = CreateConstructorDecl(
            "init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("handler", closure, moduleDecl) });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("LibraryImport", csOutput);
        Assert.DoesNotContain("public Point(", csOutput);
        Assert.Contains("unbridgeable async closure parameter", csOutput);
    }

    [Fact]
    public void Emit_ConstructorWithSyncClosure_StillEmits()
    {
        // Control for the guard above: an ordinary synchronous closure never reaches
        // the async bridge, so the constructor must keep binding.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Point", moduleDecl);
        var closure = new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty);
        var constructor = CreateConstructorDecl(
            "init", parentDecl, moduleDecl,
            parameters: new List<ArgumentDecl> { CreateArgument("handler", closure, moduleDecl) });

        var (csOutput, _) = EmitConstructor(constructor, typeDatabase);

        Assert.DoesNotContain("unbridgeable async closure parameter", csOutput);
        Assert.Contains("public Point(", csOutput);
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    #endregion

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithFloatStruct()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Point"),
                MetadataAccessor = "$s10TestModule5PointVMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static void RegisterNonFrozenStruct(TypeDatabase typeDatabase, string typeName)
    {
        var swiftTypeName = SwiftTypeName.FromModuleQualifiedName(typeName);
        var shortName = typeName.Split('.')[1];
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: swiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", shortName),
                SwiftTypeName = swiftTypeName,
                MetadataAccessor = $"$s10TestModule{shortName.Length}{shortName}VMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Struct
            })
        });
    }

    private static void RegisterProtocol(TypeDatabase typeDatabase, string protocolName, TypeRecordFlags flags)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName(protocolName), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", protocolName.Split('.')[1]),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName(protocolName),
                MetadataAccessor = "$s10TestModule8ProtocolPAAWP",
                Flags = flags,
                Kind = TypeRecordKind.Protocol
            })
        });
    }

    private static ModuleDecl CreateModuleDecl(string name)
    {
        return new ModuleDecl
        {
            Name = name,
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static StructDecl CreateFrozenStructDecl(string name, ModuleDecl moduleDecl)
    {
        var structDecl = new StructDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}VN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl,
            IsFrozen = true,
            MetadataAccessor = "$s10TestModule5PointVMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

    private static MethodDecl CreateConstructorDecl(
        string name,
        StructDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        List<ArgumentDecl>? parameters = null,
        List<GenericArgumentDecl>? genericParameters = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
        {
            signature.AddRange(parameters);
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule5PointV{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = signature,
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static ArgumentDecl CreateArgument(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            SwiftTypeSpec = typeSpec,
            Name = name,
            PrivateName = name,
            IsInOut = false,
            IsGeneric = typeSpec is NamedTypeSpec nts && nts.Name == "T",
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
    }

    private static GenericArgumentDecl CreateGenericArgumentWithProtocolConformance(string typeName, string protocolName)
    {
        return new GenericArgumentDecl(
            TypeName: typeName,
            SugaredTypeName: typeName,
            GenericConformances: new List<GenericParameterConformance>
            {
                new GenericParameterConformance(
                    Path: new[] { typeName },
                    ConformanceTarget: SwiftTypeName.FromModuleQualifiedName(protocolName),
                    Kind: ConformanceKind.Protocol)
            },
            AssosiatedTypeConformances: new List<GenericParameterConformance>());
    }

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl, TypeDatabase typeDatabase)
    {
        var classDecl = new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"{moduleDecl.Name}.{name}"),
            MangledName = $"$s{moduleDecl.Name.Length}{moduleDecl.Name}{name.Length}{name}CN",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Operators = new List<OperatorDecl>(),
            Subscripts = new List<SubscriptDecl>(),
            GenericParameters = new List<GenericArgumentDecl>(),
            Conformances = new List<TypeConformance>(),
            ParentDecl = moduleDecl,
            ModuleDecl = moduleDecl
        };
        moduleDecl.Types.Add(classDecl);

        // Register the class type in the TypeDatabase
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: classDecl.SwiftTypeName, record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", name),
                SwiftTypeName = classDecl.SwiftTypeName,
                MetadataAccessor = $"$s10TestModule{name.Length}{name}CMa",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Class
            })
        });

        return classDecl;
    }

    private static MethodDecl CreateConstructorDeclForClass(
        string name,
        ClassDecl parentDecl,
        ModuleDecl moduleDecl,
        bool throws = false,
        bool isFailable = false,
        List<ArgumentDecl>? parameters = null,
        List<GenericArgumentDecl>? genericParameters = null,
        string? mangledName = null)
    {
        var signature = new List<ArgumentDecl>
        {
            CreateArgument(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
        };
        if (parameters != null)
        {
            signature.AddRange(parameters);
        }

        var method = new MethodDecl
        {
            Name = name,
            MangledName = mangledName ?? $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}C{name}yACyF",
            MethodType = MethodType.Static,
            IsConstructor = true,
            IsFailable = isFailable,
            CSSignature = signature,
            GenericParameters = genericParameters ?? new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = throws,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(method);
        return method;
    }

    private static void RegisterEnumType(TypeDatabase typeDatabase)
    {
        typeDatabase.AddOutOfModuleTypes(new[]
        {
            (identifier: SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"), record: new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Variant"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Variant"),
                MetadataAccessor = "$s10TestModule7VariantOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                Kind = TypeRecordKind.Enum
            })
        });
    }

    private static TypeDatabase CreateTypeDatabaseWithOptionalAndProtocol()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IDrawable"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Drawable"),
                MetadataAccessor = "$s10TestModule8DrawablePAAWP",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static TypeDatabase CreateTypeDatabaseWithOptional()
    {
        var typeDatabase = new TypeDatabase();

        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.NIntType,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftOptional"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Optional"),
                MetadataAccessor = "$sSqMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Enum
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var module = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(MethodDecl methodDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        // An explicit ModuleEmissionContext, so the dedup registries hold this test's emission only.
        var context = new TypeHandlerContext(null, new(), null, EmissionContext: new ModuleEmissionContext());
        handler.Emit(csWriter, swiftWriter, env, conductor, context);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    /// <summary>
    /// Drives the full ClassHandler.Marshal + Emit path so IHandler.HandleBaseDecl runs over
    /// the class's members. Use this instead of <see cref="EmitConstructor"/> when the test
    /// needs to exercise primary/projected dedup loops, collision suffixing, or any other
    /// behavior that lives in the iteration loop rather than in the per-member handler.
    /// </summary>
    private static string EmitClass(ClassDecl classDecl, TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(new StringWriter());

        var handler = new ClassHandler(new NullLogger<ClassHandler>());
        var env = handler.Marshal(classDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return csOutput.ToString();
    }
}
