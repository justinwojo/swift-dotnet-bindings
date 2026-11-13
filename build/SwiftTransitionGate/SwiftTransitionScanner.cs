// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// The transition rules this engine implements. R3 reads the bridge plans of
/// <see cref="SwiftTransitionScanOptions.PlanSource"/>; R6 (weak linking) belongs to the thunk
/// weak-link gate, and R7 (reverse coverage) arrives with the reverse stubs.
/// </summary>
public enum TransitionRule
{
    /// <summary>A forward Swift-convention P/Invoke whose native signature or result is not primitive-only.</summary>
    R1,

    /// <summary><c>SwiftSelf</c>, <c>SwiftSelf&lt;T&gt;</c> or <c>SwiftIndirectResult</c> in a non-Swift
    /// signature: a P/Invoke, a <c>calli</c>, an <c>UnmanagedCallersOnly</c> method or a
    /// function-pointer type.</summary>
    R2,

    /// <summary>
    /// A forward bridge's twin import (<c>SBSR_…</c>) that is not the C-convention, primitive-only
    /// signature its plan states: a different convention, no plan or no plan source, a reverse or
    /// thick plan, or a parameter or result whose native carrier the plan's slot does not allow.
    /// </summary>
    R3,

    /// <summary>An <c>UnmanagedCallersOnly</c> method with the Swift convention, or any
    /// <c>SwiftSelf&lt;T&gt;</c> in a reverse signature.</summary>
    R4,

    /// <summary>A forward <c>calli</c> through an <c>unmanaged[Swift]</c> function pointer whose
    /// signature is not primitive-only.</summary>
    R5,
}

/// <summary>Where a transition occurs.</summary>
public enum TransitionSiteKind
{
    PInvoke,
    UnmanagedCallersOnly,
    Calli,
    FunctionPointerType,

    /// <summary>
    /// An <c>ldftn</c> of a Swift-convention <c>[UnmanagedCallersOnly]</c> method of the same assembly:
    /// where managed code takes a reverse callback's address to hand Swift as a closure function.
    /// Not a transition itself and never a rule hit; listed so a ledger of construction sites can be
    /// joined against the IL.
    /// </summary>
    CallbackAddress,
}

/// <summary>One rule violation: which rule, at which member, over which signature, and why.</summary>
public sealed record TransitionViolation(
    TransitionRule Rule,
    TransitionSiteKind Site,
    string Member,
    string Signature,
    string Reason,
    string? EntryPoint = null,
    string? Library = null,
    int? IlOffset = null,
    ImmutableArray<string> Classes = default);

/// <summary>
/// One transition site, for the machine-readable inventory: every P/Invoke declaration, every
/// <c>UnmanagedCallersOnly</c> method and every unmanaged <c>calli</c>, plus each managed
/// <c>calli</c> and function-pointer type a rule flagged. <see cref="Rules"/> lists the rules it violates (empty when it is safe).
/// <see cref="MemberToken"/> is the metadata token of the method (or, for a field's function-pointer
/// type, the field) the site belongs to, so overloads that share a display name stay distinct.
/// </summary>
public sealed record TransitionSite(
    TransitionSiteKind Kind,
    string Member,
    int MemberToken,
    TransitionConvention Convention,
    string Signature,
    string? EntryPoint,
    string? Library,
    int? IlOffset,
    ImmutableArray<TransitionRule> Rules,
    string? Target = null);

/// <summary>An IL body or metadata blob the gate could not inspect. Any one fails the gate.</summary>
public sealed record InspectionError(string Member, string Message);

/// <summary>
/// The shape inventory of one assembly: what the rules looked at, so a zero is never vacuous by
/// accident. Counts are of declarations and sites, not of rule hits.
/// </summary>
public sealed class TransitionInventory
{
    public int SwiftPInvokes { get; set; }
    public int SwiftPInvokesPrimitiveOnly { get; set; }
    public int SwiftPInvokesSpecialBearing { get; set; }
    public int SwiftPInvokesAggregateOnly { get; set; }
    public int SwiftPInvokesWithSwiftSelf { get; set; }
    public int SwiftPInvokesWithSwiftSelfGeneric { get; set; }

    /// <summary>Of the forward Swift P/Invokes' <c>SwiftSelf&lt;T&gt;</c> carriers, those CoreCLR lowers into ordinary registers.</summary>
    public int SwiftSelfGenericLowered { get; set; }

    /// <summary>… those CoreCLR passes by address in the self register, which the arm64 interpreter corrupts.</summary>
    public int SwiftSelfGenericByAddress { get; set; }

    /// <summary>… those whose placement could not be read with certainty.</summary>
    public int SwiftSelfGenericPlacementUnknown { get; set; }
    public int SwiftPInvokesWithSwiftError { get; set; }
    public int SwiftPInvokesWithSwiftIndirectResult { get; set; }
    public int NonSwiftPInvokes { get; set; }
    public int NonSwiftPInvokesWithSwiftSpecial { get; set; }
    public int NonSwiftPInvokesWithSwiftErrorPointer { get; set; }
    public int BridgeTwins { get; set; }
    public int SwiftUnmanagedCallersOnly { get; set; }
    public int NonSwiftUnmanagedCallersOnly { get; set; }
    public int SwiftCalliSites { get; set; }
    public int SwiftCallbackAddresses { get; set; }
    public int NonSwiftUnmanagedCalliSites { get; set; }
    public int ManagedCalliSites { get; set; }
    public int MethodBodies { get; set; }
    public int Instructions { get; set; }
}

