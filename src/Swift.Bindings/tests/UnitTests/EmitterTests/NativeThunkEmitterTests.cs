// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Xunit;

namespace BindingsGeneration.Tests
{
    public class NativeThunkEmitterTests
    {
        #region ShouldEmitThunk — Eligible Cases

        [Fact]
        public void ShouldEmitThunk_SyncStaticMethod_ReturnsTrue()
        {
            var env = CreateMethodEnv(
                methodType: MethodType.Static,
                parentDecl: CreateClassDecl());

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_SyncInstanceMethod_ReturnsTrue()
        {
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                parentDecl: CreateClassDecl());

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_Constructor_ReturnsFalse()
        {
            // Constructors are deferred — C# codegen coupled with @_cdecl pattern
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_StructInstanceMethod_NonFrozen_ReturnsTrue()
        {
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                parentDecl: CreateStructDecl(isFrozen: false));

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_MainActorIsolated_ReturnsTrue()
        {
            // @MainActor does NOT block thunk emission (follows Xamarin precedent)
            var env = CreateMethodEnv(
                isActorIsolated: true,
                isMainActorIsolated: true,
                parentDecl: CreateClassDecl());

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        #endregion

        #region ShouldEmitThunk — Rejection Cases

        [Fact]
        public void ShouldEmitThunk_AsyncMethod_ReturnsFalse()
        {
            var env = CreateMethodEnv(isAsync: true, parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_GenericMethod_ReturnsFalse()
        {
            var env = CreateMethodEnv(isGeneric: true, parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_TypedThrows_ReturnsFalse()
        {
            var env = CreateMethodEnv(hasTypedThrows: true, parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ClosureParameter_ReturnsFalse()
        {
            var env = CreateMethodEnv(hasClosureParam: true, parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_VariadicParameter_ReturnsFalse()
        {
            var env = CreateMethodEnv(hasVariadic: true, parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_GenericTypeConstructor_ReturnsFalse()
        {
            var genericClass = CreateClassDecl();
            genericClass.GenericParameters = new List<GenericArgumentDecl>
            {
                new GenericArgumentDecl("\u03C4_0_0", "T", new(), new())
            };

            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: genericClass);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NonXCFrameworkMode_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                parentDecl: CreateClassDecl(),
                xcframeworkMode: false);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ModuleInternal_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                isModuleInternal: true,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_SpiProtected_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                isSpiProtected: true,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_CustomActorIsolated_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                isActorIsolated: true,
                isMainActorIsolated: false,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_InOutParameter_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                hasInOutParam: true,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ActorClass_ReturnsFalse()
        {
            var actorClass = CreateClassDecl();
            actorClass.IsActor = true;

            var env = CreateMethodEnv(parentDecl: actorClass);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_TupleReturnType_ReturnsFalse()
        {
            // Tuples can't be lowered by TypeLowering (only handles NamedTypeSpec)
            var env = CreateMethodEnv(parentDecl: CreateClassDecl());
            var method = env.MethodDecl;
            var tupleReturn = new TupleTypeSpec(new[] {
                new NamedTypeSpec("Swift.Int"),
                new NamedTypeSpec("Swift.String")
            });
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(tupleReturn, "")
            };

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ClosureReturnType_ReturnsFalse()
        {
            // Closure return types have complex ABI incompatible with thunks
            var env = CreateMethodEnv(parentDecl: CreateClassDecl());
            var method = env.MethodDecl;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new ClosureTypeSpec(), "")
            };

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        #endregion

        #region GetThunkSymbol

        [Fact]
        public void GetThunkSymbol_DelegatesToThunkAssemblyEmitter()
        {
            var method = CreateMethodDecl();
            method.MangledName = "$s4Test6simpleyyF";

            var symbol = NativeThunkEmitter.GetThunkSymbol(method, "Test");

            Assert.StartsWith("thunk_", symbol);
            Assert.Contains("Test", symbol);
        }

        #endregion

        #region EmitThunk — Basic Integration

        [Fact]
        public void EmitThunk_VoidStaticMethod_ProducesAssembly()
        {
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyClass", new TypeRecord
            {
                Kind = TypeRecordKind.Class,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyClass"),
                MetadataAccessor = "$s4Test7MyClassCMa",
                Flags = TypeRecordFlags.None
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(
                methodType: MethodType.Static,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyFZ";

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            var asm = asmBuilder.ToString();
            Assert.Contains(".globl", asm);
            Assert.Contains("_thunk_", asm);
        }

        [Fact]
        public void EmitThunk_Constructor_WithoutMetadataAccessor_ReturnsFalse()
        {
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            // No type record → no metadata accessor → should fail
            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClassCACycfc";

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.False(result);
            Assert.Empty(asmBuilder.ToString());
        }

        [Fact]
        public void EmitThunk_InstanceMethod_OnClass_ProducesAssembly()
        {
            var parentDecl = CreateClassDecl();
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            var method = CreateMethodDecl(
                methodType: MethodType.Instance,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyF";

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            Assert.Contains(".globl", asmBuilder.ToString());
        }

        #endregion

        #region Helper Methods

        private static readonly ModuleDecl TestModule = new ModuleDecl
        {
            Name = "Test",
            ParentDecl = null,
            ModuleDecl = null,
            Types = new List<TypeDecl>(),
            Protocols = new List<ProtocolDecl>(),
            Properties = new List<PropertyDecl>(),
            Methods = new List<MethodDecl>(),
            Dependencies = new List<string>()
        };

        private static MethodEnvironment CreateMethodEnv(
            MethodType methodType = MethodType.Static,
            bool isConstructor = false,
            bool isAsync = false,
            bool isGeneric = false,
            bool hasTypedThrows = false,
            bool hasClosureParam = false,
            bool hasVariadic = false,
            bool isModuleInternal = false,
            bool isSpiProtected = false,
            bool isActorIsolated = false,
            bool isMainActorIsolated = false,
            bool hasInOutParam = false,
            bool xcframeworkMode = true,
            BaseDecl? parentDecl = null)
        {
            parentDecl ??= CreateClassDecl();

            var method = CreateMethodDecl(
                methodType: methodType,
                isConstructor: isConstructor,
                parentDecl: parentDecl);

            method.IsAsync = isAsync;
            method.HasVariadicParameter = hasVariadic;
            method.IsModuleInternal = isModuleInternal;
            method.IsSpiProtected = isSpiProtected;
            method.IsActorIsolated = isActorIsolated;
            method.IsMainActorIsolated = isMainActorIsolated;

            if (hasTypedThrows)
                method.ThrownErrorType = new NamedTypeSpec("Swift.Error");

            if (isGeneric)
            {
                method.GenericParameters = new List<GenericArgumentDecl>
                {
                    new GenericArgumentDecl("\u03C4_0_0", "T", new(), new())
                };
            }

            // Build CSSignature: first element is return type (void)
            var signature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, "")
            };

            if (hasClosureParam)
            {
                signature.Add(MakeArg(new ClosureTypeSpec(), "callback"));
            }

            if (hasInOutParam)
            {
                signature.Add(MakeArg(new NamedTypeSpec("Swift.Int"), "value", isInOut: true));
            }

            method.CSSignature = signature;

            var db = new ThunkMockTypeDatabase(xcframeworkMode);
            return new MethodEnvironment(method, db);
        }

        private static ArgumentDecl MakeArg(TypeSpec typeSpec, string name, bool isInOut = false)
        {
            return new ArgumentDecl
            {
                Name = name,
                ParentDecl = null,
                ModuleDecl = null,
                SwiftTypeSpec = typeSpec,
                PrivateName = name,
                IsInOut = isInOut,
                IsGeneric = false
            };
        }

        private static MethodDecl CreateMethodDecl(
            MethodType methodType = MethodType.Static,
            bool isConstructor = false,
            BaseDecl? parentDecl = null)
        {
            parentDecl ??= CreateClassDecl();

            return new MethodDecl
            {
                Name = isConstructor ? "init" : "doWork",
                MangledName = "$s4Test7MyClass6doWorkyyF",
                MethodType = methodType,
                IsConstructor = isConstructor,
                IsAsync = false,
                Throws = false,
                Visibility = Visibility.Public,
                CSSignature = new List<ArgumentDecl>
                {
                    MakeArg(TupleTypeSpec.Empty, "")
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = TestModule
            };
        }

        private static ClassDecl CreateClassDecl()
        {
            return new ClassDecl
            {
                Name = "MyClass",
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyClass"),
                MangledName = "$s4Test7MyClassC",
                Types = new List<TypeDecl>(),
                Methods = new List<MethodDecl>(),
                Properties = new List<PropertyDecl>(),
                Operators = new List<OperatorDecl>(),
                Conformances = new List<TypeConformance>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                IsFinal = false,
                ParentDecl = null,
                ModuleDecl = TestModule
            };
        }

        private static StructDecl CreateStructDecl(bool isFrozen = false)
        {
            return new StructDecl
            {
                Name = "MyStruct",
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                MangledName = "$s4Test8MyStructV",
                IsFrozen = isFrozen,
                Types = new List<TypeDecl>(),
                Methods = new List<MethodDecl>(),
                Properties = new List<PropertyDecl>(),
                Operators = new List<OperatorDecl>(),
                Conformances = new List<TypeConformance>(),
                MetadataAccessor = "",
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = TestModule
            };
        }

        #endregion

        #region Bug Fix: EmitThunk uses original mangled name (not already-mutated MangledName)

        [Fact]
        public void EmitThunk_WithOriginalMangledName_UsesOriginalForSwiftCallTarget()
        {
            // BUG 1: EmitThunk was reading methodDecl.MangledName which had already been
            // overwritten with the thunk symbol, causing double-hashing and wrong call targets.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyClass", new TypeRecord
            {
                Kind = TypeRecordKind.Class,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyClass"),
                MetadataAccessor = "$s4Test7MyClassCMa",
                Flags = TypeRecordFlags.None
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(
                methodType: MethodType.Static,
                parentDecl: parentDecl);
            var originalName = "$s4Test7MyClass6doWorkyyFZ";
            method.MangledName = originalName;

            // Simulate what MethodHandler does: overwrite MangledName with thunk symbol
            var thunkSymbol = NativeThunkEmitter.GetThunkSymbol(method, "Test");
            method.MangledName = thunkSymbol; // NOW MangledName is the thunk symbol

            var env = new MethodEnvironment(method, db);
            var asmBuilder = new System.Text.StringBuilder();

            // Pass the ORIGINAL name — EmitThunk should use it, not the mutated MangledName
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder, originalName);

            Assert.True(result);
            var asm = asmBuilder.ToString();

            // The assembly should reference the ORIGINAL Swift symbol (with underscore prefix),
            // not a double-hashed thunk symbol
            Assert.Contains("_$s4Test7MyClass6doWorkyyFZ", asm);

            // The thunk symbol should be the exported .globl symbol
            var expectedThunkSymbol = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", originalName);
            Assert.Contains(expectedThunkSymbol, asm);
        }

        [Fact]
        public void EmitThunk_WithoutOriginalName_FallsBackToMangledName()
        {
            // Backward compatibility: when originalSwiftMangledName is null,
            // EmitThunk should use methodDecl.MangledName (for tests and legacy paths).
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyClass", new TypeRecord
            {
                Kind = TypeRecordKind.Class,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyClass"),
                MetadataAccessor = "$s4Test7MyClassCMa",
                Flags = TypeRecordFlags.None
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(
                methodType: MethodType.Static,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyFZ";

            var env = new MethodEnvironment(method, db);
            var asmBuilder = new System.Text.StringBuilder();

            // Don't pass originalSwiftMangledName — should still work
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            Assert.Contains("_$s4Test7MyClass6doWorkyyFZ", asmBuilder.ToString());
        }

        #endregion

        #region Bug Fix: GetCallingConvention returns correct convention per WrapperStrategy

        [Theory]
        [InlineData(WrapperStrategy.CdeclConstructor)]
        [InlineData(WrapperStrategy.CdeclProperty)]
        [InlineData(WrapperStrategy.CdeclMethod)]
        [InlineData(WrapperStrategy.NativeThunk)]
        public void GetCallingConvention_WrapperOrThunk_ReturnsCdecl(WrapperStrategy strategy)
        {
            // Methods with @_cdecl wrappers or thunks should use Cdecl.
            var method = CreateMethodDecl();
            method.WrapperStrategy = strategy;

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Cdecl, convention);
        }

        [Fact]
        public void GetCallingConvention_None_ReturnsSwift()
        {
            // Methods with WrapperStrategy.None target raw Swift symbols
            // and must use CallConvSwift, not CallConvCdecl.
            var method = CreateMethodDecl();
            method.WrapperStrategy = WrapperStrategy.None;
            method.UsesWrapperLibrary = false;

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Swift, convention);
        }

        [Fact]
        public void GetCallingConvention_SilgenNameWrapper_ReturnsSwift()
        {
            // BUG 1 FIX: @_silgen_name wrappers (ObjCOverridePropertyWrapper, DefaultParameterOverload
            // without @_cdecl, standalone closure wrappers that can't convert to @_cdecl) set
            // UsesWrapperLibrary=true but do NOT set UsesCdeclWrapper. These use Swift calling convention
            // because @_silgen_name only assigns a symbol name — the function uses Swift ABI.
            var method = CreateMethodDecl();
            method.WrapperStrategy = WrapperStrategy.None;
            method.UsesWrapperLibrary = true;
            // No @_cdecl flag set — this is a @_silgen_name wrapper

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Swift, convention);
        }

        [Fact]
        public void GetCallingConvention_ClosureCdeclWithoutMethodWrapper_ReturnsSwift()
        {
            // Standalone closure wrapper that couldn't convert to @_cdecl
            // (CanConvertToCdecl=false). HasClosureCdeclWrapper is set for closure callback
            // marshalling, but the wrapper function itself is @_silgen_name → Swift convention.
            var method = CreateMethodDecl();
            method.WrapperStrategy = WrapperStrategy.None;
            method.HasClosureCdeclWrapper = true;
            method.UsesWrapperLibrary = true;
            // UsesCdeclMethodWrapper NOT set — @_silgen_name wrapper

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Swift, convention);
        }

        [Fact]
        public void GetCallingConvention_ClosureCdeclWithMethodWrapper_ReturnsCdecl()
        {
            // Closure wrapper that DID convert to @_cdecl (CanConvertToCdecl=true).
            // UsesCdeclMethodWrapper is set, so convention should be Cdecl.
            var method = CreateMethodDecl();
            method.UsesCdeclMethodWrapper = true;
            method.HasClosureCdeclWrapper = true;
            method.UsesWrapperLibrary = true;

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Cdecl, convention);
        }

        [Fact]
        public void GetCallingConvention_OptionalPointerWithoutMethodWrapper_ReturnsSwift()
        {
            // OptionalPointerWrapper that couldn't convert to @_cdecl — uses @_silgen_name.
            var method = CreateMethodDecl();
            method.WrapperStrategy = WrapperStrategy.None;
            method.HasOptionalPointerWrapper = true;
            method.UsesWrapperLibrary = true;

            var convention = WrapperValidation.GetCallingConvention(method);

            Assert.Equal(PInvokeCallingConvention.Swift, convention);
        }

        #endregion

        #region Bug Fix: ThunkAssemblyEmitted prevents duplicate emission

        [Fact]
        public void ThunkAssemblyEmitted_DefaultFalse()
        {
            // BUG 3: PropertyHandler/SubscriptHandler emit thunks, then MethodHandler emits again.
            var method = CreateMethodDecl();
            Assert.False(method.ThunkAssemblyEmitted);
        }

        [Fact]
        public void ThunkAssemblyEmitted_PreventsDuplicateEmission()
        {
            // BUG 3: When ThunkAssemblyEmitted is true, MethodHandler should skip EmitThunk.
            // This test verifies the flag can be set and read correctly.
            var method = CreateMethodDecl();
            method.ThunkAssemblyEmitted = true;
            Assert.True(method.ThunkAssemblyEmitted);

            // Verify the flag doesn't affect UsesNativeThunk or other computed properties
            method.WrapperStrategy = WrapperStrategy.NativeThunk;
            Assert.True(method.UsesNativeThunk);
            Assert.True(method.ThunkAssemblyEmitted);
        }

        #endregion

        #region Bug Fix: PInvokeEmitHelper renders correct calling convention

        [Fact]
        public void PInvokeEmitHelper_CdeclConvention_EmitsCallConvCdecl()
        {
            var info = new PInvokeEmissionInfo
            {
                LibraryPath = "libTest.dylib",
                EntryPoint = "thunk_Test_12345678",
                MethodName = "DoWork",
                ReturnType = "void",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl
            };

            var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
            var output = string.Join("\n", lines);

            Assert.Contains("CallConvCdecl", output);
            Assert.DoesNotContain("CallConvSwift", output);
        }

        [Fact]
        public void PInvokeEmitHelper_SwiftConvention_EmitsCallConvSwift()
        {
            // BUG 2: FormatDeclarationLines hardcoded CallConvCdecl, ignoring the CallingConvention property.
            var info = new PInvokeEmissionInfo
            {
                LibraryPath = "libTest.dylib",
                EntryPoint = "$s4Test6doWorkyyF",
                MethodName = "DoWork",
                ReturnType = "void",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Swift
            };

            var lines = PInvokeEmitHelper.FormatDeclarationLines(info);
            var output = string.Join("\n", lines);

            Assert.Contains("CallConvSwift", output);
            Assert.DoesNotContain("CallConvCdecl", output);
        }

        #endregion

        #region Bug Fix: SwiftCallTargetResolver explicit mangled name overload

        [Fact]
        public void SwiftCallTargetResolver_ExplicitMangledName_UsesProvidedName()
        {
            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = true; // No Tj suffix
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);

            // Overwrite MangledName to simulate thunk routing
            method.MangledName = "_thunk_Test_12345678";
            var originalName = "$s4Test7MyClass6doWorkyyF";

            // Resolve using explicit mangled name — should use the original, not the mutated one
            var resolved = SwiftCallTargetResolver.Resolve(originalName, method, parentDecl);

            Assert.Equal(originalName, resolved);
            Assert.DoesNotContain("thunk", resolved);
        }

        [Fact]
        public void SwiftCallTargetResolver_ExplicitMangledName_AppendsTjForNonFinalClass()
        {
            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = false;
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsFinal = false;

            var originalName = "$s4Test7MyClass6doWorkyyF";
            var resolved = SwiftCallTargetResolver.Resolve(originalName, method, parentDecl);

            Assert.Equal("$s4Test7MyClass6doWorkyyFTj", resolved);
        }

        [Fact]
        public void SwiftCallTargetResolver_ResolveWithPrefix_ExplicitMangledName()
        {
            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = true;
            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);

            var originalName = "$s4Test7MyClass6doWorkyyFZ";
            var resolved = SwiftCallTargetResolver.ResolveWithPrefix(originalName, method, parentDecl);

            Assert.Equal("_$s4Test7MyClass6doWorkyyFZ", resolved);
        }

        #endregion

        #region Bug Fix: PInvokeDeclaration uses CallingConvention property

        [Fact]
        public void PInvokeDeclaration_DefaultCallingConvention_IsCdecl()
        {
            // Default should be Cdecl for backward compatibility (most helper P/Invokes target @_cdecl).
            var decl = new PInvokeDeclaration
            {
                LibraryPath = "libTest.dylib",
                EntryPoint = "SBW_Test",
                MethodName = "Test",
                ReturnType = "void",
                ParametersString = ""
            };

            Assert.Equal(PInvokeCallingConvention.Cdecl, decl.CallingConvention);
        }

        [Fact]
        public void PInvokeDeclaration_SwiftConvention_EmitsCallConvSwift()
        {
            // BUG 2 FIX: PInvokeDeclaration.Emit() should use the CallingConvention property
            // instead of hardcoding Cdecl. When targeting a @_silgen_name wrapper, the convention
            // should be Swift.
            var decl = new PInvokeDeclaration
            {
                LibraryPath = "libTest.dylib",
                EntryPoint = "SBW_Test",
                MethodName = "Test",
                ReturnType = "void",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Swift
            };

            var sw = new System.IO.StringWriter();
            var csWriter = new CSharpWriter(sw);
            decl.Emit(csWriter);
            var output = sw.ToString();

            Assert.Contains("CallConvSwift", output);
            Assert.DoesNotContain("CallConvCdecl", output);
        }

        [Fact]
        public void PInvokeDeclaration_CdeclConvention_EmitsCallConvCdecl()
        {
            var decl = new PInvokeDeclaration
            {
                LibraryPath = "libTest.dylib",
                EntryPoint = "SBW_Test",
                MethodName = "Test",
                ReturnType = "void",
                ParametersString = "",
                CallingConvention = PInvokeCallingConvention.Cdecl
            };

            var sw = new System.IO.StringWriter();
            var csWriter = new CSharpWriter(sw);
            decl.Emit(csWriter);
            var output = sw.ToString();

            Assert.Contains("CallConvCdecl", output);
            Assert.DoesNotContain("CallConvSwift", output);
        }

        #endregion

        #region Frozen struct self — all sizes accepted for thunking

        [Theory]
        [InlineData(16)]
        [InlineData(24)]
        [InlineData(32)]
        public void ShouldEmitThunk_FrozenStructSelf_LargerThan8B_ReturnsTrue(int inlineSize)
        {
            // >8B frozen struct self: swiftcc passes self as pointer in x20.
            // PInvokeEmitter emits IntPtr for thunked methods. Thunk's
            // `mov x20, x{ParameterCount}` forwards the pointer correctly.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            var fieldCount = inlineSize / 8;
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen,
                InlineSize = inlineSize,
                AbiFieldLayout = string.Join(",", Enumerable.Repeat("i", fieldCount)),
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructSelf_8B_ReturnsTrue()
        {
            // ≤8B frozen struct: IsSelfTypeLowerable accepts this (since Phase 1).
            // Note: swiftcc passes ≤8B self by VALUE in x20, but PInvokeEmitter emits
            // IntPtr (pointer) for thunked methods. This is safe in practice because
            // ≤8B frozen struct methods are @inlinable, have no TBD export, and are
            // filtered by IsSwiftCallTargetExported before reaching assembly emission.
            // This test verifies the gate accepts the size — not calling-convention correctness.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 8,
                AbiFieldLayout = "i",
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructSelf_UnknownInlineSize_ReturnsFalse()
        {
            // Without InlineSize we can't confirm safe thunking — conservatively reject.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen,
                InlineSize = null,  // Unknown
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructStaticMethod_LargeSelf_ReturnsTrue()
        {
            // Static methods don't pass self — large frozen struct self should not block thunk.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 32,
                AbiFieldLayout = "i,i,i,i",
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            // Static method — no self parameter, so large struct size is irrelevant
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructSelf_FloatFields_32B_ReturnsTrue()
        {
            // Frozen struct with mixed int/float fields, 32B (4 register slots).
            // Field layout is irrelevant for self thunking — the thunk passes a pointer
            // (IntPtr from PInvokeEmitter) in x20, not decomposed register values.
            // TypeLowering.SelfLowering models this as 4 direct slots, but ThunkAssemblyEmitter
            // never reads SelfLowering — it only does `mov x20, x{ParameterCount}`.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                InlineSize = 32,
                AbiFieldLayout = "i,f,i,f", // mixed int/float — worst case for register mapping
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            // Accepted: self is a pointer in x20 regardless of field layout
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructSelf_16B_FloatOnly_ReturnsTrue()
        {
            // Frozen struct with only float fields (like CGPoint {Double, Double}).
            // Same rationale: thunk passes pointer, doesn't decompose registers.
            var structDecl = CreateStructDecl(isFrozen: true);
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.HasFloatFields,
                InlineSize = 16,
                AbiFieldLayout = "f,f",
                MetadataAccessor = "$s4Test8MyStructVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: structDecl);
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };
            var env = new MethodEnvironment(method, db);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        #endregion

        #region Bug Fix: Multi-slot value parameters rejected from thunks

        [Fact]
        public void ShouldEmitThunk_MultiSlotParam_ReturnsFalse()
        {
            // Multi-slot value parameters (e.g., 16B struct = 2 registers) can't be thunked:
            // cdecl and swiftcc may disagree on register file for mixed-type structs,
            // and assembly only does simple 1:1 register shifting.
            var classDecl = CreateClassDecl();
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.Point", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "Point"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.Point"),
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 16,
                AbiFieldLayout = "i,i",  // 2 integer slots
                MetadataAccessor = "$s4Test5PointVMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: classDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, ""),
                MakeArg(new NamedTypeSpec("Test.Point"), "point")
            };
            var env = new MethodEnvironment(method, db);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_SingleSlotParam_ReturnsTrue()
        {
            // Single-slot parameters (Int, Double, pointer) are safe for thunk.
            var env = CreateMethodEnv(
                methodType: MethodType.Static,
                parentDecl: CreateClassDecl());
            // Default env has void return, no params — add a simple Int param
            env.MethodDecl.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "value")
            };

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        #endregion

        #region Bug Fix: TBD symbol lookup underscore prefix mismatch

        [Fact]
        public void ShouldEmitThunk_SymbolInExportedSymbols_ReturnsTrue()
        {
            // BUG: IsSwiftCallTargetExported added "_" prefix before checking ExportedSymbols,
            // but ExportedSymbols stores symbols WITHOUT the leading underscore (stripped by TBD parser).
            // This caused ALL methods (846/1239 = 68%) to be rejected from thunk emission.
            var module = new ModuleDecl
            {
                Name = "Test",
                ParentDecl = null,
                ModuleDecl = null,
                Types = new List<TypeDecl>(),
                Protocols = new List<ProtocolDecl>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Dependencies = new List<string>(),
                ExportedSymbols = new HashSet<string>
                {
                    "$s4Test7MyClass6doWorkyyFZ" // Without leading underscore, as TBD parser stores it
                }
            };

            var parentDecl = CreateClassDecl();
            parentDecl.ModuleDecl = module;

            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyFZ";
            method.ModuleDecl = module;
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };

            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            var env = new MethodEnvironment(method, db);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_SymbolNotInExportedSymbols_ReturnsFalse()
        {
            // Symbol not in TBD — should correctly reject (e.g., ObjC-routed method).
            var module = new ModuleDecl
            {
                Name = "Test",
                ParentDecl = null,
                ModuleDecl = null,
                Types = new List<TypeDecl>(),
                Protocols = new List<ProtocolDecl>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Dependencies = new List<string>(),
                ExportedSymbols = new HashSet<string>
                {
                    "$s4Test5OtheryyF" // Different symbol
                }
            };

            var parentDecl = CreateClassDecl();
            parentDecl.ModuleDecl = module;

            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyFZ";
            method.ModuleDecl = module;
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };

            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            var env = new MethodEnvironment(method, db);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_TjSymbolInExportedSymbols_ReturnsTrue()
        {
            // Non-final class instance methods need Tj dispatch thunk suffix.
            // Verify the Tj-suffixed symbol is looked up correctly in ExportedSymbols.
            var module = new ModuleDecl
            {
                Name = "Test",
                ParentDecl = null,
                ModuleDecl = null,
                Types = new List<TypeDecl>(),
                Protocols = new List<ProtocolDecl>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Dependencies = new List<string>(),
                ExportedSymbols = new HashSet<string>
                {
                    "$s4Test7MyClass6doWorkyyFTj" // Tj dispatch thunk, no underscore prefix
                }
            };

            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = false;
            parentDecl.ModuleDecl = module;

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClass6doWorkyyF";
            method.IsFinal = false;
            method.ModuleDecl = module;
            method.CSSignature = new List<ArgumentDecl> { MakeArg(TupleTypeSpec.Empty, "") };

            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            var env = new MethodEnvironment(method, db);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NullExportedSymbols_ReturnsTrue()
        {
            // No TBD available — optimistically allow thunk emission.
            var env = CreateMethodEnv(
                methodType: MethodType.Static,
                parentDecl: CreateClassDecl());

            // TestModule has ExportedSymbols = null (default)
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        #endregion

        #region Mock TypeDatabase

        private class ThunkMockTypeDatabase : ITypeDatabase
        {
            private readonly Dictionary<string, TypeRecord> _types = new();
            private readonly bool _xcframeworkMode;

            public ThunkMockTypeDatabase(bool xcframeworkMode = true)
            {
                _xcframeworkMode = xcframeworkMode;
            }

            public string? AsyncLibraryName => _xcframeworkMode ? "SwiftBindings" : null;

            public void AddType(string moduleQualifiedName, TypeRecord record)
            {
                _types[moduleQualifiedName] = record;
            }

            public bool IsTypeProcessed(SwiftTypeName swiftTypeName) => false;

            public bool TryGetTypeRecord(SwiftTypeName swiftTypeName, [NotNullWhen(returnValue: true)] out TypeRecord? record)
            {
                return _types.TryGetValue(swiftTypeName.ModuleQualifiedName, out record);
            }

            public string GetLibraryPath(string moduleName) => $"lib{moduleName}.dylib";

            public void UpdateTypeRecord(SwiftTypeName name, TypeRecord record)
            {
                _types[name.ModuleQualifiedName] = record;
            }
        }

        #endregion
    }
}
