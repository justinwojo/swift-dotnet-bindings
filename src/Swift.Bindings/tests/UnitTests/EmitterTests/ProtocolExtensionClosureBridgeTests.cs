// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Tests for ProtocolExtensionClosureBridge — closure bridging in protocol extensions.
/// Tests TryEmit gate logic and output patterns for various closure shapes.
/// </summary>
public class ProtocolExtensionClosureBridgeTests
{
    #region TryEmit gate: non-protocol-extension returns false

    [Fact]
    public void TryEmit_NonProtocolExtension_ReturnsFalse()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateMethodDecl("doWork", isProtocolExtension: false);
        var env = CreateMethodEnvironment(method);

        var result = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
        Assert.Empty(output.ToString());
    }

    #endregion

    #region TryEmit gate: no closure parameter returns false

    [Fact]
    public void TryEmit_ThrowingMethod_ReturnsFalse()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "subscribe",
            new ClosureTypeSpec(TupleTypeSpec.Empty, TupleTypeSpec.Empty));
        method.Throws = true;
        var env = CreateMethodEnvironment(method);

        var result = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, CreateClassDecl("MyProtocol"));

        // PExtCB has no Swift error out-param / HandleSwiftError handling, so throwing
        // protocol-extension closure methods must fail closed (matching MCB/NCB).
        Assert.False(result);
        Assert.Empty(output.ToString());
    }

    [Fact]
    public void TryEmit_NoClosure_ReturnsFalse()
    {
        var (csWriter, swiftWriter, output) = CreateWriters();
        var method = CreateMethodDecl("doWork", isProtocolExtension: true);
        // CSSignature has return + non-closure arg only
        var env = CreateMethodEnvironment(method);

        var result = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, null);

        Assert.False(result);
    }

    #endregion

    #region TryEmit: void closure emits bridge

    [Fact]
    public void TryEmit_VoidClosure_EmitsCallback()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "forEach",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Should emit [UnmanagedCallersOnly] callback
        Assert.Contains("[UnmanagedCallersOnly", result);
        Assert.Contains("CallConvCdecl", result);
        Assert.Contains("PExtCB_", result);

        // Should emit function pointer field
        Assert.Contains("s_PExtCB_", result);
        Assert.Contains("delegate* unmanaged[Cdecl]", result);

        // Should emit P/Invoke
        Assert.Contains("LibraryImport", result);

        // Should emit public method with Action<> parameter
        Assert.Contains("public unsafe void ForEach", result);
        Assert.Contains("Action<", result);

        // Should set WasEmitted
        Assert.True(method.WasEmitted);
    }

    #endregion

    #region TryEmit: bool-returning closure

    [Fact]
    public void TryEmit_BoolReturnClosure_EmitsByteReturn()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "filter",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Bool return closure callback should return byte
        Assert.Contains("byte", result);
        Assert.Contains("Func<", result);
        Assert.Contains("bool", result);
    }

    #endregion

    #region TryEmit: zero-arg closure

    [Fact]
    public void TryEmit_ZeroArgVoidClosure_EmitsAction()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "onComplete",
            new ClosureTypeSpec(null, TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        var handled = ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        Assert.True(handled);
        var result = csOutput.ToString();

        // Zero-arg void closure → Action (not Action<>)
        Assert.Contains("Action __inner = () =>", result);
    }

    #endregion

    #region Output patterns: GCHandle allocation

    [Fact]
    public void TryEmit_EmitsGCHandleAllocation()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "process",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        Assert.Contains("GCHandle.Alloc", result);
        Assert.Contains("GCHandle.ToIntPtr", result);
        Assert.Contains("Payload.DangerousGetHandle()", result);
    }

    #endregion

    #region Output patterns: self_ parameter in P/Invoke

    [Fact]
    public void TryEmit_PInvokeHasSelfFirst()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "run",
            new ClosureTypeSpec(null, TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        // Protocol extension P/Invoke ABI: self_ comes first
        Assert.Contains("IntPtr self_", result);
    }

    [Fact]
    public void TryEmit_PInvoke_UserSelfParam_DoesNotDuplicateSyntheticSelf()
    {
        // P1-22 (C1): the protocol-extension P/Invoke ABI prepends a synthetic `self_` pointer.
        // A user non-closure parameter also spelled `self_` would emit `IntPtr self_, …, IntPtr self_`
        // — a CS0100 duplicate-parameter-name error the generator produced at exit 0 (broken C# that
        // only fails at compile). The synthetic-name guard reserves `self_` against the user param
        // names and renames the synthetic self pointer to `__self_`; the call site is positional so
        // no call-site change is needed.
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var moduleDecl = CreateModuleDecl();
        var closureSpec = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = new MethodDecl
        {
            Name = "observe",
            MangledName = "$s10TestModule10MyProtocol7observeyyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsProtocolExtensionMethod = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("self", new NamedTypeSpec("TestModule.MyProtocol"), moduleDecl),
                // User non-closure param collides with the synthetic protocol-extension self pointer.
                CreateArg("self_", new NamedTypeSpec("Swift.Int"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = CreateClassDecl("MyProtocol"),
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
        var env = CreateMethodEnvironment(method);

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, CreateClassDecl("MyProtocol"));

        var result = csOutput.ToString();
        // The synthetic self pointer was renamed off the colliding user param — proof the guard
        // fired (absent the guard the synthetic stays `self_` and there is no `__self_`).
        Assert.Contains("IntPtr __self_", result);
        // The user param survives under its own name...
        Assert.Contains("IntPtr self_", result);
        // ...and there is exactly one parameter literally named `self_` (the renamed synthetic is
        // `__self_`, which `IntPtr self_` — note the leading space — does not match).
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"IntPtr self_\b"));
    }

    #endregion

    #region Throw-window: escaping closure pre-declares GCHandle and __transferred

    [Fact]
    public void TryEmit_EscapingClosure_PreDeclaresGCHandleAndTransferredFlag()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var closureSpec = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateProtocolExtMethodWithClosure("observe", closureSpec);
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        // Pre-declared GCHandle so finally can free it after a throw between alloc and the
        // P/Invoke returning successfully.
        Assert.Contains("GCHandle __gcHandle = default;", result);
        // Transferred flag is set only after a successful P/Invoke return so the finally
        // skips Free() on the happy path (Swift owns the handle via the _SBClosureCtx box).
        Assert.Contains("bool __transferred = false;", result);
    }

    [Fact]
    public void TryEmit_EscapingClosure_WrapsCallInTryFinally()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var closureSpec = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        closureSpec.Attributes.Add(new TypeSpecAttribute("escaping"));
        var method = CreateProtocolExtMethodWithClosure("observe", closureSpec);
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        // Try/finally wraps the P/Invoke section. The finally only frees when ownership
        // transfer never moved into Swift (e.g. throw before/during the P/Invoke).
        Assert.Contains("try", result);
        Assert.Contains("finally", result);
        Assert.Contains("__transferred = true;", result);
        Assert.Contains("if (!__transferred && __gcHandle.IsAllocated) __gcHandle.Free();", result);
    }

    [Fact]
    public void TryEmit_NonEscapingClosure_FreesGCHandleUnconditionallyInFinally()
    {
        // Non-escaping protocol extension closure: the trampoline fires synchronously inside the
        // call, so Swift never assumes ownership of the GCHandle — it must be freed on EVERY path.
        // PExtCB previously gated the try/finally on `IsEscaping`, so the non-escaping branch
        // emitted no finally and leaked the handle (and everything the delegate captured) for the
        // process lifetime. The fix wraps every path in try/finally; the non-escaping branch frees
        // unconditionally (no `__transferred` flag, which is the escaping ownership-transfer gate).
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var closureSpec = new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty);
        var method = CreateProtocolExtMethodWithClosure("forEach", closureSpec);
        var env = CreateMethodEnvironment(method);
        var parentDecl = CreateClassDecl("MyProtocol");

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, parentDecl);

        var result = csOutput.ToString();
        // No ownership-transfer flag — that gate is escaping-only.
        Assert.DoesNotContain("__transferred", result);
        // The handle is freed in a finally regardless of how the call exits...
        Assert.Contains("finally", result);
        // ...and unconditionally (not behind the `!__transferred` escaping gate).
        Assert.Contains("if (__gcHandle.IsAllocated) __gcHandle.Free();", result);
        // GCHandle is still pre-declared at method scope (consistent with MCB / NCB layout).
        Assert.Contains("GCHandle __gcHandle = default;", result);
    }

    #endregion

    #region P0-01: non-throwing closure callbacks fail fast, never swallow

    // A non-throwing Swift closure has no error channel, so a managed exception escaping the
    // delegate must not unwind into native Swift (SIGABRT) and must not be silently swallowed
    // (handing Swift a fabricated result — for the generic-return path an unwritten result
    // buffer Swift then .move()s as uninitialized storage). The contract is a controlled
    // FailFast. All three PExtCB callback shapes route through the shared
    // ClosureEmitter.EmitNonThrowingFailFastCatch helper; the void and bool shapes are
    // constructible here, the bound-generic-return shape emits the identical catch via the
    // same helper.

    [Fact]
    public void TryEmit_VoidClosure_CallbackFailsFastOnManagedException()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "forEach",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), TupleTypeSpec.Empty));
        var env = CreateMethodEnvironment(method);

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, CreateClassDecl("MyProtocol"));

        var result = csOutput.ToString();
        // The UCO callback must catch a managed exception and route it to the fail-fast contract,
        // not swallow it with a bare `catch { }`.
        Assert.Contains("catch (global::System.Exception", result);
        Assert.Contains("FailFastUnhandledClosureException", result);
        Assert.DoesNotContain("catch { }", result);
    }

    [Fact]
    public void TryEmit_BoolReturnClosure_CallbackFailsFastOnManagedException()
    {
        var (csWriter, swiftWriter, csOutput) = CreateWriters();
        var method = CreateProtocolExtMethodWithClosure(
            "filter",
            new ClosureTypeSpec(new NamedTypeSpec("Swift.Int"), new NamedTypeSpec("Swift.Bool")));
        var env = CreateMethodEnvironment(method);

        ProtocolExtensionClosureBridge.TryEmit(csWriter, swiftWriter, env, CreateClassDecl("MyProtocol"));

        var result = csOutput.ToString();
        // The bool-return callback must NOT fabricate `return 0;` on a managed fault — that hands
        // Swift a bogus `false`. It must fail fast.
        Assert.Contains("catch (global::System.Exception", result);
        Assert.Contains("FailFastUnhandledClosureException", result);
        Assert.DoesNotContain("catch { return 0; }", result);
    }

    #endregion

    #region Helpers

    private static (CSharpWriter csWriter, SwiftWriter swiftWriter, StringWriter csOutput) CreateWriters()
    {
        var csOutput = new StringWriter();
        var csWriter = new CSharpWriter(csOutput);
        var swiftOutput = new StringWriter();
        var swiftWriter = new SwiftWriter(swiftOutput);
        return (csWriter, swiftWriter, csOutput);
    }

    private static ModuleDecl CreateModuleDecl()
    {
        return new ModuleDecl
        {
            Name = "TestModule",
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Types = new List<TypeDecl>(),
            Dependencies = new List<string>(),
            Protocols = new List<ProtocolDecl>(),
            ParentDecl = null,
            ModuleDecl = null
        };
    }

    private static ClassDecl CreateClassDecl(string name)
    {
        var moduleDecl = CreateModuleDecl();
        return new ClassDecl
        {
            Name = name,
            SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"TestModule.{name}"),
            MangledName = $"$s10TestModule{name.Length}{name}CN",
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
    }

    private static MethodDecl CreateMethodDecl(string name, bool isProtocolExtension)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("TestType");
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsProtocolExtensionMethod = isProtocolExtension,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("value", new NamedTypeSpec("Swift.Int"), moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
    }

    private static MethodDecl CreateProtocolExtMethodWithClosure(string name, ClosureTypeSpec closureSpec)
    {
        var moduleDecl = CreateModuleDecl();
        var classDecl = CreateClassDecl("MyProtocol");
        return new MethodDecl
        {
            Name = name,
            MangledName = $"$s10TestModule10MyProtocol{name.Length}{name}yyF",
            MethodType = MethodType.Instance,
            IsConstructor = false,
            Throws = false,
            IsAsync = false,
            IsProtocolExtensionMethod = true,
            CSSignature = new List<ArgumentDecl>
            {
                CreateArg("", TupleTypeSpec.Empty, moduleDecl),
                CreateArg("self", new NamedTypeSpec("TestModule.MyProtocol"), moduleDecl),
                CreateArg("block", closureSpec, moduleDecl)
            },
            GenericParameters = new List<GenericArgumentDecl>(),
            ParentDecl = classDecl,
            ModuleDecl = moduleDecl,
            Visibility = Visibility.Public
        };
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

    private static TypeDatabase CreateTypeDatabase()
    {
        var typeDatabase = new TypeDatabase();
        var swiftModule = new ModuleTypeDatabase("Swift", "/usr/lib/swift/libswiftCore.dylib");
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "nint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Int"),
                MetadataAccessor = "$sSiMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        swiftModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("System", "Boolean"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.Bool"),
                MetadataAccessor = "$sSbMa",
                Flags = TypeRecordFlags.Frozen,
                Kind = TypeRecordKind.Struct
            });
        typeDatabase.AddModuleDatabase(swiftModule);

        var testModule = new ModuleTypeDatabase("TestModule", "/tmp/TestModule.dylib");
        testModule.RegisterType(
            SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("TestModule", "IMyProtocol"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.MyProtocol"),
                MetadataAccessor = "",
                Flags = TypeRecordFlags.None,
                Kind = TypeRecordKind.Protocol
            });
        typeDatabase.AddModuleDatabase(testModule);
        return typeDatabase;
    }

    private static MethodEnvironment CreateMethodEnvironment(MethodDecl method)
    {
        return new MethodEnvironment(method, CreateTypeDatabase());
    }

    #endregion
}