/// <summary>The gate's result for one assembly.</summary>
public sealed record AssemblyTransitionReport(
    string Path,
    string AssemblyName,
    Guid Mvid,
    TransitionInventory Inventory,
    ImmutableArray<TransitionViolation> Violations,
    ImmutableArray<InspectionError> InspectionErrors,
    ImmutableArray<TransitionSite> Sites,
    TransitionPlanSource? PlanSource)
{
    public bool Passed => Violations.IsEmpty && InspectionErrors.IsEmpty;
}

/// <summary>Where a binding's bridge plans come from.</summary>
public enum TransitionPlanSourceKind
{
    /// <summary>A directory holding the generator's call-plan sidecars (<c>*.swift-call-plans.json</c>).</summary>
    CallPlanSidecars,
}

/// <summary>
/// The plan source a scan runs against. R3 reads it; a scan without one still checks the other rules,
/// and any bridge twin it meets is an R3 violation, since nothing says what the twin must be. It is
/// recorded in the report so a result says which plans it saw.
/// </summary>
public sealed record TransitionPlanSource(TransitionPlanSourceKind Kind, string Location);

public sealed class SwiftTransitionScanOptions
{
    /// <summary>Directories searched for referenced assemblies, to resolve carrier value types.</summary>
    public IReadOnlyList<string> ReferenceDirectories { get; init; } = Array.Empty<string>();

    /// <summary>Additional in-memory reference images (tests).</summary>
    public IReadOnlyList<byte[]> ReferenceImages { get; init; } = Array.Empty<byte[]>();

    /// <summary>The binding's bridge plans, for the plan-dependent rules; null when there are none.</summary>
    public TransitionPlanSource? PlanSource { get; init; }
}

/// <summary>
/// The Swift transition gate's rule engine: scans one compiled assembly with
/// <c>System.Reflection.Metadata</c> and reports every unsafe managed↔Swift transition.
///
/// <para>It looks at P/Invoke declarations (a <c>DllImport</c>, including the inner extern a
/// <c>LibraryImport</c> generates, which is where the native signature lives), every
/// <c>UnmanagedCallersOnly</c> method, every <c>calli</c> instruction in every method body, and every
/// function-pointer type in method, field and local signatures. Conventions and types are decoded
/// structurally from metadata, never matched as text.</para>
///
/// <para>It is fail-closed: an IL body that does not decode exactly, a signature that does not
/// decode, or a carrier value type that cannot be resolved is reported, never skipped.</para>
/// </summary>
public static class SwiftTransitionScanner
{
    public const string BridgePrefix = "SBSR_";
    public const string ReverseBridgePrefix = "SBSRR_";

    /// <summary>True when <paramref name="entryPoint"/> names a forward bridge (thin or thick).</summary>
    public static bool IsForwardBridge(string? entryPoint) =>
        entryPoint is not null && entryPoint.StartsWith(BridgePrefix, StringComparison.Ordinal);

    private const string InteropNamespace = "System.Runtime.InteropServices";

    public static AssemblyTransitionReport Scan(string path, SwiftTransitionScanOptions options)
    {
        var image = File.ReadAllBytes(path);
        return Scan(image, path, options);
    }

