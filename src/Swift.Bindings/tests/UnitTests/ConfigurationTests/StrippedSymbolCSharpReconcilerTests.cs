// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. P/Invoke Detection

    public class ReconcilerPInvokeDetectionTests
    {
        [Fact]
        public void Process_StrippedPInvoke_RemovesPInvokeDeclaration()
        {
            // The P/Invoke is a private trampoline with no public caller, so it
            // disappears from the file but contributes nothing to the public-API
            // skip count — the reconciler only records public surface that vanished.
            var input =
                "namespace Test {\n" +
                "public partial class Foo {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken_method\")]\n" +
                "    private static partial void PInvoke_broken_ABC123(IntPtr ptr);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_method" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_ABC123", result.Content);
            Assert.DoesNotContain("SBW_broken_method", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_FullyQualifiedAttribute_DetectsCorrectly()
        {
            // Trampoline-only stripping: detection works regardless of attribute form
            // (fully qualified or not), but with no public caller there is no public
            // API surface to record, so StrippedMemberCount remains 0.
            var input =
                "public partial class Foo {\n" +
                "    [global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_eq_broken\")]\n" +
                "    internal static partial int PInvoke_eq_DEADBEEF(IntPtr a, IntPtr b);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_eq_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_eq_DEADBEEF", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NativeLibraryPInvoke_NotAffected()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s20SwiftBindingsTestLib_mangled\")]\n" +
                "    private static partial int PInvoke_native_123(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "$s20SwiftBindingsTestLib_mangled" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Contains("PInvoke_native_123", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_SBWFree_NeverStripped()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Free_TestLib\")]\n" +
                "    private static partial void SBW_Free(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Free_TestLib" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Contains("SBW_Free", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InternalVisibility_DetectsCorrectly()
        {
            var input =
                "public partial class Foo {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_broken\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata(TypeMetadataRequest req);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_getMetadata", result.Content);
        }
    }

    #endregion

    #region B. Constructor Stripping (Level 1)

    public class ReconcilerConstructorStrippingTests
    {
        [Fact]
        public void Process_ConstructorCallingStrippedPInvoke_Removed()
        {
            // Acceptance bar: a stripped wrapper symbol that takes out a public
            // constructor must produce a SkippedItem keyed on the constructor
            // (name = type name, kind = Method) — *not* on the internal P/Invoke
            // trampoline. The trampoline disappears too, but it's implementation
            // noise; the public report should describe what the consumer lost.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_init_broken\")]\n" +
                "    private static partial void PInvoke_init_ABC123(IntPtr resultPtr, int value);\n" +
                "\n" +
                "    public MyClass(int value)\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            PInvoke_init_ABC123(resultPtr, value);\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_init_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_init_ABC123", result.Content);
            Assert.DoesNotContain("public MyClass(int value)", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
            var member = Assert.Single(result.StrippedMembers);
            Assert.Equal("MyClass", member.Name);
            Assert.Equal(BindingItemKind.Method, member.Kind);
            Assert.Equal(IdentityConfidence.Heuristic, member.Confidence);
            Assert.DoesNotContain(result.StrippedMembers, m => m.Name.StartsWith("PInvoke_", System.StringComparison.Ordinal));
        }

        [Fact]
        public void Process_PropertyHelperStripped_RecordsPublicProperty()
        {
            // Acceptance bar: when a property helper (private trampoline) is
            // stripped in Step B and its public property forwarder follows in
            // Step C, the report records the property — not the helper, not the
            // P/Invoke. Helpers are filtered by the public-only Add gate.
            var input =
                "public partial class MyClass : ISwiftObject {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_value_broken\")]\n" +
                "    private static partial void PInvoke_value_Get_AAA(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private int Value_Get()\n" +
                "    {\n" +
                "        PInvoke_value_Get_AAA(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public int Value\n" +
                "    {\n" +
                "        get => Value_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_value_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_value_Get_AAA", result.Content);
            Assert.DoesNotContain("Value_Get", result.Content);
            Assert.DoesNotContain("public int Value", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
            var member = Assert.Single(result.StrippedMembers);
            Assert.Equal("Value", member.Name);
            Assert.Equal(BindingItemKind.Property, member.Kind);
        }

        [Fact]
        public void Process_CascadeWithTwoPublicMembers_RecordsBoth()
        {
            // Acceptance bar: a cascade that takes out two distinct public
            // members must increment SkippedMembers by two. Independent
            // constructor and method, two stripped wrapper symbols.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_init_broken\")]\n" +
                "    private static partial void PInvoke_init_AAA(IntPtr resultPtr, int value);\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doStuff_broken\")]\n" +
                "    private static partial void PInvoke_doStuff_BBB(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public MyClass(int value)\n" +
                "    {\n" +
                "        PInvoke_init_AAA(resultPtr, value);\n" +
                "    }\n" +
                "\n" +
                "    public static string DoStuff(int arg)\n" +
                "    {\n" +
                "        PInvoke_doStuff_BBB(resultPtr, arg);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_init_broken", "SBW_doStuff_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Equal(2, result.StrippedMemberCount);
            Assert.Contains(result.StrippedMembers, m => m.Name == "MyClass" && m.Kind == BindingItemKind.Method);
            Assert.Contains(result.StrippedMembers, m => m.Name == "DoStuff" && m.Kind == BindingItemKind.Method);
        }
    }

    #endregion

    #region C. Method Stripping (Level 1)

    public class ReconcilerMethodStrippingTests
    {
        [Fact]
        public void Process_StaticMethodCallingStrippedPInvoke_Removed()
        {
            // Non-virtual static methods are safe to strip
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doStuff_broken\")]\n" +
                "    private static partial void PInvoke_doStuff_DEF456(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public static string DoStuff(int arg)\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            PInvoke_doStuff_DEF456(resultPtr, arg);\n" +
                "            return result;\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doStuff_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_doStuff_DEF456", result.Content);
            Assert.DoesNotContain("DoStuff", result.Content);
        }

        [Fact]
        public void Process_VirtualMethodNotInInterface_Stripped()
        {
            // Virtual methods that don't implement any interface CAN be stripped
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doStuff_broken\")]\n" +
                "    private static partial void PInvoke_doStuff_DEF456(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public virtual string DoStuff(int arg)\n" +
                "    {\n" +
                "        PInvoke_doStuff_DEF456(resultPtr, arg);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doStuff_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_doStuff_DEF456", result.Content);
            Assert.DoesNotContain("DoStuff", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InterfaceMethodCallingStrippedPInvoke_Preserved()
        {
            // Methods implementing protocol interface members must NOT be stripped
            var input =
                "public interface IConnectionDelegate {\n" +
                "    void DidReceive(ServerEvent @event);\n" +
                "}\n" +
                "public partial class WebSocketServer : ISwiftObject, IConnectionDelegate {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_didReceive_broken\")]\n" +
                "    private static partial void PInvoke_didReceive_AAA(IntPtr evt, IntPtr self);\n" +
                "\n" +
                "    public virtual void DidReceive(ServerEvent @event)\n" +
                "    {\n" +
                "        PInvoke_didReceive_AAA(@event.Handle, selfPtr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_didReceive_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // Both preserved — DidReceive implements IConnectionDelegate
            Assert.Contains("PInvoke_didReceive_AAA", result.Content);
            Assert.Contains("DidReceive", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InterfacePropertyHelper_Preserved()
        {
            // Property helpers for interface properties must not be stripped
            var input =
                "public interface IProcessingMode {\n" +
                "    string ModeName { get; }\n" +
                "}\n" +
                "public partial class SimpleMode : ISwiftObject, IProcessingMode {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_modeName_broken\")]\n" +
                "    private static partial void PInvoke_modeName_Get_BBB(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string ModeName_Get()\n" +
                "    {\n" +
                "        PInvoke_modeName_Get_BBB(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string ModeName\n" +
                "    {\n" +
                "        get => ModeName_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_modeName_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // All preserved — ModeName is an interface property on this type
            Assert.Contains("PInvoke_modeName_Get_BBB", result.Content);
            Assert.Contains("ModeName_Get", result.Content);
            Assert.Contains("public virtual string ModeName", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_TypeScopeDoesNotLeakPastClosingBrace()
        {
            // After TypeA's closing brace, TypeB must NOT inherit TypeA's interface protection.
            // This verifies the line-to-type map correctly pops types at their closing brace.
            var input =
                "public interface IFoo {\n" +
                "    void DoWork();\n" +
                "}\n" +
                "public partial class TypeA : ISwiftObject, IFoo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doWorkA\")]\n" +
                "    private static partial void PInvoke_doWork_AAA(IntPtr self);\n" +
                "\n" +
                "    public virtual void DoWork()\n" +
                "    {\n" +
                "        PInvoke_doWork_AAA(selfPtr);\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB : ISwiftObject {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doWorkB\")]\n" +
                "    private static partial void PInvoke_doWork_BBB(IntPtr self);\n" +
                "\n" +
                "    public virtual void DoWork()\n" +
                "    {\n" +
                "        PInvoke_doWork_BBB(selfPtr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doWorkA", "SBW_doWorkB" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // TypeA: DoWork preserved (implements IFoo)
            Assert.Contains("PInvoke_doWork_AAA", result.Content);
            // TypeB: DoWork stripped (does NOT implement IFoo — scope must not leak from TypeA)
            Assert.DoesNotContain("PInvoke_doWork_BBB", result.Content);
        }

        [Fact]
        public void Process_SameNameMemberOnNonImplementingType_Stripped()
        {
            // INameable declares 'Name'. TypeA implements INameable (protected).
            // TypeB does NOT implement INameable — its 'Name' should be stripped normally.
            var input =
                "public interface INameable {\n" +
                "    string Name { get; }\n" +
                "}\n" +
                "public partial class TypeA : ISwiftObject, INameable {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_nameA_broken\")]\n" +
                "    private static partial void PInvoke_name_Get_AAA(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string Name_Get()\n" +
                "    {\n" +
                "        PInvoke_name_Get_AAA(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB : ISwiftObject {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_nameB_broken\")]\n" +
                "    private static partial void PInvoke_name_Get_BBB(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string Name_Get()\n" +
                "    {\n" +
                "        PInvoke_name_Get_BBB(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_nameA_broken", "SBW_Get_nameB_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // TypeA: preserved (Name implements INameable)
            Assert.Contains("PInvoke_name_Get_AAA", result.Content);
            Assert.Contains("TypeA", result.Content);
            // TypeB: stripped (Name is NOT an interface implementation)
            Assert.DoesNotContain("PInvoke_name_Get_BBB", result.Content);
        }

        [Fact]
        public void Process_MultipleOverloadsCallingStrippedPInvoke_BothRemoved()
        {
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_create_broken\")]\n" +
                "    private static partial void PInvoke_create_111(IntPtr ptr, int a, int b);\n" +
                "\n" +
                "    public MyClass(int a, int b)\n" +
                "    {\n" +
                "        PInvoke_create_111(resultPtr, a, b);\n" +
                "    }\n" +
                "\n" +
                "    public MyClass(int a)\n" +
                "    {\n" +
                "        PInvoke_create_111(resultPtr, a, 0);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_create_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_create_111", result.Content);
            Assert.DoesNotContain("public MyClass(int a, int b)", result.Content);
            Assert.DoesNotContain("public MyClass(int a)", result.Content);
        }
    }

    #endregion

    #region D. Property Stripping (Level 1 + Level 2 Transitivity)

    public class ReconcilerPropertyStrippingTests
    {
        [Fact]
        public void Process_PropertyGetterStripped_HelperAndForwarderRemoved()
        {
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_count_broken\")]\n" +
                "    private static partial int PInvoke_count_Get_AAA(IntPtr self);\n" +
                "\n" +
                "    private int Count_Get()\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            return PInvoke_count_Get_AAA(selfPtr);\n" +
                "        }\n" +
                "    }\n" +
                "\n" +
                "    public int Count\n" +
                "    {\n" +
                "        get => Count_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_count_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // P/Invoke removed
            Assert.DoesNotContain("PInvoke_count_Get_AAA", result.Content);
            // Level 1 helper removed
            Assert.DoesNotContain("Count_Get", result.Content);
            // Level 2 property forwarder removed
            Assert.DoesNotContain("public int Count", result.Content);
        }

        [Fact]
        public void Process_PropertySetterStripped_PropertyEntirelyRemoved()
        {
            // When setter P/Invoke is stripped, the setter helper is removed.
            // The property forwarder that references the setter helper is also removed.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Set_name_broken\")]\n" +
                "    private static partial void PInvoke_name_Set_BBB(IntPtr self, IntPtr val);\n" +
                "\n" +
                "    private void Name_Set(string value)\n" +
                "    {\n" +
                "        PInvoke_name_Set_BBB(selfPtr, valPtr);\n" +
                "    }\n" +
                "\n" +
                "    public string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "        set => Name_Set(value);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Set_name_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_name_Set_BBB", result.Content);
            Assert.DoesNotContain("Name_Set", result.Content);
            // The property references Name_Set, so it's removed entirely
            Assert.DoesNotContain("public string Name", result.Content);
        }
        [Fact]
        public void Process_CrossScopeHelperName_DoesNotStripOtherTypes()
        {
            // When a property is stripped in TypeA, the helper name (e.g., "Id_Get")
            // must NOT cause properties with the same helper name in TypeB to be stripped.
            // This tests the scope-aware Level 2 stripping fix.
            var input =
                "namespace Test {\n" +
                "public partial class TypeA {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_TypeA_id\")]\n" +
                "    private static partial void PInvoke_id_Get_AAA(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string Id_Get()\n" +
                "    {\n" +
                "        return PInvoke_id_Get_AAA(resultPtr, selfPtr);\n" +
                "    }\n" +
                "\n" +
                "    public string Id\n" +
                "    {\n" +
                "        get => Id_Get();\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_TypeB_id\")]\n" +
                "    private static partial void PInvoke_id_Get_BBB(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private ulong Id_Get()\n" +
                "    {\n" +
                "        return PInvoke_id_Get_BBB(selfPtr);\n" +
                "    }\n" +
                "\n" +
                "    public ulong Id\n" +
                "    {\n" +
                "        get => Id_Get();\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            // Only TypeA's wrapper symbol is stripped
            var stripped = new HashSet<string> { "SBW_Get_TypeA_id" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // TypeA: all three (P/Invoke, helper, property) should be removed
            Assert.DoesNotContain("SBW_Get_TypeA_id", result.Content);
            Assert.DoesNotContain("PInvoke_id_Get_AAA", result.Content);

            // TypeB: all three should be PRESERVED (its wrapper was not stripped)
            Assert.Contains("SBW_Get_TypeB_id", result.Content);
            Assert.Contains("PInvoke_id_Get_BBB", result.Content);
            Assert.Contains("public ulong Id", result.Content);
        }

        [Fact]
        public void Process_NestedTypeSameLeafName_DoesNotStripOtherParent()
        {
            // Two nested types share the same leaf name ("Inner") under different parents.
            // Stripping a property in OuterA.Inner must NOT affect OuterB.Inner.
            var input =
                "namespace Test {\n" +
                "public partial class OuterA {\n" +
                "    public partial class Inner {\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_OuterA_Inner_name\")]\n" +
                "        private static partial void PInvoke_name_Get_111(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "        private string Name_Get()\n" +
                "        {\n" +
                "            return PInvoke_name_Get_111(resultPtr, selfPtr);\n" +
                "        }\n" +
                "\n" +
                "        public string Name\n" +
                "        {\n" +
                "            get => Name_Get();\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "public partial class OuterB {\n" +
                "    public partial class Inner {\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_OuterB_Inner_name\")]\n" +
                "        private static partial void PInvoke_name_Get_222(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "        private string Name_Get()\n" +
                "        {\n" +
                "            return PInvoke_name_Get_222(resultPtr, selfPtr);\n" +
                "        }\n" +
                "\n" +
                "        public string Name\n" +
                "        {\n" +
                "            get => Name_Get();\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            // Only OuterA.Inner's wrapper is stripped
            var stripped = new HashSet<string> { "SBW_Get_OuterA_Inner_name" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // OuterA.Inner: stripped
            Assert.DoesNotContain("SBW_Get_OuterA_Inner_name", result.Content);
            Assert.DoesNotContain("PInvoke_name_Get_111", result.Content);

            // OuterB.Inner: preserved
            Assert.Contains("SBW_Get_OuterB_Inner_name", result.Content);
            Assert.Contains("PInvoke_name_Get_222", result.Content);
            // Verify the property in OuterB.Inner survived
            Assert.Contains("public string Name", result.Content);
        }
    }

    #endregion

    #region E. GetMetadata Fallback Exemption

    public class ReconcilerGetMetadataExemptionTests
    {
        [Fact]
        public void Process_GetMetadataWithFallback_BothPInvokeAndCallerPreserved()
        {
            // When a caller has DllNotFoundException fallback, both the P/Invoke declaration
            // AND the caller must be preserved. The P/Invoke will throw DllNotFoundException
            // at runtime, which the caller catches and falls back gracefully.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_TestLib_MyClass_FFF\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata();\n" +
                "\n" +
                "    [LibraryImport(\"TestLib\", EntryPoint = \"$s7TestLib7MyClassVMa\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata_fallback();\n" +
                "\n" +
                "    static TypeMetadata ISwiftObject.GetTypeMetadata()\n" +
                "    {\n" +
                "        try\n" +
                "        {\n" +
                "            return PInvoke_getMetadata();\n" +
                "        }\n" +
                "        catch (System.DllNotFoundException)\n" +
                "        {\n" +
                "            return PInvoke_getMetadata_fallback();\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_TestLib_MyClass_FFF" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // P/Invoke declaration IS preserved (exempted because caller has DllNotFoundException)
            Assert.Contains("PInvoke_getMetadata()", result.Content);
            // Caller IS preserved (has DllNotFoundException fallback)
            Assert.Contains("GetTypeMetadata", result.Content);
            Assert.Contains("PInvoke_getMetadata_fallback", result.Content);
            // No members stripped
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_GetMetadata_OtherPInvokesStillStripped()
        {
            // A GetMetadata exemption should not prevent stripping of other P/Invokes
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_broken\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata();\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_method_broken\")]\n" +
                "    private static partial void PInvoke_method_CCC(IntPtr ptr);\n" +
                "\n" +
                "    static TypeMetadata ISwiftObject.GetTypeMetadata()\n" +
                "    {\n" +
                "        try { return PInvoke_getMetadata(); }\n" +
                "        catch (System.DllNotFoundException) { return default; }\n" +
                "    }\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_method_CCC(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_broken", "SBW_method_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // GetMetadata P/Invoke and caller preserved
            Assert.Contains("PInvoke_getMetadata()", result.Content);
            Assert.Contains("GetTypeMetadata", result.Content);
            // Other P/Invoke and its caller stripped
            Assert.DoesNotContain("PInvoke_method_CCC", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }
    }

    #endregion

    #region F. Edge Cases

    public class ReconcilerEdgeCaseTests
    {
        [Fact]
        public void Process_EmptyStrippedSet_NoChanges()
        {
            var input = "public class Foo { }\n";
            var stripped = new HashSet<string>();
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Equal(input, result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_EmptyContent_NoChanges()
        {
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = StrippedSymbolCSharpReconciler.Process("", stripped);
            Assert.Equal("", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NoMatchingPInvokes_NoChanges()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good_func\")]\n" +
                "    private static partial void PInvoke_good_123(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated_symbol" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Contains("PInvoke_good_123", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NestedPInvokeClass_OnlyPInvokeRemoved()
        {
            var input =
                "public partial struct AcceptsSummable {\n" +
                "    internal static partial class AcceptsSummable_PInvoke {\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken_init\")]\n" +
                "        internal static partial void PInvoke_init_F22(IntPtr resultPtr, IntPtr item);\n" +
                "\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good_get\")]\n" +
                "        internal static partial void PInvoke_item_Get_9EE(IntPtr resultPtr, IntPtr self);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_init" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // Stripped P/Invoke is removed
            Assert.DoesNotContain("PInvoke_init_F22", result.Content);
            // Good P/Invoke is preserved
            Assert.Contains("PInvoke_item_Get_9EE", result.Content);
            // Class structure is preserved
            Assert.Contains("AcceptsSummable_PInvoke", result.Content);
        }

        [Fact]
        public void Process_DocCommentsAndAttributes_IncludedInRemoval()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_method_broken\")]\n" +
                "    private static partial void PInvoke_method_CCC(IntPtr ptr);\n" +
                "\n" +
                "    [global::Swift.UnsupportedSwiftType(\"fallback\", \"any Proto\")]\n" +
                "    /// <summary>\n" +
                "    /// Does the broken thing.\n" +
                "    /// </summary>\n" +
                "    public static string BrokenMethod(int arg)\n" +
                "    {\n" +
                "        PInvoke_method_CCC(ptr);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_method_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.DoesNotContain("Does the broken thing", result.Content);
            Assert.DoesNotContain("UnsupportedSwiftType", result.Content);
        }

        [Fact]
        public void Process_PreservesUnrelatedMembers()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken\")]\n" +
                "    private static partial void PInvoke_broken_111(IntPtr ptr);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good\")]\n" +
                "    private static partial void PInvoke_good_222(IntPtr ptr);\n" +
                "\n" +
                "    public void GoodMethod()\n" +
                "    {\n" +
                "        PInvoke_good_222(ptr);\n" +
                "    }\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_broken_111(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_111", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Contains("PInvoke_good_222", result.Content);
            Assert.Contains("GoodMethod", result.Content);
        }

        [Fact]
        public void Process_ModuleSpecificWrapperLibrary_DetectsCorrectly()
        {
            // Issue #2: Wrapper library name is "{ModuleName}SwiftBindings" in SDK mode.
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"ImagePipelineSwiftBindings\", EntryPoint = \"SBW_broken_func\")]\n" +
                "    private static partial void PInvoke_broken_AAA(IntPtr ptr);\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_broken_AAA(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_func" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_AAA", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_AmbiguousPInvokeName_SkippedEntirely()
        {
            // When the same P/Invoke method name appears in multiple class scopes,
            // file-wide matching would false-match. Skip these entirely.
            var input =
                "public partial class TypeA {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_TypeA_eq_AAA\")]\n" +
                "    private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeA? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_TypeB_eq_BBB\")]\n" +
                "    private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeB? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n";
            // Only TypeA's eq symbol is stripped, but PInvoke_eq is ambiguous
            var stripped = new HashSet<string> { "SBW_TypeA_eq_AAA" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // Both types should be fully preserved (ambiguous name → skip entirely)
            Assert.Contains("TypeA", result.Content);
            Assert.Contains("TypeB", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_PrefixCollision_DoesNotOverStrip()
        {
            // Issue #3: PInvoke_foo_ABC must not match PInvoke_foo_ABC123.
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_short\")]\n" +
                "    private static partial void PInvoke_foo_ABC(IntPtr ptr);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_long\")]\n" +
                "    private static partial void PInvoke_foo_ABC123(IntPtr ptr);\n" +
                "\n" +
                "    public void ShortMethod()\n" +
                "    {\n" +
                "        PInvoke_foo_ABC(ptr);\n" +
                "    }\n" +
                "\n" +
                "    public void LongMethod()\n" +
                "    {\n" +
                "        PInvoke_foo_ABC123(ptr);\n" +
                "    }\n" +
                "}\n";
            // Only strip the short symbol — long should be unaffected
            var stripped = new HashSet<string> { "SBW_short" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // Short P/Invoke and its caller stripped
            Assert.DoesNotContain("PInvoke_foo_ABC(", result.Content);
            Assert.DoesNotContain("ShortMethod", result.Content);
            // Long P/Invoke and its caller preserved (not prefix-matched)
            Assert.Contains("PInvoke_foo_ABC123", result.Content);
            Assert.Contains("LongMethod", result.Content);
        }
    }

    #endregion

    #region G. Helper Method Tests

    public class ReconcilerHelperTests
    {
        [Theory]
        [InlineData("[LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"ImagePipelineSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"DocScanSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s_mangled\")]", false)]
        [InlineData("[LibraryImport(\"OtherLib\", EntryPoint = \"func\")]", false)]
        [InlineData("[LibraryImport(\"SwiftBindings\")]", false)] // no EntryPoint
        public void IsWrapperLibraryImportLine_DetectsCorrectly(string line, bool expected)
        {
            Assert.Equal(expected, StrippedSymbolCSharpReconciler.IsWrapperLibraryImportLine(line));
        }

        [Theory]
        [InlineData("    private static partial void PInvoke_method_ABC(IntPtr ptr);", "PInvoke_method_ABC")]
        [InlineData("    internal static partial TypeMetadata PInvoke_getMetadata();", "PInvoke_getMetadata")]
        [InlineData("    private static partial void SBW_Free(IntPtr ptr);", "SBW_Free")]
        public void ExtractMethodNameFromPartialDecl_ExtractsCorrectly(string line, string expected)
        {
            Assert.Equal(expected, StrippedSymbolCSharpReconciler.ExtractMethodNameFromPartialDecl(line));
        }

        [Theory]
        [InlineData("public static string DoStuff(int arg)", "DoStuff")]
        [InlineData("public MyClass(int value)", "MyClass")]
        [InlineData("private int Value_Get()", "Value_Get")]
        [InlineData("static TypeMetadata ISwiftObject.GetTypeMetadata()", "GetTypeMetadata")]
        [InlineData("public int Count", "Count")]
        [InlineData("public virtual void Execute()", "Execute")]
        public void ExtractMemberName_ExtractsCorrectly(string trimmed, string expected)
        {
            Assert.Equal(expected, StrippedSymbolCSharpReconciler.ExtractMemberName(trimmed));
        }
    }

    #endregion

    #region K. Proxy Emission Gating (ProtocolHandler)

    public class ProxyEmissionGatingTests
    {
        [Fact]
        public void SuppressedProxyClassNames_TrackedInEmissionContext()
        {
            var ctx = new ModuleEmissionContext();
            Assert.Empty(ctx.SuppressedProxyClassNames);

            ctx.RecordSuppressedProxy("FooProxy");
            ctx.RecordSuppressedProxy("BarProxy");

            Assert.Equal(2, ctx.SuppressedProxyClassNames.Count);
            Assert.Contains("FooProxy", ctx.SuppressedProxyClassNames);
            Assert.Contains("BarProxy", ctx.SuppressedProxyClassNames);
        }

        [Fact]
        public void SuppressedProxyClassNames_DeduplicatesEntries()
        {
            var ctx = new ModuleEmissionContext();
            ctx.RecordSuppressedProxy("FooProxy");
            ctx.RecordSuppressedProxy("FooProxy");
            Assert.Single(ctx.SuppressedProxyClassNames);
        }

        [Fact]
        public void WasConformanceEmitted_ControlsProxySuppression()
        {
            var ctx = new ModuleEmissionContext();
            // When no decisions recorded, WasConformanceEmitted returns false
            Assert.False(ctx.WasConformanceEmitted("SomeProtocol"));

            // Record an emitted conformance — proxy should NOT be suppressed
            ctx.RecordConformanceDecision("GoodProtocol", true, null);
            Assert.True(ctx.WasConformanceEmitted("GoodProtocol"));

            // Record a skipped conformance — proxy SHOULD be suppressed
            ctx.RecordConformanceDecision("BadProtocol", false, "class-bound");
            Assert.False(ctx.WasConformanceEmitted("BadProtocol"));
        }
    }

    #endregion

    #region F. Dangling ToString Stripping

    public class ReconcilerDanglingToStringTests
    {
        [Fact]
        public void Process_StrippedDescriptionProperty_AlsoStripsToString()
        {
            // Simulates a real-world pattern: Description property gets stripped because its
            // P/Invoke wrapper was stripped, but ToString() => Description; is left dangling.
            var input =
                "namespace Test {\n" +
                "public partial class BoolBox {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_BoolBox_description\")]\n" +
                "    private static partial IntPtr PInvoke_Get_BoolBox_description_ABC(IntPtr self);\n" +
                "\n" +
                "    private string Description_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_Get_BoolBox_description_ABC(Payload);\n" +
                "        return SwiftString.FromPayload(result);\n" +
                "    }\n" +
                "\n" +
                "    public string Description\n" +
                "    {\n" +
                "        get => Description_Get();\n" +
                "    }\n" +
                "\n" +
                "    public override string ToString() => Description;\n" +
                "\n" +
                "    public int Value { get; set; }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_BoolBox_description" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // P/Invoke, helper, property, AND ToString should all be stripped
            Assert.DoesNotContain("PInvoke_Get_BoolBox_description", result.Content);
            Assert.DoesNotContain("Description_Get", result.Content);
            Assert.DoesNotContain("Description", result.Content);
            Assert.DoesNotContain("ToString", result.Content);

            // Unrelated property should survive
            Assert.Contains("Value", result.Content);
        }

        [Fact]
        public void Process_NonStrippedDescription_PreservesToString()
        {
            // When Description property is NOT stripped, ToString should be preserved
            var input =
                "namespace Test {\n" +
                "public partial class GoodBox {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_GoodBox_description\")]\n" +
                "    private static partial IntPtr PInvoke_Get_GoodBox_description_ABC(IntPtr self);\n" +
                "\n" +
                "    private string Description_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_Get_GoodBox_description_ABC(Payload);\n" +
                "        return SwiftString.FromPayload(result);\n" +
                "    }\n" +
                "\n" +
                "    public string Description\n" +
                "    {\n" +
                "        get => Description_Get();\n" +
                "    }\n" +
                "\n" +
                "    public override string ToString() => Description;\n" +
                "}\n" +
                "}\n";
            // Different symbol stripped — not related to Description
            var stripped = new HashSet<string> { "SBW_unrelated_method" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Description", result.Content);
            Assert.Contains("ToString", result.Content);
        }

        [Fact]
        public void Process_ToStringWithoutDescription_Preserved()
        {
            // ToString that references a non-property member should not be affected
            var input =
                "namespace Test {\n" +
                "public partial class Custom {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken\")]\n" +
                "    private static partial void PInvoke_broken_ABC(IntPtr self);\n" +
                "\n" +
                "    public override string ToString() => \"Custom\";\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // ToString with string literal is preserved (not a property reference)
            Assert.Contains("ToString", result.Content);
        }
    }

    #endregion

    #region F. Orphaned Lazy Accessor Stripping

    public class ReconcilerOrphanedLazyAccessorTests
    {
        [Fact]
        public void Process_StrippedLazyField_AlsoStripsExpressionBodiedProperty()
        {
            // Simulates the lazy-field cascade pattern:
            // PInvoke_CaseByIndex is stripped → _lazy_debugMode field is stripped →
            // DebugMode property referencing _lazy_debugMode.Value must also be stripped.
            var input =
                "namespace Test {\n" +
                "public partial class SkeletonEnvironmentKey {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_CaseByIndex\")]\n" +
                "    private static partial IntPtr PInvoke_CaseByIndex(nint index);\n" +
                "\n" +
                "    private static readonly Lazy<SkeletonEnvironmentKey> _lazy_debugMode = new(() =>\n" +
                "    {\n" +
                "        IntPtr ptr = PInvoke_CaseByIndex(0);\n" +
                "        var result = new SkeletonEnvironmentKey();\n" +
                "        return result;\n" +
                "    });\n" +
                "    /// <summary>\n" +
                "    /// Gets the 'debugMode' case.\n" +
                "    /// </summary>\n" +
                "    /// <remarks>Cached singleton instance.</remarks>\n" +
                "    public static SkeletonEnvironmentKey DebugMode => _lazy_debugMode.Value;\n" +
                "\n" +
                "    private static readonly Lazy<SkeletonEnvironmentKey> _lazy_production = new(() =>\n" +
                "    {\n" +
                "        IntPtr ptr = PInvoke_CaseByIndex(1);\n" +
                "        var result = new SkeletonEnvironmentKey();\n" +
                "        return result;\n" +
                "    });\n" +
                "    /// <summary>\n" +
                "    /// Gets the 'production' case.\n" +
                "    /// </summary>\n" +
                "    public static SkeletonEnvironmentKey Production => _lazy_production.Value;\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_CaseByIndex" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // Both lazy fields and their accessor properties should be stripped
            Assert.DoesNotContain("_lazy_debugMode", result.Content);
            Assert.DoesNotContain("DebugMode", result.Content);
            Assert.DoesNotContain("_lazy_production", result.Content);
            Assert.DoesNotContain("Production", result.Content);
            Assert.DoesNotContain("PInvoke_CaseByIndex", result.Content);
        }

        [Fact]
        public void Process_NonStrippedLazyField_PreservesProperty()
        {
            // A lazy field that does NOT call a stripped P/Invoke should be preserved.
            var input =
                "namespace Test {\n" +
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken\")]\n" +
                "    private static partial IntPtr PInvoke_broken(nint index);\n" +
                "\n" +
                "    private static readonly Lazy<Foo> _lazy_good = new(() =>\n" +
                "    {\n" +
                "        return new Foo();\n" +
                "    });\n" +
                "    public static Foo Good => _lazy_good.Value;\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The lazy field and property are NOT calling the stripped P/Invoke, so preserved
            Assert.Contains("_lazy_good", result.Content);
            Assert.Contains("Good", result.Content);
        }
        [Fact]
        public void Process_TwoEnumsWithSameLazyName_OnlyStripsFromCorrectType()
        {
            // Two enums share _lazy_none field name. Only Enum1's PInvoke is stripped.
            // Enum2's _lazy_none and None property must be preserved.
            // Uses unique PInvoke method names per class to avoid the ambiguity guard.
            var input =
                "namespace Test {\n" +
                "public partial class Enum1 {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Enum1_CaseByIndex\")]\n" +
                "    private static partial IntPtr PInvoke_Enum1CaseByIndex(nint index);\n" +
                "\n" +
                "    private static readonly Lazy<Enum1> _lazy_none = new(() =>\n" +
                "    {\n" +
                "        IntPtr ptr = PInvoke_Enum1CaseByIndex(0);\n" +
                "        return new Enum1();\n" +
                "    });\n" +
                "    public static Enum1 None => _lazy_none.Value;\n" +
                "}\n" +
                "public partial class Enum2 {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Enum2_CaseByIndex\")]\n" +
                "    private static partial IntPtr PInvoke_Enum2CaseByIndex(nint index);\n" +
                "\n" +
                "    private static readonly Lazy<Enum2> _lazy_none = new(() =>\n" +
                "    {\n" +
                "        IntPtr ptr = PInvoke_Enum2CaseByIndex(0);\n" +
                "        return new Enum2();\n" +
                "    });\n" +
                "    public static Enum2 None => _lazy_none.Value;\n" +
                "}\n" +
                "}\n";
            // Only Enum1's wrapper is stripped
            var stripped = new HashSet<string> { "SBW_Enum1_CaseByIndex" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // Enum1's lazy field and property should be stripped
            Assert.DoesNotContain("Enum1 None", result.Content);

            // Enum2's lazy field and property must be preserved (different type scope)
            Assert.Contains("Enum2 None", result.Content);
            Assert.Contains("SBW_Enum2_CaseByIndex", result.Content);
        }
    }

    #endregion

    #region F. CreateSwiftInstance Constructor Stripping

    public class ReconcilerCreateSwiftInstanceTests
    {
        [Fact]
        public void Process_StrippedConstructorHelper_AlsoStripsConstructor()
        {
            // When a CreateSwiftInstance_ helper is stripped (Level 1),
            // the constructor calling it via : base() must also be stripped (Level 2).
            var input =
                "namespace Test {\n" +
                "public partial class Widget : Base {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Widget_init_ABC\")]\n" +
                "    private static partial IntPtr PInvoke_init_ABC(IntPtr arg);\n" +
                "\n" +
                "    private static IntPtr CreateSwiftInstance_PInvoke_init_ABC(int arg)\n" +
                "    {\n" +
                "        return PInvoke_init_ABC(IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public Widget(int arg) : base(CreateSwiftInstance_PInvoke_init_ABC(arg))\n" +
                "    {\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Widget_init_ABC" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("PInvoke_init_ABC", result.Content);
            Assert.DoesNotContain("CreateSwiftInstance_", result.Content);
            Assert.DoesNotContain("public Widget(", result.Content);
        }

        [Fact]
        public void Process_ValidConstructorHelper_Preserved()
        {
            // When the helper's P/Invoke is NOT stripped, everything is preserved.
            var input =
                "namespace Test {\n" +
                "public partial class Widget : Base {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Widget_init_ABC\")]\n" +
                "    private static partial IntPtr PInvoke_init_ABC(IntPtr arg);\n" +
                "\n" +
                "    private static IntPtr CreateSwiftInstance_PInvoke_init_ABC(int arg)\n" +
                "    {\n" +
                "        return PInvoke_init_ABC(IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public Widget(int arg) : base(CreateSwiftInstance_PInvoke_init_ABC(arg))\n" +
                "    {\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated_symbol" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("PInvoke_init_ABC", result.Content);
            Assert.Contains("CreateSwiftInstance_", result.Content);
            Assert.Contains("public Widget(", result.Content);
        }
    }

    #endregion

    #region G. Narrowing Overload Stripping

    public class ReconcilerNarrowingOverloadTests
    {
        [Fact]
        public void Process_SingleLineIndexerNarrowing_StrippedWhenTargetMissing()
        {
            // Single-line indexer: "this[int x] => this[(nint)x];" with no this[nint x]
            var input =
                "namespace Test {\n" +
                "public partial class Store {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_broken\")]\n" +
                "    private static partial IntPtr PInvoke_Sub_Get_ABC(nint idx, IntPtr self);\n" +
                "\n" +
                "    public nuint this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_SubGet_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("this[int index0]", result.Content);
        }

        [Fact]
        public void Process_SingleLineIndexerNarrowing_PreservedWhenTargetExists()
        {
            // Single-line indexer with a valid this[nint x] target — should NOT be stripped
            var input =
                "namespace Test {\n" +
                "public partial class Store {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_ok\")]\n" +
                "    private static partial IntPtr PInvoke_Sub_Get_OK(nint idx, IntPtr self);\n" +
                "\n" +
                "    public nuint this[nint index0] => Subscript_Get(index0);\n" +
                "\n" +
                "    public nuint this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            // Strip an unrelated symbol so the reconciler runs but doesn't touch the indexer
            var stripped = new HashSet<string> { "SBW_unrelated" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("this[int index0]", result.Content);
        }

        [Fact]
        public void Process_MultiLineIndexerNarrowing_StrippedWhenTargetMissing()
        {
            // Multi-line indexer: "this[int x] { get => this[(nint)x]; set => ... }"
            var input =
                "namespace Test {\n" +
                "public partial class BigNum {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_broken\")]\n" +
                "    private static partial bool PInvoke_Sub_Get_ABC(nint bitAt, IntPtr self);\n" +
                "\n" +
                "    public bool this[int bitAt]\n" +
                "    {\n" +
                "        get => this[(nint)bitAt];\n" +
                "        set => this[(nint)bitAt] = value;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_SubGet_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("this[int bitAt]", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowingSingleLine_StrippedWhenTargetMissing()
        {
            // Expression-bodied method: "Encode(int x) => Encode((nint)x);"
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_broken\")]\n" +
                "    private static partial void PInvoke_encode_ABC(nint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(int value) => Encode((nint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_encode_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Encode(int value)", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowingMultiLine_StrippedWhenTargetMissing()
        {
            // Multi-line expression-bodied method (wrapped signature):
            // "static IBox Parse(byte[] with, uint errorContextLength, ...)\n    => Parse(with, (nuint)errorContextLength, ...);"
            var input =
                "namespace Test {\n" +
                "public partial class Parser {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_parse_broken\")]\n" +
                "    private static partial IntPtr PInvoke_parse_ABC(IntPtr data, nuint len, IntPtr self);\n" +
                "\n" +
                "    public static IBox Parse(byte[] with, uint errorContextLength)\n" +
                "        => Parse(with, (nuint)errorContextLength);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_parse_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Parse(byte[] with, uint errorContextLength)", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowing_PreservedWhenTargetExists()
        {
            // Method narrowing with valid target — should NOT be stripped
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_ok\")]\n" +
                "    private static partial void PInvoke_encode_OK(nint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(nint value)\n" +
                "    {\n" +
                "        PInvoke_encode_OK(value, _handle);\n" +
                "    }\n" +
                "\n" +
                "    public void Encode(int value) => Encode((nint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Encode(int value)", result.Content);
            Assert.Contains("Encode(nint value)", result.Content);
        }

        [Fact]
        public void Process_NarrowingUint_StrippedWhenTargetMissing()
        {
            // uint → nuint narrowing
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_broken\")]\n" +
                "    private static partial void PInvoke_encode_ABC(nuint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(uint value) => Encode((nuint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_encode_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Encode(uint value)", result.Content);
        }

        [Fact]
        public void Process_NarrowingWithDifferentArityTarget_StillStripped()
        {
            // Narrowing overload Foo(int x) => Foo((nint)x) where the real target
            // Foo(nint x) is stripped, but a different-arity Foo(string, nint) survives.
            // The narrowing must still be stripped (the surviving overload has wrong arity).
            var input =
                "namespace Test {\n" +
                "public partial class Processor {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_process_broken\")]\n" +
                "    private static partial void PInvoke_process_ABC(nint val, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_process_ok\")]\n" +
                "    private static partial void PInvoke_process_DEF(IntPtr label, nint count, IntPtr self);\n" +
                "\n" +
                "    public void Process(string label, nint count)\n" +
                "    {\n" +
                "        PInvoke_process_DEF(IntPtr.Zero, count, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public void Process(int value) => Process((nint)value);\n" +
                "}\n" +
                "}\n";
            // Only the single-param Process(nint) is stripped; Process(string, nint) survives
            var stripped = new HashSet<string> { "SBW_process_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The narrowing Process(int) must be stripped even though Process(string, nint) exists
            Assert.DoesNotContain("Process(int value)", result.Content);
            // The 2-param overload should survive
            Assert.Contains("Process(string label, nint count)", result.Content);
        }

        [Fact]
        public void Process_IndexerNarrowingWithDifferentArityTarget_StillStripped()
        {
            // Narrowing indexer this[int x] => this[(nint)x] where the real target
            // this[nint x] is stripped, but a different-arity this[string, nint] survives.
            // The narrowing must still be stripped.
            var input =
                "namespace Test {\n" +
                "public partial class Grid {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_sub_get_broken\")]\n" +
                "    private static partial int PInvoke_Sub_Get_ABC(nint idx, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_sub_get_ok\")]\n" +
                "    private static partial int PInvoke_Sub_Get_DEF(IntPtr label, nint idx, IntPtr self);\n" +
                "\n" +
                "    public int this[string label, nint index]\n" +
                "    {\n" +
                "        get => PInvoke_Sub_Get_DEF(IntPtr.Zero, index, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_sub_get_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("this[int index0]", result.Content);
            Assert.Contains("this[string label, nint index]", result.Content);
        }
    }

    #endregion

    #region I. Throwing-Closure Facade Stripping

    public class ReconcilerThrowingClosureFacadeTests
    {
        [Fact]
        public void Process_FacadeWithStrippedBase_IsRemoved()
        {
            // Base overload calls a stripped P/Invoke -> Step B strips the base.
            // ThrowingClosureSimplificationEmitter's facade forwards by method name and would
            // survive without Step G, leaving a dangling self-call.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_ABC(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_ABC(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            Assert.DoesNotContain("Func<SwiftResult<int, SwiftError>> callback", result.Content);
        }

        [Fact]
        public void Process_FacadeWithLiveBase_IsPreserved()
        {
            // Base is not stripped -> facade's self-call resolves, facade must survive.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_OK(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_OK(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Func<int> callback", result.Content);
            Assert.Contains("_wrapped_callback", result.Content);
            Assert.Contains("Func<SwiftResult<int, SwiftError>> callback", result.Content);
        }

        [Fact]
        public void Process_FacadeInDifferentTypeScope_NotAffectedByOtherTypeStrip()
        {
            // TypeA.Run has a stripped base (facade should go) while TypeB.Run has a live
            // base (facade should stay). Scope-aware grouping by (containingType, memberName)
            // must keep the two groups isolated.
            var input =
                "namespace Test {\n" +
                "public partial class TypeA {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_A_run_broken\")]\n" +
                "    private static partial int PInvoke_A_run(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_A_run(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_B_run_ok\")]\n" +
                "    private static partial int PInvoke_B_run(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_B_run(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_A_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // TypeA: both base and facade gone.
            Assert.DoesNotContain("PInvoke_A_run", result.Content);
            // TypeB: base and facade both survive.
            Assert.Contains("PInvoke_B_run", result.Content);
            // The surviving facade in TypeB keeps its wrapper setup line.
            Assert.Contains("_wrapped_callback", result.Content);
        }

        [Fact]
        public void Process_FacadeWithOnlyUnrelatedOverloadSurviving_IsRemoved()
        {
            // The throwing-closure base is stripped; the only surviving same-name overload
            // is Run(string) which cannot bind _wrapped_callback. The facade must still be
            // stripped — unrelated overloads are not valid call targets.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr label, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(string label)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // The unrelated Run(string) overload must survive.
            Assert.Contains("Run(string label)", result.Content);
        }

        [Fact]
        public void Process_FacadeWithDifferentArityLiveBase_IsRemoved()
        {
            // Two SwiftResult-typed base overloads exist; the arity-1 base (which matches
            // the facade's single-arg self-call Run(_wrapped_callback)) is stripped. The
            // surviving arity-2 base would cause CS1501 if the facade were kept, so the
            // facade must still be stripped — the live base has the wrong arity.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr callback, int extra, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback, int extra)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, extra, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The arity-1 facade must be removed even though an arity-2 SwiftResult base
            // survives — different arity cannot bind the facade's self-call.
            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // The surviving arity-2 base must not be touched.
            Assert.Contains("int extra", result.Content);
        }

        [Fact]
        public void Process_FacadeWithMismatchedDelegateTypeLiveBase_IsRemoved()
        {
            // Two SwiftResult-typed arity-1 overloads exist — same shape, different closure
            // element type. The facade's _wrapped_callback is Func<SwiftResult<int, SwiftError>>,
            // and the ONLY surviving base takes Func<SwiftResult<long, SwiftError>>. The facade
            // would emit CS1503 (cannot convert Func<SwiftResult<int>> to Func<SwiftResult<long>>)
            // if kept, so Step G must strip it — arity alone is not enough, the delegate type
            // must bind.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<long, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The facade's wrapper type Func<SwiftResult<int, SwiftError>> doesn't bind to the
            // surviving Func<SwiftResult<long, SwiftError>> base — facade must be stripped.
            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // The surviving long base must not be touched.
            Assert.Contains("Func<SwiftResult<long, SwiftError>> callback", result.Content);
        }

        [Fact]
        public void Process_FacadeWithMatchingDelegateTypeLiveBase_IsPreserved()
        {
            // Mirror of the mismatch case: the stripped base is the long variant; the surviving
            // Func<SwiftResult<int, SwiftError>> base matches the facade's wrapper type exactly,
            // so the self-call binds and the facade must be preserved.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<long, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(_wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Func<int> callback", result.Content);
            Assert.Contains("_wrapped_callback", result.Content);
            Assert.Contains("Func<SwiftResult<int, SwiftError>> callback", result.Content);
            // The stripped long base must be gone.
            Assert.DoesNotContain("Func<SwiftResult<long, SwiftError>> callback", result.Content);
        }

        [Fact]
        public void Process_FacadeWithDuplicateWrapperTypes_RequiresPositionalMatch()
        {
            // Regression: a facade passes TWO _wrapped_* variables of the same delegate type.
            // The original throwing base (both closure params) is stripped. A same-name,
            // same-arity overload survives with only ONE closure param at the first ordinal
            // and an unrelated string at the second. Naive "all wrapper types appear somewhere
            // in the declaration" matching passes because the single unique type is present,
            // but the facade self-call cannot bind: the second _wrapped_* would coerce to
            // string and emit CS1503. Positional matching must catch this and strip the facade.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr a, IntPtr b, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr a, IntPtr label, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> first, Func<SwiftResult<int, SwiftError>> second)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> first, string label)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> first, Func<int> second)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_first = () => SwiftResult<int, SwiftError>.FromSuccess(first());\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_second = () => SwiftResult<int, SwiftError>.FromSuccess(second());\n" +
                "        var _result = Run(_wrapped_first, _wrapped_second);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The facade must be stripped: position 2 of the surviving base is `string`,
            // which cannot bind _wrapped_second (Func<SwiftResult<int, SwiftError>>).
            Assert.DoesNotContain("Func<int> first", result.Content);
            Assert.DoesNotContain("_wrapped_first", result.Content);
            Assert.DoesNotContain("_wrapped_second", result.Content);
            // The unrelated Run(Func<...>, string) overload must still be present.
            Assert.Contains("string label", result.Content);
        }

        [Fact]
        public void Process_FacadeWithDuplicateWrapperTypes_MatchingBasePreserved()
        {
            // Mirror of the positional-mismatch case: the stripped base has a string second
            // param, while the surviving base has matching Func<SwiftResult<int, SwiftError>>
            // at BOTH ordinals. The facade's self-call binds cleanly, so it must be preserved.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr a, IntPtr label, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr a, IntPtr b, IntPtr self);\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> first, string label)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<SwiftResult<int, SwiftError>> first, Func<SwiftResult<int, SwiftError>> second)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(Func<int> first, Func<int> second)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_first = () => SwiftResult<int, SwiftError>.FromSuccess(first());\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_second = () => SwiftResult<int, SwiftError>.FromSuccess(second());\n" +
                "        var _result = Run(_wrapped_first, _wrapped_second);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Func<int> first", result.Content);
            Assert.Contains("_wrapped_first", result.Content);
            Assert.Contains("_wrapped_second", result.Content);
            // The stripped Run(Func<...>, string) overload must be gone.
            Assert.DoesNotContain("string label", result.Content);
        }

        [Fact]
        public void Process_FacadeWithMismatchedPassThroughParam_IsRemoved()
        {
            // Regression: the facade self-call passes a non-wrapped pass-through argument
            // (`count`) alongside a wrapped closure. The true throwing base
            // `Run(int count, Func<SwiftResult<int, SwiftError>> callback)` is stripped; a
            // same-name/same-arity `SwiftResult<...>` sibling survives but its first param is
            // `string label` instead of `int`. Round-4 matching only checks wrapped args
            // positionally, so without pass-through validation the facade is incorrectly
            // preserved and emits CS1503 (`int` → `string`). Positional pass-through
            // matching must catch this and strip the facade.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(int count, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr label, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(int count, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(count, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(string label, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(int count, Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(count, _wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The facade must be stripped: the surviving base's first param is `string`,
            // which cannot bind the facade's `int count` pass-through.
            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // The unrelated Run(string, ...) overload survives.
            Assert.Contains("string label", result.Content);
        }

        [Fact]
        public void Process_FacadeWithMatchingPassThroughParam_IsPreserved()
        {
            // Mirror of the pass-through mismatch: the stripped base is the string variant,
            // the surviving base matches the facade on both the pass-through `int count`
            // and the wrapped closure type. The facade self-call binds, so it must survive.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr label, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(int count, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(string label, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(int count, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(count, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(int count, Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(count, _wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.Contains("Func<int> callback", result.Content);
            Assert.Contains("_wrapped_callback", result.Content);
            // The stripped string-variant base must be gone.
            Assert.DoesNotContain("string label", result.Content);
        }

        [Fact]
        public void Process_FacadeWithTypePrefixCollision_IsRemoved()
        {
            // Regression: the facade pass-through parameter type is `URL` and a surviving
            // sibling overload declares `URLRequest`. A naive Contains() check would accept
            // the sibling because `URLRequest` contains `URL`, but the facade self-call
            // cannot actually bind (no implicit URL → URLRequest conversion). Exact-type
            // comparison on pass-through parameters must catch this and strip the facade.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(IntPtr value, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr value, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(URL value, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(URLRequest value, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(URL value, Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(value, _wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // URLRequest sibling survives.
            Assert.Contains("URLRequest value", result.Content);
        }

        [Fact]
        public void Process_FacadeWithVerbatimIdentifier_IsRemoved()
        {
            // Regression: the facade's pass-through parameter is `int @event` (C# verbatim
            // for reserved keyword). Without @-normalization the parameter-name lookup
            // misses, the pass-through is accepted permissively, and a mismatched surviving
            // base can keep the facade alive. After normalization the type-mismatch check
            // strips the facade correctly.
            var input =
                "namespace Test {\n" +
                "public partial class Runner {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_broken\")]\n" +
                "    private static partial int PInvoke_run_A(int e, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_run_ok\")]\n" +
                "    private static partial int PInvoke_run_B(IntPtr e, IntPtr callback, IntPtr self);\n" +
                "\n" +
                "    public int Run(int @event, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_A(@event, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(string @event, Func<SwiftResult<int, SwiftError>> callback)\n" +
                "    {\n" +
                "        return PInvoke_run_B(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int Run(int @event, Func<int> callback)\n" +
                "    {\n" +
                "        Func<SwiftResult<int, SwiftError>> _wrapped_callback = () => SwiftResult<int, SwiftError>.FromSuccess(callback());\n" +
                "        var _result = Run(@event, _wrapped_callback);\n" +
                "        if (_result.IsFailure) throw new Swift.SwiftErrorException(_result.Failure);\n" +
                "        return _result.Success;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_run_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("Func<int> callback", result.Content);
            Assert.DoesNotContain("_wrapped_callback", result.Content);
            // The string-variant sibling survives.
            Assert.Contains("string @event", result.Content);
        }
    }

    #endregion

    #region M. ProcessDirectory write gate

    public class ReconcilerProcessDirectoryTests
    {
        [Fact]
        public void ProcessDirectory_TrampolineOnlyChange_StillWritesFile()
        {
            // Regression: the write gate must be content equality, not identity count.
            // A file whose only change is an isolated stripped trampoline (no public
            // caller, so zero public-API identities) still has different content and
            // must be persisted — otherwise the on-disk file references a wrapper
            // symbol that no longer exists, and the next compile fails with DllNotFound.
            using var temp = new TempReconcilerDir();
            var filePath = Path.Combine(temp.Path, "Foo.cs");
            File.WriteAllText(filePath,
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_orphan_trampoline\")]\n" +
                "    private static partial void PInvoke_orphan(IntPtr ptr);\n" +
                "}\n");
            var stripped = new HashSet<string> { "SBW_orphan_trampoline" };

            var aggregate = StrippedSymbolCSharpReconciler.ProcessDirectory(temp.Path, stripped);

            Assert.Empty(aggregate);
            var written = File.ReadAllText(filePath);
            Assert.DoesNotContain("PInvoke_orphan", written);
            Assert.DoesNotContain("SBW_orphan_trampoline", written);
        }

        private sealed class TempReconcilerDir : IDisposable
        {
            public string Path { get; }
            public TempReconcilerDir()
            {
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(Path);
            }
            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); }
                catch { /* test cleanup */ }
            }
        }
    }

    #endregion

    #region N. Orphaned Closure-Callback Field Stripping (Step B2)

    public class ReconcilerOrphanedCallbackFieldTests
    {
        // Models the post-Step-A/B state for an optional throwing-Void closure: an error-mint
        // P/Invoke targeting a stripped wrapper symbol, the [UnmanagedCallersOnly] callback whose
        // catch block calls it, the one-line function-pointer field "s_<cb> = &<cb>;", and the
        // survivor(s) that read the field. The whole chain must strip symmetrically — the field
        // and its readers are NOT block members of the stripped callback, so before Step B2 they
        // dangled and produced CS0103.

        [Fact]
        public void Process_OrphanedCallbackField_StripsFieldReaderAndForwarder()
        {
            // Full chain: stripped P/Invoke -> stripped callback -> orphaned field ->
            // setter-helper reader (Step B2) -> public property forwarder (Step C).
            var input =
                "namespace Test {\n" +
                "public partial class Holder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_CreateError_Module\")]\n" +
                "    private static partial IntPtr PInvoke_SBW_CreateError(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_modifier_Set_Callback = &modifier_Set_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void modifier_Set_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { }\n" +
                "        catch (System.Exception _ex)\n" +
                "        {\n" +
                "            *_error = PInvoke_SBW_CreateError(IntPtr.Zero);\n" +
                "        }\n" +
                "    }\n" +
                "\n" +
                "    private static void Modifier_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_modifier_Set_Callback, value);\n" +
                "        PInvoke_set_modifier(handle, _data);\n" +
                "    }\n" +
                "\n" +
                "    public SwiftClosure Modifier\n" +
                "    {\n" +
                "        set { Modifier_Set(_handle, value); }\n" +
                "    }\n" +
                "\n" +
                "    public int KeepMe => 42;\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_CreateError_Module" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // The stripped P/Invoke, the callback, the orphaned field, the reader, and the
            // public forwarder must all be gone.
            Assert.DoesNotContain("PInvoke_SBW_CreateError", result.Content);
            Assert.DoesNotContain("modifier_Set_Callback", result.Content); // field + callback
            Assert.DoesNotContain("Modifier_Set", result.Content);
            Assert.DoesNotContain("public SwiftClosure Modifier", result.Content);
            // Unrelated member in the same type survives — Step B2 is targeted, not a nuke.
            Assert.Contains("public int KeepMe", result.Content);
        }

        [Fact]
        public void Process_OrphanedCallbackField_PublicMethodReader_Stripped()
        {
            // Orphaned-field shape: the orphaned field is read directly by a public method (no
            // property forwarder). The method's body can't compile against a missing field,
            // so the binding is dropped rather than emitted as non-compiling code.
            var input =
                "namespace Test {\n" +
                "public partial class Functions {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_CreateError_Module\")]\n" +
                "    private static partial IntPtr PInvoke_SBW_CreateError(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_upload_modifier_Callback = &upload_modifier_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void upload_modifier_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { *_error = PInvoke_SBW_CreateError(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    public static bool Upload(int timeout, IntPtr modifier)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_upload_modifier_Callback, modifier);\n" +
                "        return PInvoke_upload(timeout, _data);\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_CreateError_Module" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("upload_modifier_Callback", result.Content); // field + callback
            Assert.DoesNotContain("public static bool Upload(", result.Content);
        }

        [Fact]
        public void Process_LiveCallbackField_PreservedWhenUnrelatedSymbolStripped()
        {
            // The error-mint P/Invoke is NOT stripped, so the callback survives and the field's
            // address-of target is alive. Stripping an unrelated symbol must leave the closure
            // field, callback, and reader fully intact (no over-strip).
            var input =
                "namespace Test {\n" +
                "public partial class Holder {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_other\")]\n" +
                "    private static partial void PInvoke_other(IntPtr p);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_CreateError_Module\")]\n" +
                "    private static partial IntPtr PInvoke_SBW_CreateError(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_modifier_Set_Callback = &modifier_Set_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void modifier_Set_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { *_error = PInvoke_SBW_CreateError(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    private static void Modifier_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_modifier_Set_Callback, value);\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_other" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            Assert.DoesNotContain("PInvoke_other", result.Content);
            Assert.Contains("s_modifier_Set_Callback = &modifier_Set_Callback", result.Content);
            Assert.Contains("private static void modifier_Set_Callback(", result.Content);
            Assert.Contains("private static void Modifier_Set(", result.Content);
            Assert.Contains("PInvoke_SBW_CreateError", result.Content);
        }

        [Fact]
        public void Process_OrphanedField_WordBoundary_DoesNotStripSimilarlyNamedReader()
        {
            // A reader of a DIFFERENT live field whose name has the orphaned field name as a
            // prefix ("s_a_Callback2" vs orphaned "s_a_Callback") must survive — the field-read
            // match is whole-identifier, not substring.
            var input =
                "namespace Test {\n" +
                "public partial class Holder {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_dead\")]\n" +
                "    private static partial IntPtr PInvoke_dead(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_a_Callback = &a_Callback;\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_a_Callback2 = &liveHelper;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void a_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { var _e = PInvoke_dead(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    private static void liveHelper(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "    }\n" +
                "\n" +
                "    public static IntPtr ReadLive(IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_a_Callback2, value);\n" +
                "        return (IntPtr)s_a_Callback2;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_dead" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // Orphaned field + its callback gone.
            Assert.DoesNotContain("s_a_Callback ", result.Content);
            Assert.DoesNotContain("&a_Callback;", result.Content);
            // The prefix-colliding live field and its reader survive.
            Assert.Contains("s_a_Callback2 = &liveHelper", result.Content);
            Assert.Contains("public static IntPtr ReadLive(IntPtr value)", result.Content);
        }

        [Fact]
        public void Process_TwoCallbackFields_OnlyOrphanedChainStripped()
        {
            // Two closure fields in one type: one whose callback calls a STRIPPED helper, one
            // whose callback calls a KEPT helper. Per-field address-of-target gating must strip
            // only the dead chain and preserve the live one.
            var input =
                "namespace Test {\n" +
                "public partial class Holder {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_CreateError_Module\")]\n" +
                "    private static partial IntPtr PInvoke_SBW_CreateError(IntPtr msg);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_dead_helper\")]\n" +
                "    private static partial IntPtr PInvoke_dead(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_dead_Callback = &dead_Callback;\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_live_Callback = &live_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void dead_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { var _e = PInvoke_dead(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void live_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { var _e = PInvoke_SBW_CreateError(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    private static void Dead_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_dead_Callback, value);\n" +
                "    }\n" +
                "\n" +
                "    private static void Live_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_live_Callback, value);\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_dead_helper" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // Dead chain fully stripped.
            Assert.DoesNotContain("PInvoke_dead", result.Content);
            Assert.DoesNotContain("dead_Callback", result.Content); // field + callback
            Assert.DoesNotContain("Dead_Set", result.Content);
            // Live chain fully preserved.
            Assert.Contains("s_live_Callback = &live_Callback", result.Content);
            Assert.Contains("private static void live_Callback(", result.Content);
            Assert.Contains("private static void Live_Set(", result.Content);
            Assert.Contains("PInvoke_SBW_CreateError", result.Content);
        }

        [Fact]
        public void Process_CrossTypeSameFieldName_DoesNotStripSiblingTypesLiveField()
        {
            // Two sibling types each contain an identically-named callback + field
            // (a synthesized-name hash collision). Only TypeA's callback is orphaned (its body
            // calls the stripped PInvoke_dead); TypeB's callback calls a live helper. The field
            // gate and the reader strip must both be type-scoped so TypeB's live field, callback,
            // and reader survive — scope leakage here is the class of bug every other reconciler
            // step explicitly avoids.
            var input =
                "namespace Test {\n" +
                "public partial class TypeA {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_dead\")]\n" +
                "    private static partial IntPtr PInvoke_dead(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_same_Callback = &same_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void same_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { var _e = PInvoke_dead(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    private static void Same_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_same_Callback, value);\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_live\")]\n" +
                "    private static partial IntPtr PInvoke_live(IntPtr msg);\n" +
                "\n" +
                "    private static unsafe readonly delegate* unmanaged[Cdecl]<void*, SwiftError*, IntPtr, void> s_same_Callback = &same_Callback;\n" +
                "\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void same_Callback(void* _arg0, SwiftError* _error, IntPtr _context)\n" +
                "    {\n" +
                "        try { } catch { var _e = PInvoke_live(IntPtr.Zero); }\n" +
                "    }\n" +
                "\n" +
                "    private static void Same_Set(IntPtr handle, IntPtr value)\n" +
                "    {\n" +
                "        var _data = new SwiftClosureData((IntPtr)s_same_Callback, value);\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_dead" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);

            // TypeA chain is gone; TypeB's identically-named chain survives intact.
            Assert.DoesNotContain("PInvoke_dead", result.Content);
            Assert.Contains("PInvoke_live", result.Content);
            // TypeB still holds its field, callback, and reader (one of each must remain).
            Assert.Contains("s_same_Callback = &same_Callback", result.Content);
            Assert.Contains("private static void Same_Set(", result.Content);
            // Exactly one of the two identical chains was stripped: the field/reader survive once.
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Content, @"s_same_Callback = &same_Callback"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(result.Content, @"private static void Same_Set\("));
        }
    }

    #endregion

    #region N. DllImport + static-extern shape

    // The reconciler historically only recognized the [LibraryImport]+partial P/Invoke
    // shape. Four emitters (AppEntityKeyPathSingletonEmitter, KeyPathBagValueSpecializationEmitter,
    // KeyPathSingletonEmitter, ModuleHandler) emit the older [DllImport]+`static extern`
    // shape against the same wrapper library (AsyncLibraryName / "libSwiftBindings").
    // A stripped wrapper symbol behind that shape must be co-gated identically, or the
    // generated binding keeps a dangling P/Invoke and throws DllNotFoundException-class
    // dispatch failures at runtime.
    public class ReconcilerDllImportShapeTests
    {
        [Fact]
        public void Process_DllImportStaticExtern_StrippedSymbol_RemovesDeclaration()
        {
            // KeyPath/AppEntity singleton shape: EntryPoint before CallingConvention,
            // wrapper lib "libSwiftBindings", body is `private static extern`.
            var input =
                "public partial class Foo {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_singleton_broken\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern IntPtr PInvoke_singleton_ABC();\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_singleton_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_singleton_ABC", result.Content);
            Assert.DoesNotContain("SBW_singleton_broken", result.Content);
        }

        [Fact]
        public void Process_DllImportEntryPointAfterCallingConvention_RemovesDeclaration()
        {
            // ModuleHandler enum-metadata shape: CallingConvention before EntryPoint,
            // module-qualified wrapper lib "{Module}SwiftBindings".
            var input =
                "public partial class Foo {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"MyLibSwiftBindings\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl, EntryPoint = \"SBW_GetEnumMetadata_broken\")]\n" +
                "    private static extern IntPtr __GetEnumMetadata_Bar();\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetEnumMetadata_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("__GetEnumMetadata_Bar", result.Content);
            Assert.DoesNotContain("SBW_GetEnumMetadata_broken", result.Content);
        }

        [Fact]
        public void Process_DllImportStaticExtern_WithPublicCaller_RemovesCallerTransitively()
        {
            // Maximum-case: a public accessor forwards to the stripped extern P/Invoke.
            // Both must vanish, and the public surface that disappeared is recorded.
            var input =
                "public partial class MyClass {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_doStuff_broken\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern void PInvoke_doStuff_DEF(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public virtual string DoStuff(int arg)\n" +
                "    {\n" +
                "        PInvoke_doStuff_DEF(resultPtr, arg);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doStuff_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_doStuff_DEF", result.Content);
            Assert.DoesNotContain("DoStuff", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_DllImportNativeLib_NotAffected()
        {
            // A DllImport against the native source library (not the wrapper) must survive
            // even when its mangled symbol happens to be in the stripped set.
            var input =
                "public partial class Foo {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s20SwiftBindingsTestLib_mangled\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern int PInvoke_native_123(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "$s20SwiftBindingsTestLib_mangled" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Contains("PInvoke_native_123", result.Content);
        }

        [Fact]
        public void Process_DllImportStaticExtern_AmbiguousAcrossTypes_SkippedEntirely()
        {
            // Same extern P/Invoke name in two type scopes; only TypeA's symbol is stripped.
            // The ambiguity guard (broadened to static-extern decls) must recognize the
            // collision and skip file-wide caller stripping so TypeB survives intact.
            var input =
                "public partial class TypeA {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_TypeA_eq_AAA\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeA? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_TypeB_eq_BBB\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeB? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_TypeA_eq_AAA" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.Contains("TypeA", result.Content);
            Assert.Contains("TypeB", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_DllImportAndLibraryImportMixed_BothShapesStripped()
        {
            // A file can carry both shapes; a stripped symbol behind either must be removed
            // while the other shape's unrelated, live P/Invoke is preserved.
            var input =
                "public partial class Foo {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_dll_broken\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern IntPtr PInvoke_dll_X();\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_lib_live\")]\n" +
                "    private static partial IntPtr PInvoke_lib_Y();\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_dll_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_dll_X", result.Content);
            Assert.Contains("PInvoke_lib_Y", result.Content);
        }

        [Fact]
        public void Process_PartialKeywordIdentifierInCaller_DoesNotSuppressStrip()
        {
            // 'partial' is a *contextual* keyword and a legal identifier. A caller body line that
            // declares a local named 'partial' AND calls the stripped P/Invoke would, under a loose
            // " partial " + ";" decl check, be miscounted as a SECOND declaration of that P/Invoke —
            // flipping its name to "ambiguous" and SUPPRESSING the strip file-wide, so the dead
            // wrapper symbol's P/Invoke and its caller survive (CS0103 / DllNotFound). The P/Invoke
            // signature check requires the static-method shape, so the body line is not a decl and
            // the single real declaration is stripped cleanly.
            var input =
                "public partial class Foo {\n" +
                "    [System.Runtime.InteropServices.DllImport(\"libSwiftBindings\", EntryPoint = \"SBW_singleton_broken\", CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]\n" +
                "    private static extern IntPtr PInvoke_singleton_X();\n" +
                "\n" +
                "    public bool IsReady()\n" +
                "    {\n" +
                "        bool partial = PInvoke_singleton_X() != IntPtr.Zero;\n" +
                "        return partial;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_singleton_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            // Strip proceeds: the dead P/Invoke and its transitive caller are both removed.
            Assert.DoesNotContain("PInvoke_singleton_X", result.Content);
            Assert.DoesNotContain("SBW_singleton_broken", result.Content);
            Assert.DoesNotContain("IsReady", result.Content);
        }

        [Fact]
        public void Process_InternalStaticUnsafePartial_StrippedSymbol_RemovesDeclaration()
        {
            // Modifier-order coverage: the generator also emits wrapper P/Invokes as
            // `internal static unsafe partial` (e.g. async/generic-parent cdecl thunks). The
            // tightened signature predicate (which now also requires " static " and "(") must
            // still recognize that shape — the extra `unsafe` token sits between static and
            // partial, so a contiguous `static partial` match would wrongly miss it.
            var input =
                "internal static unsafe partial class PInvoke {\n" +
                "    [LibraryImport(\"MyLibSwiftBindings\", EntryPoint = \"SBW_async_thunk_broken\")]\n" +
                "    internal static unsafe partial void PInvoke_asyncThunk_X(void* ctx);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_async_thunk_broken" };
            var result = StrippedSymbolCSharpReconciler.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_asyncThunk_X", result.Content);
            Assert.DoesNotContain("SBW_async_thunk_broken", result.Content);
        }
    }

    #endregion

}
