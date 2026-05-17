// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using RuntimeTestsApp.Infrastructure;
using SwiftBindingsTestLib;

namespace RuntimeTestsApp.Generics;

/// <summary>
/// Runtime tests for the WeatherKit-shape closed-static-factory accessor:
/// a static property on a PAT-constrained generic struct whose return type is
/// a fully closed bound generic of the same nominal type. The wrapper hard-codes
/// the concrete instantiation, so the parent's open <c>T</c> is irrelevant at
/// the call boundary — calling <c>Q&lt;X&gt;.PresetA</c> for any <c>X</c> must
/// return the declared closed type (here, <c>Q&lt;StatPayloadA&gt;</c>).
/// </summary>
public class PatBoundedStaticFactoryTests : TestBase
{
    public PatBoundedStaticFactoryTests(TestResults results) : base(results) { }

    public void TestPresetA_ReturnsClosedInstantiation()
    {
        using var preset = PatBoundedStatsQuery<StatPayloadA>.PresetA;
        AssertNotNull(preset, "PresetA returns non-null");
    }

    public void TestPresetB_ReturnsClosedInstantiation()
    {
        using var preset = PatBoundedStatsQuery<StatPayloadB>.PresetB;
        AssertNotNull(preset, "PresetB returns non-null");
    }

    public void TestPresetA_OpenTypeArgIsIgnored()
    {
        // Calling PresetA via PatBoundedStatsQuery<StatPayloadB> must still
        // return the closed StatPayloadA instantiation: the wrapper hard-codes
        // T' from the declared return type, never from the receiver's T.
        using var preset = PatBoundedStatsQuery<StatPayloadB>.PresetA;
        AssertNotNull(preset, "PresetA via <StatPayloadB> receiver returns non-null");
    }

    public void TestPresetB_OpenTypeArgIsIgnored()
    {
        using var preset = PatBoundedStatsQuery<StatPayloadA>.PresetB;
        AssertNotNull(preset, "PresetB via <StatPayloadA> receiver returns non-null");
    }

    public void TestPresetA_RepeatedCallsAreIndependent()
    {
        // Each call must produce a fresh, independently-disposable instance —
        // exercises the alloc-into-resultPtr path on every call.
        using var p1 = PatBoundedStatsQuery<StatPayloadA>.PresetA;
        using var p2 = PatBoundedStatsQuery<StatPayloadA>.PresetA;
        AssertNotNull(p1, "first PresetA returns non-null");
        AssertNotNull(p2, "second PresetA returns non-null");
        AssertTrue(p1.Payload.DangerousGetHandle() != p2.Payload.DangerousGetHandle(),
            "PresetA returns independent payloads on each call");
    }

    // NativeAOT trims P/Invoke methods that are only reached via direct call —
    // their reflection metadata (parameters, attributes) disappears with them.
    // Root only the specific cdecl wrapper this test reflects over; rooting
    // the helper's other methods would preserve sibling CallConvSwift P/Invokes
    // that TrimmerRoots.xml deliberately avoids (ILC bus error risk).
    // If the Swift signature ever shifts the hash suffix, the assertion below
    // will fail loudly with the existing "PInvoke_presetA_Get_* exists" message.
    [DynamicDependency("PInvoke_presetA_Get_89055DEA(System.IntPtr)", typeof(PatBoundedStatsQuery_PInvoke))]
    public void TestPresetA_PInvokeSignature_TakesOnlyResultPtr()
    {
        // Pins the ABI contract: the @_cdecl wrapper for the closed-static
        // factory takes exactly one IntPtr (resultPtr) — no parent metadata,
        // no PWTs. A regression that re-threaded TMetadata.Handle or a
        // ProtocolWitnessTable handle would add parameters here and a wrapper
        // mismatch would slip past the value-shape assertions above.
        var method = typeof(PatBoundedStatsQuery_PInvoke)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name.StartsWith("PInvoke_presetA_Get", StringComparison.Ordinal));
        AssertNotNull(method, "PInvoke_presetA_Get_* exists on PatBoundedStatsQuery_PInvoke");

        var parameters = method!.GetParameters();
        AssertEqual(1, parameters.Length, "PresetA P/Invoke has exactly one parameter (resultPtr)");
        AssertEqual(typeof(IntPtr), parameters[0].ParameterType, "sole parameter is IntPtr (resultPtr)");
        AssertEqual(typeof(void), method.ReturnType, "PresetA P/Invoke returns void");

        var libraryImport = method.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.InteropServices.LibraryImportAttribute");
        AssertNotNull(libraryImport, "PresetA P/Invoke is LibraryImport-attributed");
        var libName = libraryImport!.ConstructorArguments.Count > 0
            ? libraryImport.ConstructorArguments[0].Value as string
            : null;
        AssertEqual("SwiftBindings", libName, "PresetA P/Invoke targets the SwiftBindings wrapper library");
        var entryPoint = libraryImport.NamedArguments
            .FirstOrDefault(a => a.MemberName == "EntryPoint").TypedValue.Value as string;
        AssertEqual("SBW_Get_SwiftBindingsTestLib_PatBoundedStatsQuery_presetA", entryPoint,
            "PresetA P/Invoke entry point is the SBW_Get_* wrapper symbol (not the Swift mangled name)");
    }
}