    public static AssemblyTransitionReport Scan(byte[] image, string displayPath, SwiftTransitionScanOptions options)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var md = pe.GetMetadataReader();
        using var resolver = new AssemblyResolver(options.ReferenceDirectories);
        foreach (var reference in options.ReferenceImages)
            resolver.AddImage(reference);
        resolver.Add(md);
        var scan = new ScanState(pe, md, new CarrierClassifier(resolver), options);
        scan.Run();
        var assemblyName = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : Path.GetFileNameWithoutExtension(displayPath);
        var mvid = md.GetGuid(md.GetModuleDefinition().Mvid);
        return new AssemblyTransitionReport(displayPath, assemblyName, mvid, scan.Inventory,
            scan.Violations.OrderBy(v => v.Rule).ThenBy(v => v.Member, StringComparer.Ordinal)
                .ThenBy(v => v.Signature, StringComparer.Ordinal).ThenBy(v => v.IlOffset ?? -1).ToImmutableArray(),
            scan.Errors.ToImmutableArray(),
            scan.Sites.Select(b => b.Build())
                .OrderBy(t => t.Member, StringComparer.Ordinal).ThenBy(t => t.MemberToken)
                .ThenBy(t => t.Kind).ThenBy(t => t.IlOffset ?? -1).ThenBy(t => t.Signature, StringComparer.Ordinal)
                .ToImmutableArray(),
            options.PlanSource);
    }

    /// <summary>
    /// The canonical spelling of a built-in native carrier, the vocabulary plans and twins are
    /// compared in: <c>i8 u8 i16 u16 i32 u32 i64 u64 isize usize f32 f64 bool char ptr void</c>. Returns
    /// null for anything else; the classifier spells enums and one-word wrappers through this too
    /// (<see cref="CarrierVerdict.Canonical"/>).
    /// </summary>
    public static string? CanonicalCarrier(SigType type) => type.Unwrapped() switch
    {
        SigType.Primitive p => p.Code switch
        {
            PrimitiveTypeCode.Void => "void",
            PrimitiveTypeCode.Boolean => "bool",
            PrimitiveTypeCode.Char => "char",
            PrimitiveTypeCode.SByte => "i8",
            PrimitiveTypeCode.Byte => "u8",
            PrimitiveTypeCode.Int16 => "i16",
            PrimitiveTypeCode.UInt16 => "u16",
            PrimitiveTypeCode.Int32 => "i32",
            PrimitiveTypeCode.UInt32 => "u32",
            PrimitiveTypeCode.Int64 => "i64",
            PrimitiveTypeCode.UInt64 => "u64",
            PrimitiveTypeCode.IntPtr => "isize",
            PrimitiveTypeCode.UIntPtr => "usize",
            PrimitiveTypeCode.Single => "f32",
            PrimitiveTypeCode.Double => "f64",
            _ => null,
        },
        SigType.Pointer or SigType.ByRef or SigType.FunctionPointer => "ptr",
        _ => null,
    };

    private sealed class ScanState
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _md;
        private readonly CarrierClassifier _classifier;
        private readonly SwiftTransitionScanOptions _options;
        private readonly SigTypeProvider _provider;
        private readonly HashSet<(int MemberToken, string Signature)> _functionPointerTypesSeen = new();
        private readonly Dictionary<MethodDefinitionHandle, (string Member, string Signature)?> _swiftCallbacks = new();
        private BridgeTwinPlans? _twinPlans;
        private int _memberToken;

        public TransitionInventory Inventory { get; } = new();
        public List<TransitionViolation> Violations { get; } = new();
        public List<InspectionError> Errors { get; } = new();
        public List<SiteBuilder> Sites { get; } = new();

        public ScanState(PEReader pe, MetadataReader md, CarrierClassifier classifier, SwiftTransitionScanOptions options)
        {
            _pe = pe;
            _md = md;
            _classifier = classifier;
            _options = options;
            _provider = new SigTypeProvider(md);
        }

        public void Run()
        {
            if (_md.IsAssembly && _md.GetString(_md.GetAssemblyDefinition().Name) == CarrierClassifier.RuntimeAssemblyName)
                foreach (var problem in CarrierClassifier.VerifyAllowList(_md))
                    Errors.Add(new InspectionError(CarrierClassifier.RuntimeAssemblyName, problem));

            foreach (var typeHandle in _md.TypeDefinitions)
            {
                var type = _md.GetTypeDefinition(typeHandle);
                var typeName = TypeDisplayName(typeHandle);
                foreach (var fieldHandle in type.GetFields())
                {
                    _memberToken = MetadataTokens.GetToken(fieldHandle);
                    var field = _md.GetFieldDefinition(fieldHandle);
                    var member = typeName + "::" + _md.GetString(field.Name);
                    Guarded(member, () =>
                    {
                        if (TryDecode(member, () => _provider.DecodeField(field.Signature), out var fieldType))
                            CheckFunctionPointerTypes(member, new[] { fieldType }, "field type");
                    });
                }
                foreach (var methodHandle in type.GetMethods())
                {
                    var name = typeName + "::" + _md.GetString(_md.GetMethodDefinition(methodHandle).Name);
                    Guarded(name, () => ScanMethod(typeName, methodHandle));
                }
            }
        }

        /// <summary>
        /// Malformed metadata a member's scan trips over (a bad token, an out-of-range row, an
        /// unreadable blob) is an inspection error on that member, never an aborted scan.
        /// </summary>
        private void Guarded(string member, Action scan)
        {
            try
            {
                scan();
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                Errors.Add(new InspectionError(member, "metadata unreadable: " + ex.Message));
            }
        }

        private SiteBuilder AddSite(TransitionSiteKind kind, string member, TransitionConvention convention, string signature,
            string? entryPoint = null, string? library = null, int? ilOffset = null, string? target = null)
        {
            var site = new SiteBuilder(kind, member, _memberToken, convention, signature, entryPoint, library, ilOffset, target);
            Sites.Add(site);
            return site;
        }

        private void Report(SiteBuilder site, TransitionViolation violation)
        {
            Violations.Add(violation);
            site.Rules.Add(violation.Rule);
        }

        private void ScanMethod(string typeName, MethodDefinitionHandle handle)
        {
            var method = _md.GetMethodDefinition(handle);
            var rawName = _md.GetString(method.Name);
            var member = typeName + "::" + DisplayMethodName(rawName);
            _memberToken = MetadataTokens.GetToken(handle);
            if (!TryDecode(member, () => _provider.DecodeMethod(method.Signature), out var signature))
                return;

            var attributes = ReadInteropAttributes(member, method);
            var isPInvoke = (method.Attributes & MethodAttributes.PinvokeImpl) != 0;
            if (isPInvoke)
                ScanPInvoke(member, method, signature, attributes.UnmanagedCallConv);
            if (attributes.UnmanagedCallersOnly is { } ucoConvention)
                ScanUnmanagedCallersOnly(member, signature, ucoConvention);

            // A transition's own signature was judged above as a whole; every other method signature is
            // inspected only for the function-pointer types it carries.
            CheckFunctionPointerTypes(member, signature.ParameterTypes.Prepend(signature.ReturnType), "signature");

            if (method.RelativeVirtualAddress != 0)
                ScanBody(member, method);
        }

        private void ScanPInvoke(string member, MethodDefinition method, MethodSignature<SigType> signature,
            TransitionConvention? unmanagedCallConv)
        {
            var import = method.GetImport();
            var entryPoint = _md.GetString(import.Name);
            var library = import.Module.IsNil ? "" : _md.GetString(_md.GetModuleReference(import.Module).Name);
            if (entryPoint.Length == 0)
                entryPoint = _md.GetString(method.Name);

            // DllImport's own CallingConvention wins unless it is Winapi; only then does
            // [UnmanagedCallConv] apply (the runtime's precedence).
            var explicitConvention = (import.Attributes & MethodImportAttributes.CallingConventionMask) switch
            {
                MethodImportAttributes.CallingConventionCDecl => TransitionConvention.Cdecl,
                MethodImportAttributes.CallingConventionStdCall => TransitionConvention.Stdcall,
                MethodImportAttributes.CallingConventionThisCall => TransitionConvention.Thiscall,
                MethodImportAttributes.CallingConventionFastCall => TransitionConvention.Fastcall,
                _ => (TransitionConvention?)null,
            };
            var convention = explicitConvention ?? unmanagedCallConv ?? TransitionConvention.PlatformDefault;
            var sigText = TypeNames.Display(convention, signature);
            var carriers = Carriers(signature);
            var site = AddSite(TransitionSiteKind.PInvoke, member, convention, sigText, entryPoint, library);

            if (convention == TransitionConvention.Swift)
            {
                Inventory.SwiftPInvokes++;
                var specials = carriers.Where(c => c.Verdict.IsSwiftSpecial).Select(c => c.Verdict.Kind).ToHashSet();
                if (specials.Contains(CarrierKind.SwiftSelf)) Inventory.SwiftPInvokesWithSwiftSelf++;
                if (specials.Contains(CarrierKind.SwiftSelfGeneric)) Inventory.SwiftPInvokesWithSwiftSelfGeneric++;
                foreach (var (_, verdict) in carriers)
                {
                    switch (verdict.SelfPhysical?.Placement)
                    {
                        case LegacySelfPlacement.Lowered: Inventory.SwiftSelfGenericLowered++; break;
                        case LegacySelfPlacement.ByAddressInSelfRegister: Inventory.SwiftSelfGenericByAddress++; break;
                        case LegacySelfPlacement.Unknown: Inventory.SwiftSelfGenericPlacementUnknown++; break;
                    }
                }
                if (specials.Contains(CarrierKind.SwiftError)) Inventory.SwiftPInvokesWithSwiftError++;
                if (specials.Contains(CarrierKind.SwiftIndirectResult)) Inventory.SwiftPInvokesWithSwiftIndirectResult++;
                var offending = carriers.Where(c => !c.Verdict.IsPrimitiveOrVoid).ToList();
                if (offending.Count == 0)
                    Inventory.SwiftPInvokesPrimitiveOnly++;
                else
                {
                    if (specials.Count > 0) Inventory.SwiftPInvokesSpecialBearing++;
                    else Inventory.SwiftPInvokesAggregateOnly++;
                    Report(site, new TransitionViolation(TransitionRule.R1, TransitionSiteKind.PInvoke, member, sigText,
                        "forward Swift-convention P/Invoke is not primitive-only: " + Describe(offending),
                        entryPoint, library, Classes: ClassesOf(offending)));
                }
            }
            else
            {
                Inventory.NonSwiftPInvokes++;
                var forbidden = carriers.Where(c => IsForbiddenOutsideSwift(c.Verdict.Kind)).ToList();
                if (forbidden.Count > 0)
                {
                    Inventory.NonSwiftPInvokesWithSwiftSpecial++;
                    Report(site, new TransitionViolation(TransitionRule.R2, TransitionSiteKind.PInvoke, member, sigText,
                        $"{convention} P/Invoke carries a Swift special type: " + Describe(forbidden),
                        entryPoint, library, Classes: ClassesOf(forbidden)));
                }
                if (carriers.Any(c => c.Verdict.Kind == CarrierKind.SwiftError))
                    Inventory.NonSwiftPInvokesWithSwiftErrorPointer++;
            }

            if (IsForwardBridge(entryPoint))
            {
                Inventory.BridgeTwins++;
                if (TwinProblem(convention, carriers, ParameterNames(method), entryPoint) is { } problem)
                    Report(site, new TransitionViolation(TransitionRule.R3, TransitionSiteKind.PInvoke, member, sigText,
                        problem, entryPoint, library));
            }
        }

        /// <summary>
        /// The parameter names of an import, as declared: a source-generated import's inner P/Invoke names
        /// each parameter <c>__{name}_native</c>, which is undone here.
        /// </summary>
        private List<string> ParameterNames(MethodDefinition method)
        {
            var names = new SortedDictionary<int, string>();
            foreach (var handle in method.GetParameters())
            {
                var parameter = _md.GetParameter(handle);
                if (parameter.SequenceNumber > 0)
                    names[parameter.SequenceNumber] = _md.GetString(parameter.Name);
            }
            return names.Values
                .Select(n => n.Length > "___native".Length && n.StartsWith("__", StringComparison.Ordinal) && n.EndsWith("_native", StringComparison.Ordinal)
                    ? n[2..^"_native".Length]
                    : n)
                .ToList();
        }

        /// <summary>
        /// Why a bridge twin is not the signature its plan states, or null. The twin must use the C
        /// convention (named, or the platform default, which is C on every Apple target and is how
        /// the generator declares it), its plan must be a forward plan in the plan source, each native
        /// carrier, result first, must be one the plan's slot allows, in order and in number, and each
        /// trailing pointer must sit where the plan puts its role: the trailing pointers are all
        /// pointer-sized, so a swapped pair is told apart only by the names the generator gives them.
        /// </summary>
        private string? TwinProblem(TransitionConvention convention, List<(int Index, CarrierVerdict Verdict)> carriers,
            List<string> names, string symbol)
        {
            if (convention is not (TransitionConvention.Cdecl or TransitionConvention.PlatformDefault))
                return $"a bridge twin is called with the {convention} convention; a bridge takes the C convention";
            if (_options.PlanSource is not { } source)
                return "a bridge twin with no plan source: nothing states the signature it must have";
            _twinPlans ??= BridgeTwinPlans.Load(source);
            if (!_twinPlans.Problems.IsEmpty)
                return "the plan source does not read: " + string.Join("; ", _twinPlans.Problems);
            if (_twinPlans.Find(symbol) is not { } plan)
                return $"no plan in {source.Location} has the symbol {symbol}";
            if (plan.Kind != "Forward")
                return $"the plan {symbol} is a {plan.Kind} plan; a twin calls a forward bridge";

            var parameters = carriers.Where(c => c.Index >= 0).ToList();
            var result = carriers.Single(c => c.Index < 0).Verdict;
            var spelled = $"{result.Canonical ?? result.Display} ({string.Join(", ", parameters.Select(c => c.Verdict.Canonical ?? c.Verdict.Display))})";
            if (parameters.Count != plan.Parameters.Length)
                return $"the twin {spelled} takes {parameters.Count} argument(s); its plan {plan} takes {plan.Parameters.Length}";
            if (plan.Return is { } returned ? !Allows(returned, result) : result.Kind != CarrierKind.Void)
                return $"the twin {spelled} returns {result.Display}; its plan {plan} returns {(plan.Return is { } r ? r.ToString() : "nothing")}";
            for (var i = 0; i < parameters.Count; i++)
            {
                if (!Allows(plan.Parameters[i], parameters[i].Verdict))
                    return $"the twin {spelled} passes {parameters[i].Verdict.Display} as argument {i}; its plan {plan} passes a {plan.Parameters[i]}";
            }
            if (names.Count != parameters.Count)
                return $"the twin {spelled} names {names.Count} of its {parameters.Count} parameter(s)";
            for (var i = 0; i < parameters.Count; i++)
            {
                var role = plan.Roles[i];
                var expected = TwinPlan.TrailingName(role);
                if (expected is not null ? names[i] != expected : TwinPlan.TrailingNames.Contains(names[i]))
                    return expected is not null
                        ? $"the twin passes {names[i]} as argument {i}, where its plan {symbol} passes the {role} pointer ({expected})"
                        : $"the twin passes {names[i]} as argument {i}, where its plan {symbol} passes an ordinary argument";
            }
            return null;

            static bool Allows(TwinSlot slot, CarrierVerdict verdict) =>
                verdict.Kind == CarrierKind.Primitive && verdict.Canonical is { } canonical && slot.Carriers.Contains(canonical);
        }

        private void ScanUnmanagedCallersOnly(string member, MethodSignature<SigType> signature, TransitionConvention convention)
        {
            var sigText = TypeNames.Display(convention, signature);
            var carriers = Carriers(signature);
            var site = AddSite(TransitionSiteKind.UnmanagedCallersOnly, member, convention, sigText);
            if (convention == TransitionConvention.Swift)
                Inventory.SwiftUnmanagedCallersOnly++;
            else
                Inventory.NonSwiftUnmanagedCallersOnly++;

            var reasons = new List<string>();
            var classes = new List<string>();
            if (convention == TransitionConvention.Swift)
            {
                reasons.Add("reverse callback uses the Swift convention");
                classes.Add("swift-uco");
            }
            if (signature.ParameterTypes.Prepend(signature.ReturnType)
                .SelectMany(t => t.SelfAndDescendants())
                .Any(t => CarrierClassifier.SpecialKind(t) == CarrierKind.SwiftSelfGeneric))
            {
                reasons.Add("reverse signature carries SwiftSelf<T>");
                classes.Add(nameof(CarrierKind.SwiftSelfGeneric));
            }
            if (reasons.Count > 0)
                Report(site, new TransitionViolation(TransitionRule.R4, TransitionSiteKind.UnmanagedCallersOnly, member, sigText,
                    string.Join("; ", reasons), Classes: classes.ToImmutableArray()));

            if (convention != TransitionConvention.Swift)
            {
                var forbidden = carriers.Where(c => IsForbiddenOutsideSwift(c.Verdict.Kind)).ToList();
                if (forbidden.Count > 0)
                    Report(site, new TransitionViolation(TransitionRule.R2, TransitionSiteKind.UnmanagedCallersOnly, member, sigText,
                        $"{convention} UnmanagedCallersOnly carries a Swift special type: " + Describe(forbidden),
                        Classes: ClassesOf(forbidden)));
            }
        }

        private void ScanBody(string member, MethodDefinition method)
        {
            MethodBodyBlock body;
            try
            {
                body = _pe.GetMethodBody(method.RelativeVirtualAddress);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                Errors.Add(new InspectionError(member, "method body unreadable: " + ex.Message));
                return;
            }

            Inventory.MethodBodies++;
            if (!body.LocalSignature.IsNil &&
                TryDecode(member, () => _provider.DecodeLocals(body.LocalSignature), out var locals))
                CheckFunctionPointerTypes(member, locals, "local");

            var decoded = IlDecoder.Decode(body);
            if (decoded.Error is { } error)
            {
                Errors.Add(new InspectionError(member, $"IL does not decode at IL_{error.Offset:X4}: {error.Message}"));
                return;
            }
            Inventory.Instructions += decoded.Instructions.Length;

            foreach (var instruction in decoded.Instructions)
            {
                if (instruction.OpCode == ILOpCode.Ldftn)
                {
                    ScanFunctionAddress(member, instruction.Offset, instruction.Token);
                    continue;
                }
                if (instruction.OpCode != ILOpCode.Calli)
                    continue;
                var token = MetadataTokens.EntityHandle(instruction.Token);
                if (token.Kind != HandleKind.StandaloneSignature)
                {
                    Errors.Add(new InspectionError(member, $"calli at IL_{instruction.Offset:X4} names token 0x{instruction.Token:X8}, not a signature"));
                    continue;
                }
                var sigBlob = _md.GetStandaloneSignature((StandaloneSignatureHandle)token).Signature;
                if (!TryDecode(member, () => _provider.DecodeMethod(sigBlob), out var calliSignature))
                    continue;
                ScanCalli(member, instruction.Offset, calliSignature);
                // The call site's own signature is judged above; the function-pointer types it carries
                // may appear nowhere else in metadata.
                CheckFunctionPointerTypes(member, calliSignature.ParameterTypes.Prepend(calliSignature.ReturnType), "calli signature");
            }
        }

        /// <summary>
        /// An <c>ldftn</c> of a method of this assembly that is a Swift-convention
        /// <c>[UnmanagedCallersOnly]</c> callback is listed as a <see cref="TransitionSiteKind.CallbackAddress"/>
        /// naming its target. Any other <c>ldftn</c> (a delegate's target, a C-convention callback, a
        /// method of another assembly) is not a Swift transition and is not listed.
        /// </summary>
        private void ScanFunctionAddress(string member, int offset, int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            if (handle.Kind != HandleKind.MethodDefinition)
                return;
            var target = (MethodDefinitionHandle)handle;
            if (!_swiftCallbacks.TryGetValue(target, out var described))
            {
                described = DescribeSwiftCallback(target);
                _swiftCallbacks[target] = described;
            }
            if (described is not { } callback)
                return;
            Inventory.SwiftCallbackAddresses++;
            AddSite(TransitionSiteKind.CallbackAddress, member, TransitionConvention.Swift, callback.Signature,
                ilOffset: offset, target: callback.Member);
        }

        /// <summary>The member name and signature of a Swift-convention UCO, or null for any other method.</summary>
        private (string Member, string Signature)? DescribeSwiftCallback(MethodDefinitionHandle handle)
        {
            var method = _md.GetMethodDefinition(handle);
            var member = TypeDisplayName(method.GetDeclaringType()) + "::" + DisplayMethodName(_md.GetString(method.Name));
            if (ReadInteropAttributes(member, method).UnmanagedCallersOnly != TransitionConvention.Swift)
                return null;
            if (!TryDecode(member, () => _provider.DecodeMethod(method.Signature), out var signature))
                return null;
            return (member, TypeNames.Display(TransitionConvention.Swift, signature));
        }

        private void ScanCalli(string member, int offset, MethodSignature<SigType> signature)
        {
            var convention = Conventions.OfFunctionPointer(signature);
            var sigText = TypeNames.Display(convention, signature);
            var carriers = Carriers(signature);
            if (convention == TransitionConvention.Managed)
            {
                // A managed calli is not a transition: counted, and listed only when R2 flags it.
                Inventory.ManagedCalliSites++;
                var special = carriers.Where(c => IsForbiddenOutsideSwift(c.Verdict.Kind)).ToList();
                if (special.Count > 0)
                    Report(AddSite(TransitionSiteKind.Calli, member, convention, sigText, ilOffset: offset),
                        new TransitionViolation(TransitionRule.R2, TransitionSiteKind.Calli, member, sigText,
                            "managed calli carries a Swift special type: " + Describe(special),
                            IlOffset: offset, Classes: ClassesOf(special)));
                return;
            }
            var site = AddSite(TransitionSiteKind.Calli, member, convention, sigText, ilOffset: offset);
            switch (convention)
            {
                case TransitionConvention.Swift:
                {
                    Inventory.SwiftCalliSites++;
                    var offending = carriers.Where(c => !c.Verdict.IsPrimitiveOrVoid).ToList();
                    if (offending.Count > 0)
                        Report(site, new TransitionViolation(TransitionRule.R5, TransitionSiteKind.Calli, member, sigText,
                            "forward calli through an unmanaged[Swift] pointer is not primitive-only: " + Describe(offending),
                            IlOffset: offset, Classes: ClassesOf(offending)));
                    break;
                }
                default:
                {
                    Inventory.NonSwiftUnmanagedCalliSites++;
                    var forbidden = carriers.Where(c => IsForbiddenOutsideSwift(c.Verdict.Kind)).ToList();
                    if (forbidden.Count > 0)
                        Report(site, new TransitionViolation(TransitionRule.R2, TransitionSiteKind.Calli, member, sigText,
                            $"{convention} calli carries a Swift special type: " + Describe(forbidden),
                            IlOffset: offset, Classes: ClassesOf(forbidden)));
                    break;
                }
            }
        }

        /// <summary>
        /// R2 over function-pointer types nested anywhere in <paramref name="types"/>: a non-Swift
        /// function-pointer type (managed or unmanaged) must not carry a Swift special value type.
        /// Each distinct type is reported once per member (by metadata token), at its first location.
        /// </summary>
        private void CheckFunctionPointerTypes(string member, IEnumerable<SigType> types, string location)
        {
            foreach (var root in types)
            {
                foreach (var type in root.SelfAndDescendants())
                {
                    if (type is not SigType.FunctionPointer fnptr || fnptr.Convention == TransitionConvention.Swift)
                        continue;
                    var forbidden = Carriers(fnptr.Signature).Where(c => IsForbiddenOutsideSwift(c.Verdict.Kind)).ToList();
                    if (forbidden.Count == 0)
                        continue;
                    var sigText = TypeNames.Display(type);
                    if (!_functionPointerTypesSeen.Add((_memberToken, sigText)))
                        continue;
                    var site = AddSite(TransitionSiteKind.FunctionPointerType, member, fnptr.Convention, sigText);
                    Report(site, new TransitionViolation(TransitionRule.R2, TransitionSiteKind.FunctionPointerType, member, sigText,
                        $"{fnptr.Convention} function-pointer type ({location}) carries a Swift special type: " + Describe(forbidden),
                        Classes: ClassesOf(forbidden)));
                }
            }
        }

        private List<(int Index, CarrierVerdict Verdict)> Carriers(MethodSignature<SigType> signature)
        {
            var list = new List<(int, CarrierVerdict)> { (-1, _classifier.Classify(signature.ReturnType)) };
            for (var i = 0; i < signature.ParameterTypes.Length; i++)
                list.Add((i, _classifier.Classify(signature.ParameterTypes[i])));
            return list;
        }

        private static bool IsForbiddenOutsideSwift(CarrierKind kind) =>
            kind is CarrierKind.SwiftSelf or CarrierKind.SwiftSelfGeneric or CarrierKind.SwiftIndirectResult;

        private static string Describe(IEnumerable<(int Index, CarrierVerdict Verdict)> carriers) =>
            string.Join("; ", carriers.Select(c =>
                (c.Index < 0 ? "result" : $"parameter {c.Index}") + $" {c.Verdict.Display}: {c.Verdict.Detail ?? c.Verdict.Kind.ToString()}"));

        private static ImmutableArray<string> ClassesOf(IEnumerable<(int Index, CarrierVerdict Verdict)> carriers) =>
            carriers.Select(c => c.Index < 0 && !c.Verdict.IsSwiftSpecial ? c.Verdict.Kind + "Result" : c.Verdict.Kind.ToString())
                .Distinct().OrderBy(s => s, StringComparer.Ordinal).ToImmutableArray();

        private bool TryDecode<T>(string member, Func<T> decode, out T value)
        {
            try
            {
                value = decode();
                return true;
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                Errors.Add(new InspectionError(member, "signature does not decode: " + ex.Message));
                value = default!;
                return false;
            }
        }

        private (TransitionConvention? UnmanagedCallConv, TransitionConvention? UnmanagedCallersOnly) ReadInteropAttributes(
            string member, MethodDefinition method)
        {
            TransitionConvention? callConv = null, uco = null;
            foreach (var attributeHandle in method.GetCustomAttributes())
            {
                var attribute = _md.GetCustomAttribute(attributeHandle);
                var name = AttributeTypeName(attribute);
                var isCallConv = name == InteropNamespace + ".UnmanagedCallConvAttribute";
                if (!isCallConv && name != InteropNamespace + ".UnmanagedCallersOnlyAttribute")
                    continue;
                CustomAttributeValue<string> value;
                try
                {
                    value = attribute.DecodeValue(AttributeTypeProvider.Instance);
                }
                catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
                {
                    Errors.Add(new InspectionError(member, $"{name} does not decode: {ex.Message}"));
                    continue;
                }
                var names = value.NamedArguments
                    .Where(a => a.Name == "CallConvs" && a.Value is ImmutableArray<CustomAttributeTypedArgument<string>>)
                    .SelectMany(a => (ImmutableArray<CustomAttributeTypedArgument<string>>)a.Value!)
                    .Select(a => a.Value as string)
                    .Where(s => s is not null)
                    .Select(s => AttributeTypeProvider.StripAssemblyQualification(s!))
                    .ToList();
                var convention = Conventions.FromCallConvNames(names);
                if (isCallConv)
                    callConv = convention;
                else
                    uco = convention ?? TransitionConvention.PlatformDefault;
            }
            return (callConv, uco);
        }

        private string? AttributeTypeName(CustomAttribute attribute)
        {
            EntityHandle parent;
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                    parent = _md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                    break;
                case HandleKind.MethodDefinition:
                    parent = _md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                    break;
                default:
                    return null;
            }
            switch (parent.Kind)
            {
                case HandleKind.TypeReference:
                {
                    var (ns, name, _) = TypeNames.OfReference(_md, (TypeReferenceHandle)parent);
                    return ns + "." + name;
                }
                case HandleKind.TypeDefinition:
                {
                    var (ns, name) = TypeNames.OfDefinition(_md, (TypeDefinitionHandle)parent);
                    return ns + "." + name;
                }
                default:
                    return null;
            }
        }

        private string TypeDisplayName(TypeDefinitionHandle handle)
        {
            var (ns, name) = TypeNames.OfDefinition(_md, handle);
            return (ns.Length == 0 ? name : ns + "." + name).Replace('/', '+');
        }
    }

    /// <summary>A site under construction: the rules it violates accumulate as they are found.</summary>
    private sealed class SiteBuilder(TransitionSiteKind kind, string member, int memberToken, TransitionConvention convention,
        string signature, string? entryPoint, string? library, int? ilOffset, string? target)
    {
        public List<TransitionRule> Rules { get; } = new();

        public TransitionSite Build() => new(kind, member, memberToken, convention, signature, entryPoint, library, ilOffset,
            Rules.Distinct().OrderBy(r => r).ToImmutableArray(), target);
    }

    /// <summary>
    /// A readable member name for a compiler-generated method. A LibraryImport stub's inner extern is
    /// a local function named <c>&lt;Outer&gt;g____PInvoke|N_M</c> and is reported as <c>Outer</c>, the
    /// declaration a reader can find; another local function as <c>Outer.Local</c>; a lambda
    /// <c>&lt;Outer&gt;b__N_M</c> as <c>Outer.&lt;lambda&gt;</c>.
    /// </summary>
    public static string DisplayMethodName(string rawName)
    {
        if (rawName.Length > 1 && rawName[0] == '<')
        {
            var close = rawName.IndexOf('>');
            if (close > 1 && rawName.AsSpan(close + 1).StartsWith("g__", StringComparison.Ordinal))
            {
                var local = rawName.Substring(close + 4);
                var bar = local.IndexOf('|');
                if (bar >= 0)
                    local = local.Substring(0, bar);
                return rawName.Substring(1, close - 1) + (local == "__PInvoke" ? "" : "." + local);
            }
            if (close > 1 && rawName.AsSpan(close + 1).StartsWith("b__", StringComparison.Ordinal))
                return rawName.Substring(1, close - 1) + ".<lambda>";
        }
        return rawName;
    }

    /// <summary>Decodes the attribute arguments the gate reads (<c>Type[] CallConvs</c>, strings).</summary>
    internal sealed class AttributeTypeProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly AttributeTypeProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var (ns, name) = TypeNames.OfDefinition(reader, handle);
            return ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var (ns, name, _) = TypeNames.OfReference(reader, handle);
            return ns + "." + name;
        }

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) =>
            throw new BadImageFormatException($"enum-typed attribute argument {type} is not one the gate reads");

        public bool IsSystemType(string type) => type == "System.Type";

        public static string StripAssemblyQualification(string serialized)
        {
            var comma = serialized.IndexOf(',');
            return (comma >= 0 ? serialized.Substring(0, comma) : serialized).Trim();
        }
    }
}
