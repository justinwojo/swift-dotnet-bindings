// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for the Finding 56(d) string by-value fast path: a transient <c>String</c> argument to an
/// <c>@_cdecl</c> constructor/method wrapper is built directly into a 16-byte STACK buffer
/// (<see cref="Swift.SwiftString.EphemeralSwiftString"/>) instead of the heap
/// <c>SwiftString</c> + <c>SafeHandle</c> + <c>PayloadBuffer</c> the general parameter path allocates.
/// The two extracted nint words (<c>{p}_w0</c>/<c>{p}_w1</c>) and their +0/+1 lifetimes are
/// byte-identical to the old heap path — this asserts the fast path is emitted for the cdecl-decompose
/// case and that the heap path's markers are no longer present, with no change to the P/Invoke shape.
/// </summary>
public class StringByValueFastPathEmitterTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void StringDecomposition_UsesArgumentDirection(bool inout, bool typeSpecInout)
    {
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replace", CreateClassDecl("Loader", module), module);
        method.UsesCdeclMethodWrapper = true;
        var argument = CreateArg("text", new NamedTypeSpec("Swift.String") { IsInOut = typeSpecInout }, module);
        argument.IsInOut = inout;
        Assert.Equal(!inout, MarshallingHelpers.ShouldDecomposeStringForCdecl(method, argument));
        method.UsesCdeclMethodWrapper = false;
        method.UsesCdeclConstructorWrapper = true;
        Assert.Equal(!inout, MarshallingHelpers.ShouldDecomposeStringForCdecl(method, argument));
        method.UsesCdeclConstructorWrapper = false;
        Assert.False(MarshallingHelpers.ShouldDecomposeStringForCdecl(method, argument));
    }

    [Fact]
    public void StringInout_PrimaryCapabilityDoesNotGrantSecondaryOrUnknownStorageSupport()
    {
        var db = CreateTypeDatabase();
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replace", CreateClassDecl("Loader", module), module);
        var argument = CreateArg("text", new NamedTypeSpec("Swift.String"), module);
        argument.IsInOut = true;
        method.CSSignature.Add(argument);
        var env = new MethodEnvironment(method, db);

        Assert.True(MethodWrapperEmitter.ShouldEmitWrapper(env));
        Assert.True(WrapperValidation.WillPromoteToCdeclMethodWrapper(env));
        Assert.True(WrapperValidation.HasInoutWithAbiMismatch(env));
        Assert.False(WrapperValidation.HasInoutWithAbiMismatch(env, supportsInoutString: true));
        Assert.False(MethodWrapperEmitter.HasCdeclCompatibleFunctionShape(env));

        foreach (string excluded in new[] { "TestModule.Loader", "Unknown.Value" })
        {
            argument.SwiftTypeSpec = new NamedTypeSpec(excluded);
            Assert.True(WrapperValidation.HasInoutWithAbiMismatch(env, supportsInoutString: true));
            Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
        }
        argument.SwiftTypeSpec = new NamedTypeSpec("Swift.String");
        Assert.True(db.TryGetTypeRecord(argument.SwiftTypeSpec, out var record));
        record.Flags |= TypeRecordFlags.NonCopyable;
        Assert.True(WrapperValidation.HasInoutWithAbiMismatch(env, supportsInoutString: true));
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
    }

    [Theory]
    [InlineData("async")]
    [InlineData("generic-parent")]
    [InlineData("generic-method")]
    [InlineData("closure")]
    [InlineData("closure-return")]
    [InlineData("constructor")]
    [InlineData("noncopyable-value")]
    public void StringInout_DoesNotAdmitUnqualifiedProducerCompositions(string shape)
    {
        var db = CreateTypeDatabase();
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        var module = CreateModuleDecl("TestModule");
        var parent = CreateClassDecl("Loader", module);
        var method = CreateMethod("replace", parent, module);
        var text = CreateArg("text", new NamedTypeSpec("Swift.String"), module);
        text.IsInOut = true;
        method.CSSignature.Add(text);
        var generic = new GenericArgumentDecl("τ_0_0", "T", new List<GenericParameterConformance>(), new List<GenericParameterConformance>());
        switch (shape)
        {
            case "async": method.IsAsync = true; break;
            case "generic-parent": parent.GenericParameters.Add(generic); break;
            case "generic-method": method.GenericParameters.Add(generic); break;
            case "constructor": method.IsConstructor = true; break;
            case "closure":
                method.CSSignature.Add(CreateArg("callback", new ClosureTypeSpec { Arguments = TupleTypeSpec.Empty, ReturnType = TupleTypeSpec.Empty }, module));
                break;
            case "closure-return":
                method.CSSignature[0].SwiftTypeSpec = new ClosureTypeSpec { Arguments = TupleTypeSpec.Empty, ReturnType = TupleTypeSpec.Empty };
                break;
            case "noncopyable-value":
                Assert.True(db.TryGetTypeRecord(new NamedTypeSpec("TestModule.Tag"), out var record));
                record.Flags |= TypeRecordFlags.NonCopyable;
                method.CSSignature.Add(CreateArg("value", new NamedTypeSpec("TestModule.Tag"), module));
                break;
        }
        var env = new MethodEnvironment(method, db);
        Assert.False(MethodWrapperEmitter.ShouldEmitWrapper(env));
        Assert.False(WrapperValidation.WillPromoteToCdeclMethodWrapper(env));
    }

    [Theory]
    [InlineData(false, ParameterOwnership.Owned)]
    [InlineData(true, ParameterOwnership.Owned)]
    [InlineData(false, ParameterOwnership.Shared)]
    public void QualifiedStringInout_EmitsMatchingNativeAndManagedStorageWithOwnedValueSibling(bool throws, ParameterOwnership ownership)
    {
        var db = CreateTypeDatabase();
        db.AsyncLibraryName = "TestModuleSwiftBindings";
        Assert.True(db.TryGetTypeRecord(new NamedTypeSpec("TestModule.Tag"), out var valueRecord));
        valueRecord.Flags = TypeRecordFlags.RequiresMemoryManagement; // nonfrozen, nontrivial
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replace", CreateClassDecl("Loader", module), module);
        method.Throws = throws;
        method.CSSignature[0].SwiftTypeSpec = new NamedTypeSpec("Swift.Int");
        var value = CreateArg("value", new NamedTypeSpec("TestModule.Tag"), module);
        value.Ownership = ownership;
        method.CSSignature.Add(value);
        method.CSSignature.Add(CreateArg("before", new NamedTypeSpec("Swift.String"), module));
        var text = CreateArg("text", new NamedTypeSpec("Swift.String"), module);
        text.IsInOut = true;
        method.CSSignature.Add(text);
        method.CSSignature.Add(CreateArg("after", new NamedTypeSpec("Swift.String"), module));

        var (managed, native) = EmitMethod(method, db); // Natural MethodHandler eligibility, no flag injection.

        Assert.Contains("CallConvCdecl", managed);
        Assert.Contains("LibraryImport(\"TestModuleSwiftBindings\"", managed);
        Assert.Contains("ref string text", managed);
        Assert.Contains("ref Swift.SwiftString.Buffer text", managed);
        Assert.Contains("ref textDisposable.BufferRef", managed);
        Assert.Contains("new SwiftString(text)", managed);
        Assert.Contains("text = textSwift.ToString();", managed);
        Assert.DoesNotContain("text_w0", managed);
        Assert.DoesNotContain("text_w1", managed);
        Assert.DoesNotContain("BeginValueTransfer", managed);
        Assert.DoesNotContain("SwiftSelf", managed);
        Assert.DoesNotContain("ref SwiftError", managed);
        Assert.Contains("new SwiftString.EphemeralSwiftString(before)", managed);
        Assert.Contains("new SwiftString.EphemeralSwiftString(after)", managed);
        Assert.Contains("nint before_w0", managed);
        Assert.Contains("nint after_w1", managed);
        Assert.Contains("_ text: UnsafeMutableRawPointer", native);
        Assert.Contains("var textVal = text.assumingMemoryBound(to: Swift.String.self).pointee", native);
        Assert.Contains("text.assumingMemoryBound(to: Swift.String.self).pointee = textVal", native);
        Assert.Contains("defer {", native);
        Assert.Contains("text: &textVal", native);
        Assert.Contains("value.assumingMemoryBound(to: TestModule.Tag.self).pointee", native);
        if (throws)
        {
            Assert.Contains("out IntPtr errorPtr", managed);
            Assert.Contains("Unmanaged.passRetained(error as AnyObject).toOpaque()", native);
            int copyback = managed.IndexOf("text = textSwift.ToString();", System.StringComparison.Ordinal);
            Assert.True(copyback > managed.IndexOf("SwiftMarshal.ThrowSwiftError", System.StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonXCFrameworkStringInout_StillEmitsRealDirectValueTransfer(bool staticStruct)
    {
        var db = CreateTypeDatabase(); // No wrapper library, real direct-generation context.
        Assert.True(db.TryGetTypeRecord(new NamedTypeSpec("TestModule.Tag"), out var valueRecord));
        valueRecord.Flags = TypeRecordFlags.RequiresMemoryManagement;
        var module = CreateModuleDecl("TestModule");
        TypeDecl parent = staticStruct ? CreateFrozenStructDecl("Tag", module) : CreateClassDecl("Loader", module);
        if (parent is StructDecl structParent)
            structParent.IsFrozen = false;
        var method = CreateMethod("replace", parent, module);
        if (staticStruct)
            method.MethodType = MethodType.Static;
        var value = CreateArg("value", new NamedTypeSpec("TestModule.Tag"), module);
        value.Ownership = ParameterOwnership.Owned;
        method.CSSignature.Add(value);
        var text = CreateArg("text", new NamedTypeSpec("Swift.String"), module);
        text.IsInOut = true;
        method.CSSignature.Add(text);
        var (managed, native) = EmitMethod(method, db);
        Assert.Contains("CallConvSwift", managed);
        // A nonfinal class instance method resolves to its vtable dispatch export.
        // A static struct method retains the original symbol and has no receiver slot.
        string expectedEntry = method.MangledName + (staticStruct ? "" : "Tj");
        Assert.True(managed.Contains("EntryPoint = \"" + expectedEntry + "\"", System.StringComparison.Ordinal), managed);
        Assert.DoesNotContain("throw new NotSupportedException", managed);
        Assert.Contains("OwnedArgument.BeginValueTransfer<TestModule.Tag>(value.Payload)", managed);
        Assert.Contains("valueOwnedTransfer.Complete();", managed);
        Assert.Contains("ref textDisposable.BufferRef", managed);
        Assert.Contains("text = textSwift.ToString();", managed);
        Assert.DoesNotContain("@_cdecl", native);
    }

    [Fact]
    public void CdeclMethodWrapper_StringParam_UsesEphemeralStackBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethod("greet", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        method.UsesCdeclMethodWrapper = true;

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        // Fast path: build the transient String into a stack buffer, extract two words.
        Assert.Contains("new SwiftString.EphemeralSwiftString(name)", csOutput);
        Assert.Contains("using var nameSwift =", csOutput);
        Assert.Contains("Unsafe.As<SwiftString.Buffer, nint>(ref nameBuf)", csOutput);
        // P/Invoke argument names are unchanged (two nint words).
        Assert.Contains("nint name_w0 =", csOutput);
        Assert.Contains("nint name_w1 =", csOutput);

        // R3-F1: the owning ref-struct handle (nameSwift) must be read ONLY through its inert
        // `.Buffer` words (copied into nameBuf) — never aliased/copied by value, which would let two
        // instances each Dispose the same +1 (over-release on a heap-backed string).
        Assert.Contains("var nameBuf = nameSwift.Buffer;", csOutput);
        Assert.DoesNotContain("= nameSwift;", csOutput); // no value-copy of the owning handle

        // The heap SwiftString path (SafeHandle + PayloadBuffer) is intentionally skipped for the
        // decomposed param — its markers must be gone (behavior-preserving, allocation-free).
        Assert.DoesNotContain("nameDisposable", csOutput);
        Assert.DoesNotContain("PayloadBuffer", csOutput);
        Assert.DoesNotContain("new SwiftString(name)", csOutput);
    }

    [Fact]
    public void CdeclConstructorWrapper_StringParam_UsesEphemeralStackBuffer()
    {
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateFrozenStructDecl("Tag", moduleDecl);

        var ctor = CreateConstructor(parentDecl, moduleDecl);
        ctor.CSSignature.Add(CreateArg("label", new NamedTypeSpec("Swift.String"), moduleDecl));
        ctor.UsesCdeclConstructorWrapper = true;

        var (csOutput, _) = EmitConstructor(ctor, typeDatabase);

        Assert.Contains("new SwiftString.EphemeralSwiftString(label)", csOutput);
        Assert.Contains("using var labelSwift =", csOutput);
        Assert.Contains("Unsafe.As<SwiftString.Buffer, nint>(ref labelBuf)", csOutput);
        Assert.Contains("nint label_w0 =", csOutput);
        Assert.Contains("nint label_w1 =", csOutput);

        Assert.DoesNotContain("labelDisposable", csOutput);
        Assert.DoesNotContain("PayloadBuffer", csOutput);
        Assert.DoesNotContain("new SwiftString(label)", csOutput);
    }

    [Fact]
    public void NonCdeclMethod_StringParam_KeepsHeapPath_NoEphemeral()
    {
        // Behavior-preservation guard: the fast path is scoped to the @_cdecl decompose case.
        // A CallConvSwift (non-cdecl) method's String param must still marshal through the heap
        // SwiftString projection — EphemeralSwiftString must NOT appear.
        var typeDatabase = CreateTypeDatabase();
        var moduleDecl = CreateModuleDecl("TestModule");
        var parentDecl = CreateClassDecl("Loader", moduleDecl);

        var method = CreateMethod("greet", parentDecl, moduleDecl);
        method.CSSignature.Add(CreateArg("name", new NamedTypeSpec("Swift.String"), moduleDecl));
        // UsesCdeclMethodWrapper deliberately NOT set.

        var (csOutput, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("EphemeralSwiftString", csOutput);
        Assert.Contains("new SwiftString(name)", csOutput);
        Assert.DoesNotContain("name = nameSwift.ToString();", csOutput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectStringInout_WritesBackInFinallyAfterErrorAndReturnConversion(bool throws)
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var parent = CreateClassDecl("Loader", module);
        var method = CreateMethod("replace", parent, module);
        method.Throws = throws;
        method.CSSignature[0].SwiftTypeSpec = new NamedTypeSpec("TestModule.Loader");
        var argument = CreateArg("name", new NamedTypeSpec("Swift.String"), module);
        argument.IsInOut = true;
        method.CSSignature.Add(argument);

        var (output, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("ref string name", output);
        Assert.Contains("ref nameDisposable.BufferRef", output);
        var writeback = output.IndexOf("name = nameSwift.ToString();", System.StringComparison.Ordinal);
        Assert.True(writeback >= 0, output);
        var finallyIndex = output.LastIndexOf("finally", writeback, System.StringComparison.Ordinal);
        var call = output.IndexOf("ref nameDisposable.BufferRef", System.StringComparison.Ordinal);
        Assert.True(finallyIndex > call, output);
        // The managed return conversion is evaluated before finally. A failed String
        // conversion therefore cannot strand the newly returned native class reference.
        Assert.Contains("return ", output.Substring(call, finallyIndex - call));
        if (throws)
            Assert.Contains("SwiftMarshal.ThrowSwiftError", output.Substring(call, finallyIndex - call));
        Assert.Contains("= true;", output.Substring(call, finallyIndex - call));
        Assert.Contains("if (", output.Substring(finallyIndex, writeback - finallyIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectStringInout_ConstructorAndFailableFactoryWriteBack(bool failable)
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var parent = CreateFrozenStructDecl("Tag", module);
        var constructor = CreateConstructor(parent, module);
        constructor.IsFailable = failable;
        var argument = CreateArg("label", new NamedTypeSpec("Swift.String"), module);
        argument.IsInOut = true;
        constructor.CSSignature.Add(argument);

        var (output, _) = EmitConstructor(constructor, typeDatabase);

        Assert.Contains("ref string label", output);
        var writeback = output.IndexOf("label = labelSwift.ToString();", System.StringComparison.Ordinal);
        Assert.True(writeback >= 0, output);
        var finallyIndex = output.LastIndexOf("finally", writeback, System.StringComparison.Ordinal);
        Assert.True(finallyIndex > output.IndexOf("ref labelDisposable.BufferRef", System.StringComparison.Ordinal), output);
    }

    [Fact]
    public void DirectStringInout_MultipleArgumentsWriteBackInDeclarationOrder()
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replaceBoth", CreateClassDecl("Loader", module), module);
        foreach (var name in new[] { "first", "second" })
        {
            var argument = CreateArg(name, new NamedTypeSpec("Swift.String"), module);
            argument.IsInOut = true;
            method.CSSignature.Add(argument);
        }

        var (output, _) = EmitMethod(method, typeDatabase);

        var first = output.IndexOf("first = firstSwift.ToString();", System.StringComparison.Ordinal);
        var second = output.IndexOf("second = secondSwift.ToString();", System.StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first, output);
    }

    [Fact]
    public void DirectStringInout_CallCompletionLocalAvoidsParameterCollision()
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replace", CreateClassDecl("Loader", module), module);
        var argument = CreateArg("__stringInoutCallCompleted", new NamedTypeSpec("Swift.String"), module);
        argument.IsInOut = true;
        method.CSSignature.Add(argument);

        var (output, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("bool __stringInoutCallCompleted =", output);
        Assert.Contains("__stringInoutCallCompleted = __stringInoutCallCompletedSwift.ToString();", output);
    }

    [Fact]
    public void DirectStringInout_ObjCRootedHelperReleasesPendingHandleOnConversionFailure()
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var parent = CreateClassDecl("Loader", module);
        parent.IsObjCRooted = true;
        var constructor = CreateConstructor(parent, module);
        constructor.Throws = true;
        var argument = CreateArg("label", new NamedTypeSpec("Swift.String"), module);
        argument.IsInOut = true;
        constructor.CSSignature.Add(argument);

        var (output, _) = EmitConstructor(constructor, typeDatabase);

        var pending = output.IndexOf("__stringInoutUnadoptedObjCResult = result;", System.StringComparison.Ordinal);
        var error = output.IndexOf("SwiftMarshal.ThrowSwiftError", System.StringComparison.Ordinal);
        Assert.True(error >= 0 && pending > error, output);
        var writeback = output.IndexOf("label = labelSwift.ToString();", System.StringComparison.Ordinal);
        var release = output.IndexOf("Arc.UnknownObjectRelease(__stringInoutUnadoptedObjCResult)", System.StringComparison.Ordinal);
        Assert.True(release > writeback && writeback > pending, output);
        Assert.Contains("catch", output.Substring(writeback, release - writeback));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CdeclOptionalStringInout_WritesBackSomeAndNoneAfterErrorConversion(bool throws)
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("replaceOptional", CreateClassDecl("Loader", module), module);
        method.UsesCdeclMethodWrapper = true;
        method.Throws = throws;
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        var argument = CreateArg("name", optional, module);
        argument.IsInOut = true;
        method.CSSignature.Add(argument);

        var (output, _) = EmitMethod(method, typeDatabase);

        Assert.Contains("ref string? name", output);
        Assert.Contains("if (nameSwift.HasValue)", output);
        Assert.Contains("using var __nameInoutValue = nameSwift.Some;", output);
        Assert.Contains("name = __nameInoutValue.ToString();", output);
        Assert.Contains("name = null;", output);
        var writeback = output.IndexOf("if (nameSwift.HasValue)", System.StringComparison.Ordinal);
        var finallyIndex = output.LastIndexOf("finally", writeback, System.StringComparison.Ordinal);
        Assert.True(finallyIndex >= 0, output);
        Assert.Contains("if (__stringInoutCallCompleted)", output.Substring(finallyIndex, writeback - finallyIndex));
        if (throws)
            Assert.True(output.IndexOf("SwiftMarshal.ThrowSwiftError", System.StringComparison.Ordinal) < finallyIndex, output);
    }

    [Fact]
    public void CdeclOptionalStringByValue_DoesNotWriteBack()
    {
        var typeDatabase = CreateTypeDatabase();
        var module = CreateModuleDecl("TestModule");
        var method = CreateMethod("readOptional", CreateClassDecl("Loader", module), module);
        method.UsesCdeclMethodWrapper = true;
        var optional = new NamedTypeSpec("Swift.Optional");
        optional.GenericParameters.Add(new NamedTypeSpec("Swift.String"));
        method.CSSignature.Add(CreateArg("name", optional, module));

        var (output, _) = EmitMethod(method, typeDatabase);

        Assert.DoesNotContain("InoutValue", output);
        Assert.DoesNotContain("__stringInoutCallCompleted", output);
    }

    [Fact]
    public void EphemeralSwiftString_ConstructedAtExactlyOneUsingBoundEmitterSite()
    {
        // R3-F1 structural guard (the durable gate for the over-release-on-copy hazard).
        // EphemeralSwiftString is a ref struct that OWNS a +1 Swift String and releases it once on
        // Dispose. C# cannot enforce move-only semantics, so a value-copy of the owning handle
        // (`var b = a;`) would carry `_created = true` into the copy and let two instances each call
        // SBW_SwiftString_Destroy on the same String — an over-release for heap-backed (large-form)
        // strings. The only robust defense is to guarantee the GENERATOR never copies the handle: it
        // must construct it at exactly one emitter site and always bind it with `using` (a single,
        // deterministic Dispose). This scans the generator source and pins that invariant, so a
        // future emitter edit that adds a second construction site or drops the `using` turns red.
        var srcRoot = Path.Combine(LocateRepoRoot(), "src", "Swift.Bindings", "src");
        Assert.True(Directory.Exists(srcRoot), $"Generator source root not found: {srcRoot}");

        var sites = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("new SwiftString.EphemeralSwiftString("))
                    continue;
                // Every emitted construction MUST be a `using var` declaration so the owning handle
                // is disposed exactly once and is never bound to a non-`using` (leaked) or copyable
                // local. The emitter builds the emitted code via an interpolated string, so the
                // `using var` token appears on the SAME source line as the construction.
                Assert.True(lines[i].Contains("using var "),
                    $"EphemeralSwiftString construction at {Path.GetFileName(file)}:{i + 1} is not `using`-bound: {lines[i].Trim()}");
                sites.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }

        Assert.True(sites.Count == 1,
            $"Expected exactly ONE EphemeralSwiftString construction site (the @_cdecl string by-value fast path in " +
            $"WrapperEmitter.Marshalling.cs); found {sites.Count}: [{string.Join(", ", sites)}]. A ref struct that owns " +
            $"a +1 must never be copied — every emission must be a single `using`-bound site reading only `.Buffer`.");
    }

    #region Helpers

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SwiftBindings.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (string csOutput, string swiftOutput) EmitMethod(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new MethodHandler(new NullLogger<MethodHandler>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static (string csOutput, string swiftOutput) EmitConstructor(
        MethodDecl methodDecl,
        TypeDatabase typeDatabase)
    {
        var csOutput = new StringWriter();
        var swiftOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftWriter = new SwiftWriter(swiftOutput);

        var handler = new ConstructorHandler(new NullLogger<ConstructorHandler>(), new HashSet<string>());
        var env = new MethodEnvironment(methodDecl, typeDatabase);
        var conductor = new Conductor(new NullLoggerFactory());
        handler.Emit(csWriter, swiftWriter, env, conductor, TypeHandlerContext.Empty);

        return (csOutput.ToString(), swiftOutput.ToString());
    }

    private static MethodDecl CreateMethod(string name, TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var method = new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule6LoaderC{name.Length}{name}yySSF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, TupleTypeSpec.Empty, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        if (parentDecl is ClassDecl classDecl)
            classDecl.Methods.Add(method);
        else if (parentDecl is StructDecl structDecl)
            structDecl.Methods.Add(method);
        return method;
    }

    private static MethodDecl CreateConstructor(TypeDecl parentDecl, ModuleDecl moduleDecl)
    {
        var ctor = new MethodDecl
        {
            Name = "init",
            MangledName = $"$s10TestModule{parentDecl.Name.Length}{parentDecl.Name}V5labelACSS_tcfc",
            MethodType = MethodType.Static,
            IsConstructor = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg(string.Empty, new NamedTypeSpec($"{moduleDecl.Name}.{parentDecl.Name}"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = parentDecl,
            ModuleDecl = moduleDecl,
            Throws = false,
            IsAsync = false,
            IsSynthesizedAccessor = false
        };
        parentDecl.Methods.Add(ctor);
        return ctor;
    }

    private static ArgumentDecl CreateArg(string name, TypeSpec typeSpec, ModuleDecl moduleDecl)
    {
        return new ArgumentDecl
        {
            Name = name,
            PrivateName = name,
            SwiftTypeSpec = typeSpec,
            IsInOut = false,
            IsGeneric = false,
            ParentDecl = null,
            ModuleDecl = moduleDecl
        };
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

    private static ClassDecl CreateClassDecl(string name, ModuleDecl moduleDecl)
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
        return classDecl;
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
            MetadataAccessor = $"$s10TestModule{name.Length}{name}VMa"
        };
        moduleDecl.Types.Add(structDecl);
        return structDecl;
    }

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
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.String"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Struct,
                InlineSize = 16
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
            SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Loader"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Loader"),
                MetadataAccessor = "$s10TestModule6LoaderCMa",
                Flags = TypeRecordFlags.RequiresMemoryManagement,
                Kind = TypeRecordKind.Class
            });
        module.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.Tag"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "Tag"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Tag"),
                MetadataAccessor = "$s10TestModule3TagVMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(module);

        return typeDatabase;
    }

    #endregion
}
