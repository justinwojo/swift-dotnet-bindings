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
        public void ShouldEmitThunk_ClassConstructor_ReturnsTrue()
        {
            // Class constructors: allocating init returns pointer in x0 (no indirect result).
            // Thunk puts metatype in x20 via metadata accessor.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateClassDecl());

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
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

        [Fact]
        public void ShouldEmitThunk_StringReturn_NoAbiFieldLayout_ReturnsFalse()
        {
            // Swift.String is a 16-byte frozen struct without ABI field layout in SwiftDatabase.xml.
            // TypeLowering can't determine register assignments, so we can't know if the return
            // is HFA (d0+d1, safe for tail call) or non-HFA (x0+x1, may differ from cdecl).
            // Must reject to @_cdecl wrapper which converts String to Utf8Slice.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Swift.String", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Swift", "SwiftString"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Swift.String"),
                MetadataAccessor = "$sSSSMa",
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 16, // 16 bytes, no AbiFieldLayout
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Swift.String"), ""), // return type
                MakeArg(new NamedTypeSpec("Swift.Int"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructReturn_WithAbiFieldLayout_ReturnsTrue()
        {
            // A frozen struct WITH ABI field layout can be lowered by TypeLowering,
            // so the thunk emitter knows the exact register assignments. This should
            // be eligible for thunking (tail call or full-frame with return bridge).
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.FrozenPoint", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "FrozenPoint"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.FrozenPoint"),
                MetadataAccessor = "$s4Test11FrozenPointVMa",
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 16,
                AbiFieldLayout = "f,f", // two float fields — TypeLowering can resolve
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.FrozenPoint"), ""), // return type
                MakeArg(new NamedTypeSpec("Swift.Int"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_OptionalStringReturn_ReturnsFalse()
        {
            // Optional<String> can't be lowered by TypeLowering (inner type String has no
            // AbiFieldLayout). Without knowing the register layout, the thunk can't safely
            // bridge the return. Must reject to @_cdecl wrapper.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                // Return type: Optional<String> — not in the type database
                MakeArg(new NamedTypeSpec("Swift.Optional",
                    new NamedTypeSpec("Swift.String")), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_OptionalValueTypeReturn_ReturnsFalse()
        {
            // Optional<Int32> returns need indirect result (resultPtr buffer).
            // Even though Int32 is a known type with ABI layout, thunks can't handle
            // Optional returns — the @_cdecl wrapper writes to a caller-provided buffer.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                // Return type: Optional<Int32> — concrete value type
                MakeArg(new NamedTypeSpec("Swift.Optional",
                    new NamedTypeSpec("Swift.Int32")), ""),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_OptionalDoubleReturn_ReturnsFalse()
        {
            // Optional<Double> static property getter — same issue as Optional<Int32>.
            // Verifies the fix for static property accessors (GlobalSettings.defaultTimeout).
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Swift.Optional",
                    new NamedTypeSpec("Swift.Double")), ""),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_DispatchThunkGetterEnumReturn_ReturnsFalse()
        {
            // Non-final getter on non-final class → dispatch thunk (vgTj).
            // Dispatch thunk uses x9 for vtable (preserving x8) and the getter writes
            // the result to [x8]. Our bridge thunk doesn't set x8, so this would crash.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyEnum"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FinalGetterEnumReturn_ReturnsTrue()
        {
            // Final getter on non-final class → direct dispatch (no Tj suffix).
            // Direct-dispatch getters use standard swiftcc return convention,
            // which TypeLowering handles correctly — no x8 hazard.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateClassDecl(); // non-final class
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.IsFinal = true; // final getter → no dispatch thunk
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyEnum"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FinalClassGetterEnumReturn_ReturnsTrue()
        {
            // Getter on final class → direct dispatch (no Tj suffix, regardless of method finality).
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = true; // final class → no dispatch thunk
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyEnum"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_DispatchThunkGetterClassReturn_ReturnsTrue()
        {
            // Dispatch thunk getter returning class type — returns in x0, no x8 issue.
            // The dispatch thunk clobbers x8 for vtable lookup, but class return is in x0.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.ReturnClass", new TypeRecord
            {
                Kind = TypeRecordKind.Class,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "ReturnClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.ReturnClass"),
                MetadataAccessor = "$s4Test11ReturnClassCMa",
                Flags = TypeRecordFlags.None,
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.ReturnClass"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_PropertySetterEnumParam_NonFinalClass_ReturnsFalse()
        {
            // Setter dispatch thunks (Tj) pass the new value via indirect buffer.
            // For non-class types (enums), the setter reads from [x0] (indirect), but the
            // thunk passes the raw value in x0 → SIGSEGV. Non-final class = Tj dispatch.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.Name = "backgroundBehavior_Set";
            method.CSSignature = new List<ArgumentDecl>
            {
                // Setter: void return
                MakeArg(new TupleTypeSpec(), ""),
                MakeArg(new NamedTypeSpec("Test.MyEnum"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_PropertySetterClassParam_NonFinalClass_ReturnsTrue()
        {
            // Setter with class-typed value param on non-final class. The class value IS
            // a pointer (ARC-retained), so indirect buffer dereferencing works correctly.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyClass", new TypeRecord
            {
                Kind = TypeRecordKind.Class,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyClass"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyClass"),
                MetadataAccessor = "$s4Test7MyClassCMa",
                Flags = TypeRecordFlags.None,
            });

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.Name = "delegate_Set";
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new TupleTypeSpec(), ""),
                MakeArg(new NamedTypeSpec("Test.MyClass"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_PropertySetterEnumParam_FinalClass_ReturnsTrue()
        {
            // Setter with frozen enum value param on FINAL class. No Tj dispatch — uses direct
            // dispatch with standard calling convention. Thunk is safe.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = true;
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.Name = "mode_Set";
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new TupleTypeSpec(), ""),
                MakeArg(new NamedTypeSpec("Test.MyEnum"), "value"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NonFrozenStructGetterAccessor_ReturnsFalse()
        {
            // Non-frozen struct property getters use opaque accessor calling conventions:
            // the getter writes its result to an indirect buffer via x8, even for small
            // return types (e.g., 1-byte enum). Our thunk doesn't set x8 → SIGSEGV.
            // Verified by disassembling Nuke's ImageRequest.priority getter (arm64):
            //   ldr x9, [x20]; ...; strb w9, [x8]; ret
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateStructDecl(isFrozen: false);
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyEnum"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NonFrozenStructSetterAccessor_ReturnsFalse()
        {
            // Non-frozen struct property setters read the new value from an indirect
            // buffer at [x0], not from x0 directly. Our thunk passes the raw value
            // in x0, which the setter dereferences as a pointer → SIGSEGV.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.SimpleEnum,
            });

            var parentDecl = CreateStructDecl(isFrozen: false);
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new TupleTypeSpec(), ""),
                MakeArg(new NamedTypeSpec("Test.MyEnum"), "value"),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenStructGetterAccessor_ReturnsTrue()
        {
            // Frozen struct property getters use standard swiftcc return convention —
            // thunks work correctly for these.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Test.MyEnum", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyEnum"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyEnum"),
                MetadataAccessor = "$s4Test6MyEnumOMa",
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
            });
            db.AddType("Test.MyStruct", new TypeRecord
            {
                Kind = TypeRecordKind.Struct,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Test", "MyStruct"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Test.MyStruct"),
                MetadataAccessor = "$s4Test8MyStructVMa",
                Flags = TypeRecordFlags.Frozen,
                InlineSize = 16,
            });

            var parentDecl = CreateStructDecl(isFrozen: true);
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsAccessor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyEnum"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "self"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NonFrozenStructNonAccessorMethod_ReturnsTrue()
        {
            // Non-frozen struct regular methods (not property accessors) use standard
            // swiftcc calling convention — self as pointer in x20, return in x0.
            // Only property ACCESSORS use opaque indirect conventions.
            var parentDecl = CreateStructDecl(isFrozen: false);
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                parentDecl: parentDecl);

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ConstructorWithClassParam_ReturnsFalse()
        {
            // Constructors with class reference parameters follow +1 owned convention in Swift.
            // The init body retains for storage, then releases the caller's reference.
            // Our thunk passes +0 (raw pointer), causing double-release on finalization.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsConstructor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyClass"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "value"),
                MakeArg(new NamedTypeSpec("Test.MyClass"), "other"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ConstructorWithOptionalClassParam_ReturnsFalse()
        {
            // Optional<Class> params in constructors also follow +1 owned convention.
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: parentDecl);
            method.IsConstructor = true;
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Test.MyClass"), ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "value"),
                MakeArg(new NamedTypeSpec("Swift.Optional",
                    new NamedTypeSpec("Test.MyClass")), "previous"),
            };

            var env = new MethodEnvironment(method, db);
            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ConstructorWithValueTypeParams_ReturnsTrue()
        {
            // Constructors with only value type params are fine — no +1 ownership issue.
            // Use CreateMethodEnv which handles metadata accessor and type registration.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateClassDecl());

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
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

        [Fact]
        public void ShouldEmitThunk_NonFrozenEnumParam_ReturnsFalse()
        {
            // Non-frozen simple enums are passed indirectly in Swift ABI (resilient layout):
            // the Swift function dereferences x0 as a pointer to the enum value, but the
            // thunk passes the raw int value in x0 → SIGSEGV (null deref for case 0).
            // KeychainAccess.Accessibility crash: 14 tests pass, test 15 (WithAccessibility)
            // crashes on both simulator (Mono) and device (NativeAOT).
            var classDecl = CreateClassDecl();
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Lib.Accessibility", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Lib", "Accessibility"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Lib.Accessibility"),
                Flags = TypeRecordFlags.SimpleEnum, // SimpleEnum but NOT Frozen
                InlineSize = 1,
                MetadataAccessor = "$s3Lib13AccessibilityOMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Instance, parentDecl: classDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(new NamedTypeSpec("Lib.Accessibility"), ""), // return: class
                MakeArg(new NamedTypeSpec("Lib.Accessibility"), "accessibility")
            };
            var env = new MethodEnvironment(method, db);

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_FrozenEnumParam_ReturnsTrue()
        {
            // Frozen simple enums are safe for thunks — the value is passed directly
            // in a register (x0) and the Swift function reads it as a value, not a pointer.
            var classDecl = CreateClassDecl();
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);
            db.AddType("Lib.Color", new TypeRecord
            {
                Kind = TypeRecordKind.Enum,
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("Lib", "Color"),
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("Lib.Color"),
                Flags = TypeRecordFlags.Frozen | TypeRecordFlags.SimpleEnum,
                InlineSize = 1,
                MetadataAccessor = "$s3Lib5ColorOMa"
            });

            var method = CreateMethodDecl(methodType: MethodType.Static, parentDecl: classDecl);
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, ""),
                MakeArg(new NamedTypeSpec("Lib.Color"), "color")
            };
            var env = new MethodEnvironment(method, db);

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

        #region Constructor Thunks

        [Fact]
        public void ShouldEmitThunk_FrozenStructConstructor_ReturnsFalse()
        {
            // Session 4 finding: Struct constructors can't use native thunks because
            // Mono AOT can't JIT the LibraryImport-generated wrapper for struct returns
            // ("Attempting to JIT compile method" in aot-only mode). The @_cdecl wrapper
            // approach (void return + resultPtr) avoids this. The underlying x8 ABI
            // mechanism works (verified empirically), but LibraryImport doesn't produce
            // AOT-compatible wrappers for struct returns.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateStructDecl(isFrozen: true));

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_NonFrozenStructConstructor_ReturnsFalse()
        {
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateStructDecl(isFrozen: false));

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }


        [Fact]
        public void ShouldEmitThunk_FailableClassConstructor_ReturnsFalse()
        {
            // Failable constructors (init?) return Optional<Self> — needs indirect result.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateClassDecl());
            env.MethodDecl.IsFailable = true;

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void ShouldEmitThunk_ClassConstructorWithParams_ReturnsTrue()
        {
            // Class constructor with parameters — thunk saves/restores param registers
            // around metadata accessor call.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: CreateClassDecl());
            env.MethodDecl.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "count"),
                MakeArg(new NamedTypeSpec("Swift.Bool"), "flag")
            };

            Assert.True(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void EmitThunk_ClassConstructor_ProducesMetatypeSetup()
        {
            // Verify the thunk assembly calls the metadata accessor and puts metatype in x20.
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
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClassCACycfC";

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            var asm = asmBuilder.ToString();
            // Must call metadata accessor
            Assert.Contains("_$s4Test7MyClassCMa", asm);
            // Must put metatype in x20
            Assert.Contains("mov     x20, x0", asm);
            // Must call the allocating init
            Assert.Contains("_$s4Test7MyClassCACycfC", asm);
        }

        [Fact]
        public void EmitThunk_ClassConstructorWithParams_SavesRestoresRegisters()
        {
            // Constructor with params: metadata accessor clobbers x0-x7,
            // so thunk must save/restore parameter registers around the call.
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
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClassC4nameACSS_tcfC";
            method.CSSignature = new List<ArgumentDecl>
            {
                MakeArg(TupleTypeSpec.Empty, ""),
                MakeArg(new NamedTypeSpec("Swift.Int"), "name")
            };

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            var asm = asmBuilder.ToString();
            // Must save x0 (the param) before metadata accessor call
            Assert.Contains("str     x0", asm);
            // Must restore x0 after metadata accessor call
            Assert.Contains("ldr     x0", asm);
        }

        [Fact]
        public void ShouldEmitThunk_ClassConstructorWithClosureParam_ReturnsFalse()
        {
            // Closure parameters need Swift adapter code — not thunkable.
            var env = CreateMethodEnv(
                methodType: MethodType.Instance,
                isConstructor: true,
                hasClosureParam: true,
                parentDecl: CreateClassDecl());

            Assert.False(NativeThunkEmitter.ShouldEmitThunk(env));
        }

        [Fact]
        public void SwiftCallTargetResolver_Constructor_NoTjSuffix()
        {
            // Constructors use direct dispatch (allocating init is globally exported),
            // NOT vtable dispatch — no Tj suffix.
            var parentDecl = CreateClassDecl();
            parentDecl.IsFinal = false;
            var method = CreateMethodDecl(
                methodType: MethodType.Instance,
                isConstructor: true,
                parentDecl: parentDecl);
            method.MangledName = "$s4Test7MyClassCACycfC";

            var resolved = SwiftCallTargetResolver.Resolve(method, parentDecl);

            Assert.Equal("$s4Test7MyClassCACycfC", resolved);
            Assert.DoesNotContain("Tj", resolved);
        }

        #endregion

        #region Bug Fix: Free function throwing thunks — error out register off-by-one

        [Fact]
        public void EmitThunk_ThrowingFreeFunction_ErrorOutAtCorrectRegister()
        {
            // Free functions have MethodType.Instance (ABI parser defaults non-@static to Instance)
            // but have no self parameter. The thunk must NOT add a self bridge,
            // so error_out is at x{ParameterCount} (not x{ParameterCount+1}).
            // Bug: without the ParentDecl guard, the thunk emits:
            //   mov x19, x3   (wrong — garbage register)
            //   mov x20, x2   (wrong — treats error ptr as self)
            // Fixed: thunk emits:
            //   mov x19, x2   (correct — error out at x2)
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var method = new MethodDecl
            {
                Name = "divide",
                MangledName = "$s4Test6divide1a1bs5Int32VAF_AFtKF",
                MethodType = MethodType.Instance, // Parser sets this for free functions
                IsConstructor = false,
                IsAsync = false,
                Throws = true,
                Visibility = Visibility.Public,
                CSSignature = new List<ArgumentDecl>
                {
                    MakeArg(new NamedTypeSpec("Swift.Int32"), ""),  // return type
                    MakeArg(new NamedTypeSpec("Swift.Int32"), "a"),
                    MakeArg(new NamedTypeSpec("Swift.Int32"), "b")
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = TestModule, // Free function — parent is ModuleDecl, not TypeDecl
                ModuleDecl = TestModule
            };

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            var asm = asmBuilder.ToString();

            // Error out pointer should be at x2 (after 2 int params at x0, x1)
            Assert.Contains("mov     x19, x2", asm);
            // Must NOT contain self bridge (no mov x20, x{N} for self)
            Assert.DoesNotContain("mov     x20, x2", asm);
            Assert.DoesNotContain("mov     x20, x0", asm);
            // Must clear swifterror and store it after call
            Assert.Contains("mov     x21, xzr", asm);
            Assert.Contains("str     x21, [x19]", asm);
            // x21 is callee-saved in AAPCS64 (x19-x28 range) — must save/restore
            Assert.Contains("str     x21, [sp, #32]", asm);  // save in prologue
            Assert.Contains("ldr     x21, [sp, #32]", asm);  // restore in epilogue
            // Frame must be 48 bytes for throwing (vs 32 for non-throwing)
            Assert.Contains("[sp, #-48]!", asm);
        }

        [Fact]
        public void EmitThunk_ThrowingInstanceMethod_SelfBridgeAndErrorOutCorrect()
        {
            // Instance method on a class: self IS at x{ParameterCount},
            // error_out is at x{ParameterCount+1} (after self).
            var db = new ThunkMockTypeDatabase(xcframeworkMode: true);

            var parentDecl = CreateClassDecl();
            var method = new MethodDecl
            {
                Name = "doWork",
                MangledName = "$s4Test7MyClass6doWorkyyKF",
                MethodType = MethodType.Instance,
                IsConstructor = false,
                IsAsync = false,
                Throws = true,
                Visibility = Visibility.Public,
                CSSignature = new List<ArgumentDecl>
                {
                    MakeArg(TupleTypeSpec.Empty, ""),  // void return
                    MakeArg(new NamedTypeSpec("Swift.Int32"), "value")
                },
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = parentDecl,
                ModuleDecl = TestModule
            };

            var env = new MethodEnvironment(method, db);

            var asmBuilder = new System.Text.StringBuilder();
            var result = NativeThunkEmitter.EmitThunk(env, "Test", asmBuilder);

            Assert.True(result);
            var asm = asmBuilder.ToString();

            // Instance method with 1 param: self at x1, error_out at x2
            // Self bridge: mov x20, x1
            Assert.Contains("mov     x20, x1", asm);
            // Error out: mov x19, x2
            Assert.Contains("mov     x19, x2", asm);
            // x21 save/restore for throwing
            Assert.Contains("str     x21, [sp, #32]", asm);
            Assert.Contains("ldr     x21, [sp, #32]", asm);
            Assert.Contains("[sp, #-48]!", asm);
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
