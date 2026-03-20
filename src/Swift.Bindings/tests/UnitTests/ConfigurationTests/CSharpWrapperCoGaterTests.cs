// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. P/Invoke Detection

    public class CoGaterPInvokeDetectionTests
    {
        [Fact]
        public void Process_StrippedPInvoke_RemovesPInvokeDeclaration()
        {
            var input =
                "namespace Test {\n" +
                "public partial class Foo {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken_method\")]\n" +
                "    private static partial void PInvoke_broken_ABC123(IntPtr ptr);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_method" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_ABC123", result.Content);
            Assert.DoesNotContain("SBW_broken_method", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_FullyQualifiedAttribute_DetectsCorrectly()
        {
            var input =
                "public partial class Foo {\n" +
                "    [global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_eq_broken\")]\n" +
                "    internal static partial int PInvoke_eq_DEADBEEF(IntPtr a, IntPtr b);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_eq_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_eq_DEADBEEF", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_getMetadata", result.Content);
        }
    }

    #endregion

    #region B. Constructor Stripping (Level 1)

    public class CoGaterConstructorStrippingTests
    {
        [Fact]
        public void Process_ConstructorCallingStrippedPInvoke_Removed()
        {
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_init_ABC123", result.Content);
            Assert.DoesNotContain("public MyClass(int value)", result.Content);
        }
    }

    #endregion

    #region C. Method Stripping (Level 1)

    public class CoGaterMethodStrippingTests
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_create_111", result.Content);
            Assert.DoesNotContain("public MyClass(int a, int b)", result.Content);
            Assert.DoesNotContain("public MyClass(int a)", result.Content);
        }
    }

    #endregion

    #region D. Property Stripping (Level 1 + Level 2 Transitivity)

    public class CoGaterPropertyStrippingTests
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_name_Set_BBB", result.Content);
            Assert.DoesNotContain("Name_Set", result.Content);
            // The property references Name_Set, so it's removed entirely
            Assert.DoesNotContain("public string Name", result.Content);
        }
    }

    #endregion

    #region E. GetMetadata Fallback Exemption

    public class CoGaterGetMetadataExemptionTests
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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

    public class CoGaterEdgeCaseTests
    {
        [Fact]
        public void Process_EmptyStrippedSet_NoChanges()
        {
            var input = "public class Foo { }\n";
            var stripped = new HashSet<string>();
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.Equal(input, result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_EmptyContent_NoChanges()
        {
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = CSharpWrapperCoGater.Process("", stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
                "    [LibraryImport(\"NukeSwiftBindings\", EntryPoint = \"SBW_broken_func\")]\n" +
                "    private static partial void PInvoke_broken_AAA(IntPtr ptr);\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_broken_AAA(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_func" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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
            var result = CSharpWrapperCoGater.Process(input, stripped);
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

    public class CoGaterHelperTests
    {
        [Theory]
        [InlineData("[LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"NukeSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"BlinkIDSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s_mangled\")]", false)]
        [InlineData("[LibraryImport(\"OtherLib\", EntryPoint = \"func\")]", false)]
        [InlineData("[LibraryImport(\"SwiftBindings\")]", false)] // no EntryPoint
        public void IsWrapperLibraryImportLine_DetectsCorrectly(string line, bool expected)
        {
            Assert.Equal(expected, CSharpWrapperCoGater.IsWrapperLibraryImportLine(line));
        }

        [Theory]
        [InlineData("    private static partial void PInvoke_method_ABC(IntPtr ptr);", "PInvoke_method_ABC")]
        [InlineData("    internal static partial TypeMetadata PInvoke_getMetadata();", "PInvoke_getMetadata")]
        [InlineData("    private static partial void SBW_Free(IntPtr ptr);", "SBW_Free")]
        public void ExtractMethodNameFromPartialDecl_ExtractsCorrectly(string line, string expected)
        {
            Assert.Equal(expected, CSharpWrapperCoGater.ExtractMethodNameFromPartialDecl(line));
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
            Assert.Equal(expected, CSharpWrapperCoGater.ExtractMemberName(trimmed));
        }
    }

    #endregion
}
