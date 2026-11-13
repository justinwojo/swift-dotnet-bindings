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
using System.Text;
using System.Text.Json;

namespace SwiftBindings.TransitionGate;

/// <summary>
/// The register-convention shapes of a direct Swift-convention transition that some .NET runtime
/// executes wrongly. The values are those of <c>Swift.Runtime.SwiftTransitionFacts</c>, which a
/// binding compiles into its guards as constants; this project does not reference the runtime, so
/// they are restated here and a unit test holds the two together.
/// </summary>
public static class SwiftTransitionShapes
{
    public const ulong SelfRegister = 1UL << 0;
    public const ulong SpecialBeforeArguments = 1UL << 1;
    public const ulong LoweredSignature = 1UL << 2;
    public const ulong FunctionPointerSelfRegister = 1UL << 8;
    public const ulong FunctionPointerSpecialBeforeArguments = 1UL << 9;
    public const ulong FunctionPointerLoweredSignature = 1UL << 10;

    private static readonly (ulong Bit, string Name)[] Names =
    {
        (SelfRegister, nameof(SelfRegister)),
        (SpecialBeforeArguments, nameof(SpecialBeforeArguments)),
        (LoweredSignature, nameof(LoweredSignature)),
        (FunctionPointerSelfRegister, nameof(FunctionPointerSelfRegister)),
        (FunctionPointerSpecialBeforeArguments, nameof(FunctionPointerSpecialBeforeArguments)),
        (FunctionPointerLoweredSignature, nameof(FunctionPointerLoweredSignature)),
    };

    /// <summary>
    /// The shapes of a forward Swift-convention signature, read from its native carriers (result
    /// first, at index -1): an untyped <c>SwiftSelf</c> loads the self register, and a typed
    /// <c>SwiftSelf&lt;T&gt;</c> counts as loading it too, matching the generator's stricter reading
    /// (a small typed self actually travels in ordinary registers; the two sides must agree). A
    /// <c>SwiftSelf</c> or <c>SwiftError*</c> before an ordinary argument is a special before
    /// arguments; and any special register, or any parameter or result that is not a primitive
    /// scalar or pointer, needs Swift lowering.
    /// </summary>
    public static ulong Of(IReadOnlyList<(int Index, CarrierVerdict Verdict)> carriers, bool functionPointer)
    {
        bool self = false, special = false, specialBefore = false, lowered = false;
        foreach (var (_, verdict) in carriers.Where(c => c.Index >= 0).OrderBy(c => c.Index))
        {
            switch (verdict.Kind)
            {
                case CarrierKind.SwiftSelf or CarrierKind.SwiftSelfGeneric:
                    self = special = lowered = true;
                    break;
                case CarrierKind.SwiftError:
                    special = lowered = true;
                    break;
                case CarrierKind.SwiftIndirectResult:
                    lowered = true;
                    break;
                default:
                    specialBefore |= special;
                    lowered |= !verdict.IsPrimitiveOrVoid;
                    break;
            }
        }
        if (carriers.Where(c => c.Index < 0).Any(c => !c.Verdict.IsPrimitiveOrVoid))
            lowered = true;

        ulong shapes = 0;
        if (self)
            shapes |= functionPointer ? FunctionPointerSelfRegister : SelfRegister;
        if (specialBefore)
            shapes |= functionPointer ? FunctionPointerSpecialBeforeArguments : SpecialBeforeArguments;
        if (lowered)
            shapes |= functionPointer ? FunctionPointerLoweredSignature : LoweredSignature;
        return shapes;
    }

    /// <summary>The shapes by name; a bit this table does not know is spelled in hex.</summary>
    public static string Describe(ulong shapes)
    {
        if (shapes == 0)
            return "None";
        var parts = Names.Where(n => (shapes & n.Bit) != 0).Select(n => n.Name).ToList();
        var unknown = shapes & ~Names.Aggregate(0UL, (all, n) => all | n.Bit);
        if (unknown != 0)
            parts.Add($"0x{unknown:X}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// One way a known-unsafe direct transition can run unguarded. <see cref="Rule"/> is one of the
/// <c>ResidualGuardCheck.*Rule</c> names.
/// </summary>
public sealed record ResidualGuardViolation(string Rule, string Member, string Reason);

/// <summary>
/// A direct Swift-convention transition some runtime mishandles: a P/Invoke, or a <c>calli</c> in
/// <see cref="Member"/>. An import no method of the assembly calls is not <see cref="Referenced"/>:
/// it has no holder to guard, and only code outside the assembly could run it, which the check
/// refuses when that code can see it.
/// </summary>
public sealed record ResidualHazard(TransitionSiteKind Kind, string Member, string? EntryPoint, int? IlOffset, ulong Shapes, bool Referenced = true);

/// <summary>
/// A method that must refuse before it runs: it holds a hazard (<see cref="Direct"/>), or code outside
/// the assembly can call it and its calls reach one (<see cref="Visible"/>). <see cref="Guard"/> is the
/// shapes its guard refuses, or null when it has none.
/// </summary>
public sealed record ResidualGuardHolder(string Member, bool Visible, bool Direct, ulong Required, ulong? Guard);

/// <summary>The residual-guard check's result for one assembly.</summary>
public sealed record ResidualGuardResult(
    string AssemblyName,
    ImmutableArray<ResidualHazard> Hazards,
    ImmutableArray<ResidualGuardHolder> Holders,
    int GuardCalls,
    int RoutedGuardCalls,
    int Methods,
    int Edges,
    int ConservativeEdges,
    ImmutableArray<ResidualGuardViolation> Violations,
    ImmutableArray<InspectionError> Errors)
{
    public bool Passed => Violations.IsEmpty && Errors.IsEmpty;
}

/// <summary>
/// Whether every known-unsafe direct Swift transition in a compiled assembly is reachable only
/// through a member that refuses first, on the runtimes that mishandle it.
///
/// <para>A hazard is a forward Swift-convention P/Invoke or <c>calli</c> with any
/// <see cref="SwiftTransitionShapes"/>, whatever its plan says about it: planned, refused or never
/// planned. Over the assembly's own call graph, read from its IL:</para>
/// <list type="bullet">
/// <item><b>holder</b>: the first method on each path to a hazard that is not compiler-generated
/// (a lambda, local function or state machine is part of the method that creates it, and a
/// <c>LibraryImport</c> stub is part of its import) begins with a guard that refuses the hazard's
/// shapes.</item>
/// <item><b>root</b>: each method code outside the assembly can call, whose calls reach a hazard,
/// begins with a guard that refuses every shape it reaches. A call through a virtual or interface
/// member is a dispatch boundary (the target is itself a root); a <c>call</c> of a virtual member, a
/// <c>base.</c> call, is not.</item>
/// <item><b>initializer</b>: no type initializer, module initializer or finalizer reaches a hazard;
/// none of them can refuse before it runs.</item>
/// </list>
/// <para>A guard is a call of <c>SwiftTransitionGuard.ThrowIfUnsupported</c> or
/// <c>EnsureSupported</c> with constant shapes. It begins a method when nothing before it can enter
/// native code (no <c>calli</c> and no call into this assembly or any assembly outside the base
/// library), no branch can skip it, and no exception region holds it; in a state machine's
/// <c>MoveNext</c> it may sit in the try block that faults the task, after the state dispatch.</para>
/// <para>Fail-closed: a body that does not decode, a token that does not resolve, or a guard whose
/// shapes are not a constant is reported. A guard is also reported when it refuses a shape nothing
/// its method reaches needs (<c>superfluous</c>): on those runtimes it would refuse a call that works,
/// so the binding would lose that member for nothing.</para>
/// </summary>
public static class ResidualGuardCheck
{
    public const string UnguardedRule = "unguarded";
    public const string UncoveredRule = "uncovered";
    public const string LateRule = "late";
    public const string InitializerRule = "initializer";
    public const string ExposedRule = "exposed";
    public const string UnattributedRule = "unattributed";
    public const string MalformedRule = "malformed";
    public const string SuperfluousRule = "superfluous";

    private const string GuardNamespace = "Swift.Runtime";
    private const string GuardTypeName = "SwiftTransitionGuard";
    private const string GuardAssembly = "Swift.Runtime";

    public static ResidualGuardResult Check(string path, AssemblyTransitionReport report) =>
        Check(File.ReadAllBytes(path), report);

    public static ResidualGuardResult Check(byte[] image, AssemblyTransitionReport report)
    {
        using var pe = new PEReader(new MemoryStream(image, writable: false));
        var state = new State(pe, pe.GetMetadataReader());
        return state.Run(report);
    }

    /// <summary>A readable summary: the counts, every holder, and every violation.</summary>
    public static string ToSummary(ResidualGuardResult result)
    {
        var text = new StringBuilder();
        text.AppendLine($"Residual transition guards: {(result.Passed ? "PASSED" : "FAILED")}");
        text.AppendLine($"  hazards {result.Hazards.Length}, holders {result.Holders.Length}, guard calls {result.GuardCalls} " +
                        $"({result.RoutedGuardCalls} refusing nothing), methods {result.Methods}, call edges {result.Edges} " +
                        $"({result.ConservativeEdges} resolved by name)");
        foreach (var hazard in result.Hazards)
            text.AppendLine($"  hazard {hazard.Kind} {hazard.Member}{(hazard.EntryPoint is { } e ? " " + e : "")}" +
                            $"{(hazard.IlOffset is { } o ? $" IL_{o:X4}" : "")}: {SwiftTransitionShapes.Describe(hazard.Shapes)}" +
                            $"{(hazard.Referenced ? "" : " (unreferenced)")}");
        foreach (var holder in result.Holders)
            text.AppendLine($"  holder {holder.Member} ({(holder.Direct ? "direct" : "")}{(holder.Direct && holder.Visible ? ", " : "")}{(holder.Visible ? "visible" : "")}): " +
                            $"needs {SwiftTransitionShapes.Describe(holder.Required)}, refuses {(holder.Guard is { } g ? SwiftTransitionShapes.Describe(g) : "nothing")}");
        foreach (var violation in result.Violations)
            text.AppendLine($"  {violation.Rule} {violation.Member}: {violation.Reason}");
        foreach (var error in result.Errors)
            text.AppendLine($"  inspection error {error.Member}: {error.Message}");
        return text.ToString();
    }

    /// <summary>
    /// The inventory tripwire against the generator's guard sidecars (<c>*.swift-transition-guards.json</c>
    /// in <paramref name="directory"/>): each hazard of a generated type is a transition a sidecar
    /// records with the same shapes, and each transition a sidecar records with shapes is a hazard, so
    /// the check never passes over an assembly whose transitions it did not see. An unreferenced
    /// import is one the generator guards nothing for, so neither side counts it. Hazards of
    /// <paramref name="handWrittenTypes"/> are the generator's to neither record nor guard; each named
    /// type must still hold one, so the list stays exact.
    /// </summary>
    public static ImmutableArray<string> CrossCheckSidecars(ResidualGuardResult result, string directory, IReadOnlyCollection<string> handWrittenTypes)
    {
        var problems = new List<string>();
        var recorded = new List<(TransitionSiteKind Kind, string Member, string? EntryPoint, ulong Shapes)>();
        foreach (var path in Directory.Exists(directory)
                     ? Directory.EnumerateFiles(directory, "*.swift-transition-guards.json").Order(StringComparer.Ordinal)
                     : Enumerable.Empty<string>())
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var transition in document.RootElement.GetProperty("transitions").EnumerateArray())
                {
                    var facts = transition.GetProperty("facts").GetUInt64();
                    if (facts == 0)
                        continue;
                    var kind = transition.GetProperty("kind").GetString() switch
                    {
                        "ForwardPInvoke" => TransitionSiteKind.PInvoke,
                        "ForwardCalli" => TransitionSiteKind.Calli,
                        var other => throw new InvalidDataException($"transition kind {other}"),
                    };
                    var member = transition.GetProperty("member").GetString() ?? "";
                    var entry = kind == TransitionSiteKind.PInvoke ? transition.GetProperty("entryPoint").GetString() : null;
                    recorded.Add((kind, member, entry, facts));
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
            {
                problems.Add($"{Path.GetFileName(path)} does not read: {ex.Message}");
            }
        }

        static string TypeOf(string member) => member.Split("::")[0];
        bool HandWritten(string member) => handWrittenTypes.Contains(TypeOf(member));

        var hazards = result.Hazards.Where(h => h.Referenced && !HandWritten(h.Member))
            .GroupBy(h => (h.Kind, h.Member, h.EntryPoint))
            .ToDictionary(g => g.Key, g => g.ToList());
        var records = recorded.GroupBy(r => (r.Kind, r.Member, r.EntryPoint)).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var (key, found) in hazards.OrderBy(kv => kv.Key.Member, StringComparer.Ordinal))
        {
            var listed = records.GetValueOrDefault(key) ?? new();
            if (listed.Count != found.Count)
                problems.Add($"{key.Kind} {key.Member}{(key.EntryPoint is { } e ? " " + e : "")}: the IL has {found.Count} hazard(s), the guard sidecars record {listed.Count}");
            else if (found.Select(h => h.Shapes).Order().SequenceEqual(listed.Select(r => r.Shapes).Order()) is false)
                problems.Add($"{key.Kind} {key.Member}: the IL's shapes ({string.Join("; ", found.Select(h => SwiftTransitionShapes.Describe(h.Shapes)))}) " +
                             $"are not the recorded ones ({string.Join("; ", listed.Select(r => SwiftTransitionShapes.Describe(r.Shapes)))})");
        }
        foreach (var (key, listed) in records.Where(kv => !hazards.ContainsKey(kv.Key)).OrderBy(kv => kv.Key.Member, StringComparer.Ordinal))
            problems.Add($"{key.Kind} {key.Member}{(key.EntryPoint is { } e ? " " + e : "")}: the guard sidecars record {listed.Count} transition(s) with shapes the IL does not hold");
        foreach (var type in handWrittenTypes.Order(StringComparer.Ordinal))
            if (!result.Hazards.Any(h => TypeOf(h.Member) == type) && !result.Holders.Any(h => TypeOf(h.Member) == type))
                problems.Add($"the hand-written type {type} holds no hazard; drop it from the hand-written list");
        return problems.ToImmutableArray();
    }

    private sealed class Method
    {
        public required MethodDefinitionHandle Handle { get; init; }
        public required string Display { get; init; }
        public required string RawName { get; init; }
        public required TypeDefinitionHandle DeclaringType { get; init; }
        public required bool Generated { get; init; }
        public ImmutableArray<IlInstruction> Instructions { get; set; } = ImmutableArray<IlInstruction>.Empty;
        public ImmutableArray<ExceptionRegion> Regions { get; set; } = ImmutableArray<ExceptionRegion>.Empty;
        public List<(MethodDefinitionHandle Target, bool Follow)> Out { get; } = new();
        public List<MethodDefinitionHandle> In { get; } = new();
        public MethodDefinitionHandle? StateMachine { get; set; }
        /// <summary>The state its state machine's <c>MoveNext</c> first runs in, when known.</summary>
        public int? InitialState { get; set; }
        public ulong Local { get; set; }
        public ulong Reach { get; set; }
        public ulong Direct { get; set; }
        public bool IsDirect { get; set; }
    }

    private sealed class State(PEReader pe, MetadataReader md)
    {
        private readonly Dictionary<MethodDefinitionHandle, Method> _methods = new();
        private readonly Dictionary<string, TypeDefinitionHandle> _typesByName = new(StringComparer.Ordinal);
        private readonly List<ResidualGuardViolation> _violations = new();
        private readonly List<InspectionError> _errors = new();
        private readonly SigTypeProvider _provider = new(md);
        private int _edges;
        private int _conservative;
        private int _guardCalls;
        private int _routedGuardCalls;
        private readonly List<(MethodDefinitionHandle Method, int Offset, ulong Mask)> _guards = new();

        public ResidualGuardResult Run(AssemblyTransitionReport report)
        {
            foreach (var typeHandle in md.TypeDefinitions)
            {
                _typesByName[SerializedName(typeHandle)] = typeHandle;
                var generatedType = IsGeneratedType(typeHandle);
                foreach (var handle in md.GetTypeDefinition(typeHandle).GetMethods())
                {
                    var definition = md.GetMethodDefinition(handle);
                    var raw = md.GetString(definition.Name);
                    _methods[handle] = new Method
                    {
                        Handle = handle,
                        RawName = raw,
                        Display = Describe(handle),
                        DeclaringType = typeHandle,
                        Generated = generatedType || raw.StartsWith('<'),
                    };
                }
            }
            foreach (var method in _methods.Values)
                Read(method);
            foreach (var method in _methods.Values)
                foreach (var (target, _) in method.Out)
                    _methods[target].In.Add(method.Handle);

            // The hazards, at the method that executes each: the import itself, or the method holding the calli.
            var hazardSites = new List<(TransitionSite Site, MethodDefinitionHandle Handle)>();
            foreach (var site in report.Sites)
            {
                if (site.Shapes == 0 || site.Convention != TransitionConvention.Swift || site.Kind is not (TransitionSiteKind.PInvoke or TransitionSiteKind.Calli))
                    continue;
                var handle = MetadataTokens.EntityHandle(site.MemberToken);
                if (handle.Kind != HandleKind.MethodDefinition || !_methods.TryGetValue((MethodDefinitionHandle)handle, out var holder))
                {
                    _errors.Add(new InspectionError(site.Member, $"transition site token 0x{site.MemberToken:X8} is not a method of this assembly"));
                    continue;
                }
                holder.Local |= site.Shapes;
                hazardSites.Add((site, holder.Handle));
            }

            Propagate();
            var hazards = new List<ResidualHazard>();
            foreach (var (site, handle) in hazardSites)
            {
                var referenced = AttributeHolders(site, _methods[handle]);
                hazards.Add(new ResidualHazard(site.Kind, site.Member, site.EntryPoint, site.IlOffset, site.Shapes, referenced));
            }

            var holders = new List<ResidualGuardHolder>();
            var accounted = new HashSet<(MethodDefinitionHandle, int)>();
            foreach (var method in _methods.Values.OrderBy(m => m.Display, StringComparer.Ordinal))
            {
                if (IsInitializer(method))
                {
                    if (method.Reach != 0)
                        _violations.Add(new ResidualGuardViolation(InitializerRule, method.Display,
                            $"reaches a transition of shape {SwiftTransitionShapes.Describe(method.Reach)}, and it cannot refuse before it runs"));
                    continue;
                }
                if (method.Generated || IsImport(method) || IsStub(method))
                {
                    if ((IsImport(method) || IsStub(method)) && method.Reach != 0 && IsVisible(method))
                        _violations.Add(new ResidualGuardViolation(ExposedRule, method.Display,
                            $"code outside the assembly can call this transition of shape {SwiftTransitionShapes.Describe(method.Reach)} with no guard before it"));
                    continue;
                }
                var visible = method.Reach != 0 && IsVisible(method);
                if (!method.IsDirect && !visible)
                    continue;
                var required = (visible ? method.Reach : 0) | method.Direct;
                var guard = GuardOf(method, out var late, out var at);
                if (at is { } guardAt)
                    accounted.Add(guardAt);
                holders.Add(new ResidualGuardHolder(method.Display, visible, method.IsDirect, required, guard));
                if (guard is null)
                    _violations.Add(new ResidualGuardViolation(UnguardedRule, method.Display,
                        $"{(method.IsDirect ? "holds" : "can be called from outside the assembly and reaches")} a transition of shape " +
                        $"{SwiftTransitionShapes.Describe(required)} with no guard"));
                else if ((guard.Value & required) != required)
                    _violations.Add(new ResidualGuardViolation(UncoveredRule, method.Display,
                        $"its guard refuses {SwiftTransitionShapes.Describe(guard.Value)}, but it reaches {SwiftTransitionShapes.Describe(required)}"));
                if (late is not null)
                    _violations.Add(new ResidualGuardViolation(LateRule, method.Display, late));
                if (guard is { } refuses && (refuses & ~required) != 0)
                    _violations.Add(new ResidualGuardViolation(SuperfluousRule, method.Display,
                        $"its guard refuses {SwiftTransitionShapes.Describe(refuses & ~required)}, which nothing it reaches needs"));
            }
            foreach (var (handle, offset, mask) in _guards)
            {
                if (accounted.Contains((handle, offset)))
                    continue;
                var owner = _methods.Values.FirstOrDefault(m => m.StateMachine == handle) ?? _methods[handle];
                _violations.Add(new ResidualGuardViolation(SuperfluousRule, owner.Display,
                    $"its guard at IL_{offset:X4} refuses {SwiftTransitionShapes.Describe(mask)}, and it neither holds nor, callable from outside, reaches a transition that needs it"));
            }

            return new ResidualGuardResult(
                md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "",
                hazards.OrderBy(h => h.Member, StringComparer.Ordinal).ThenBy(h => h.IlOffset ?? -1).ToImmutableArray(),
                holders.ToImmutableArray(),
                _guardCalls, _routedGuardCalls, _methods.Count, _edges, _conservative,
                _violations.Distinct().OrderBy(v => v.Rule, StringComparer.Ordinal).ThenBy(v => v.Member, StringComparer.Ordinal).ToImmutableArray(),
                _errors.ToImmutableArray());
        }

        // ---- reading -------------------------------------------------------------------------------

        private void Read(Method method)
        {
            var definition = md.GetMethodDefinition(method.Handle);
            method.StateMachine = StateMachineOf(method, definition);
            if (method.StateMachine is { } moveNext)
                AddEdge(method, moveNext, follow: true);
            if (definition.RelativeVirtualAddress == 0)
                return;
            MethodBodyBlock body;
            try
            {
                body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                _errors.Add(new InspectionError(method.Display, "method body unreadable: " + ex.Message));
                return;
            }
            var decoded = IlDecoder.Decode(body);
            if (decoded.Error is { } error)
            {
                _errors.Add(new InspectionError(method.Display, $"IL does not decode at IL_{error.Offset:X4}: {error.Message}"));
                return;
            }
            method.Instructions = decoded.Instructions;
            method.Regions = body.ExceptionRegions;
            for (var i = 0; i < decoded.Instructions.Length; i++)
            {
                var instruction = decoded.Instructions[i];
                if (!IsReference(instruction.OpCode))
                    continue;
                var token = MetadataTokens.EntityHandle(instruction.Token);
                if (IsGuardCall(token, out _))
                {
                    _guardCalls++;
                    if (MaskBefore(decoded.Instructions, i) is not { } mask)
                        _violations.Add(new ResidualGuardViolation(MalformedRule, method.Display,
                            $"the guard at IL_{instruction.Offset:X4} is not passed constant shapes"));
                    else if (mask == 0)
                        _routedGuardCalls++;
                    else
                        _guards.Add((method.Handle, instruction.Offset, mask));
                    continue;
                }
                foreach (var target in Resolve(token, method.Display, out var conservative, out _))
                {
                    if (conservative)
                        _conservative++;
                    // A delegate or function pointer to a lambda or local function is part of the method
                    // that makes it; one to a declared method runs it wherever it is invoked, a boundary
                    // like a dispatch, where the method is a root if code outside can call it.
                    var follow = instruction.OpCode switch
                    {
                        ILOpCode.Ldftn or ILOpCode.Ldvirtftn => _methods[target].Generated,
                        ILOpCode.Callvirt => !Dispatches(target),
                        _ => true,
                    };
                    AddEdge(method, target, follow);
                }
            }
        }

        private void AddEdge(Method from, MethodDefinitionHandle to, bool follow)
        {
            from.Out.Add((to, follow));
            _edges++;
        }

        private static bool IsReference(ILOpCode opCode) =>
            opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp;

        /// <summary>
        /// The methods of this assembly <paramref name="token"/> names. A member reference on a generic
        /// instantiation of one of its types resolves by name and signature; when no signature matches
        /// exactly, every method of that name is taken (<paramref name="conservative"/>).
        /// <paramref name="assembly"/> is the declaring assembly's name, empty for this one.
        /// </summary>
        private List<MethodDefinitionHandle> Resolve(EntityHandle token, string member, out bool conservative, out string assembly)
        {
            conservative = false;
            assembly = "";
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                    return new() { (MethodDefinitionHandle)token };
                case HandleKind.MethodSpecification:
                    return Resolve(md.GetMethodSpecification((MethodSpecificationHandle)token).Method, member, out conservative, out assembly);
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)token);
                    if (reference.GetKind() != MemberReferenceKind.Method)
                        return new();
                    if (reference.Parent.Kind == HandleKind.MethodDefinition)
                        return new() { (MethodDefinitionHandle)reference.Parent };
                    if (TypeOf(reference.Parent, out assembly) is not { } type)
                        return new();
                    var name = md.GetString(reference.Name);
                    var signature = md.GetBlobBytes(reference.Signature);
                    var named = md.GetTypeDefinition(type).GetMethods().Where(h => md.GetString(md.GetMethodDefinition(h).Name) == name).ToList();
                    var exact = named.Where(h => md.GetBlobBytes(md.GetMethodDefinition(h).Signature).AsSpan().SequenceEqual(signature)).ToList();
                    if (exact.Count > 0)
                        return exact;
                    if (named.Count == 0)
                        _errors.Add(new InspectionError(member, $"{SerializedName(type)}::{name} does not resolve to a method of this assembly"));
                    conservative = true;
                    return named;
                }
                default:
                    _errors.Add(new InspectionError(member, $"a call names token 0x{MetadataTokens.GetToken(token):X8}, which is not a method"));
                    return new();
            }
        }

        /// <summary>The type of this assembly <paramref name="handle"/> names, or null with its assembly.</summary>
        private TypeDefinitionHandle? TypeOf(EntityHandle handle, out string assembly)
        {
            assembly = "";
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (TypeDefinitionHandle)handle;
                case HandleKind.TypeReference:
                {
                    var (ns, name, scope) = TypeNames.OfReference(md, (TypeReferenceHandle)handle);
                    if (scope.Length > 0)
                    {
                        assembly = scope;
                        return null;
                    }
                    if (_typesByName.TryGetValue((ns.Length > 0 ? ns + "." : "") + name.Replace('/', '+'), out var local))
                        return local;
                    assembly = "?";
                    return null;
                }
                case HandleKind.TypeSpecification:
                {
                    var reader = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (reader.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                    {
                        assembly = "?";
                        return null;
                    }
                    reader.ReadSignatureTypeCode();
                    return TypeOf(reader.ReadTypeHandle(), out assembly);
                }
                default:
                    assembly = "?";
                    return null;
            }
        }

        private bool IsGuardCall(EntityHandle token, out string name)
        {
            name = "";
            if (token.Kind != HandleKind.MemberReference)
                return false;
            var reference = md.GetMemberReference((MemberReferenceHandle)token);
            if (reference.Parent.Kind != HandleKind.TypeReference)
                return false;
            var (ns, type, scope) = TypeNames.OfReference(md, (TypeReferenceHandle)reference.Parent);
            name = md.GetString(reference.Name);
            return ns == GuardNamespace && type == GuardTypeName && scope == GuardAssembly
                   && name is "ThrowIfUnsupported" or "EnsureSupported";
        }

        /// <summary>The constant shapes passed to the guard call at <paramref name="call"/>: <c>ldc; [conv]; ldstr; call</c>.</summary>
        private static ulong? MaskBefore(ImmutableArray<IlInstruction> instructions, int call)
        {
            if (call < 2 || instructions[call - 1].OpCode != ILOpCode.Ldstr)
                return null;
            var at = call - 2;
            var widen = instructions[at].OpCode;
            if (widen is ILOpCode.Ldc_i8)
                return unchecked((ulong)instructions[at].Int64Operand);
            if (widen is not (ILOpCode.Conv_i8 or ILOpCode.Conv_u8) || at < 1)
                return null;
            var load = instructions[at - 1];
            int? value = load.OpCode switch
            {
                ILOpCode.Ldc_i4_m1 => -1,
                >= ILOpCode.Ldc_i4_0 and <= ILOpCode.Ldc_i4_8 => load.OpCode - ILOpCode.Ldc_i4_0,
                ILOpCode.Ldc_i4_s or ILOpCode.Ldc_i4 => (int)load.Int64Operand,
                _ => null,
            };
            if (value is not { } v)
                return null;
            return widen == ILOpCode.Conv_u8 ? (uint)v : unchecked((ulong)(long)v);
        }

        /// <summary>The <c>MoveNext</c> of the state machine an async or iterator method runs in.</summary>
        private MethodDefinitionHandle? StateMachineOf(Method method, MethodDefinition definition)
        {
            foreach (var handle in definition.GetCustomAttributes())
            {
                var attribute = md.GetCustomAttribute(handle);
                if (AttributeName(attribute) is not ("System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                    or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"
                    or "System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute"))
                    continue;
                string? typeName;
                try
                {
                    typeName = attribute.DecodeValue(TypeNameProvider.Instance).FixedArguments.FirstOrDefault().Value as string;
                }
                catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
                {
                    _errors.Add(new InspectionError(method.Display, "state machine attribute does not decode: " + ex.Message));
                    continue;
                }
                var comma = typeName?.IndexOf(',') ?? -1;
                if (comma >= 0)
                    typeName = typeName![..comma].Trim();
                if (typeName is null || !_typesByName.TryGetValue(typeName, out var type))
                {
                    _errors.Add(new InspectionError(method.Display, $"state machine {typeName} is not a type of this assembly"));
                    continue;
                }
                var moveNext = md.GetTypeDefinition(type).GetMethods().Where(h => md.GetString(md.GetMethodDefinition(h).Name) == "MoveNext").ToList();
                if (moveNext.Count != 1)
                {
                    _errors.Add(new InspectionError(method.Display, $"state machine {typeName} has {moveNext.Count} MoveNext methods"));
                    continue;
                }
                // The compiler starts an async method's machine in state -1, and an iterator's first
                // MoveNext runs in state 0, whether its constructor or GetEnumerator set it. An async
                // iterator's is left unknown, so any dispatch ahead of its guard counts as a branch.
                method.InitialState = AttributeName(attribute) switch
                {
                    "System.Runtime.CompilerServices.AsyncStateMachineAttribute" => -1,
                    "System.Runtime.CompilerServices.IteratorStateMachineAttribute" => 0,
                    _ => null,
                };
                return moveNext[0];
            }
            return null;
        }

        private string? AttributeName(CustomAttribute attribute)
        {
            EntityHandle parent = attribute.Constructor.Kind switch
            {
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent,
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType(),
                _ => default,
            };
            return parent.Kind switch
            {
                HandleKind.TypeReference => TypeNames.OfReference(md, (TypeReferenceHandle)parent) is var (ns, name, _) ? ns + "." + name : null,
                HandleKind.TypeDefinition => SerializedName((TypeDefinitionHandle)parent),
                _ => null,
            };
        }

        // ---- the graph -----------------------------------------------------------------------------

        /// <summary>Each method's <see cref="Method.Reach"/>: the shapes it holds or reaches over edges that are not dispatch.</summary>
        private void Propagate()
        {
            foreach (var method in _methods.Values)
                method.Reach = method.Local;
            var queue = new Queue<Method>(_methods.Values.Where(m => m.Reach != 0));
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                foreach (var caller in CallersOf(method))
                {
                    var reach = caller.Reach | method.Reach;
                    if (reach == caller.Reach)
                        continue;
                    caller.Reach = reach;
                    queue.Enqueue(caller);
                }
            }
        }

        /// <summary>
        /// Marks the holders of one hazard, and says whether it has any: walking back over calls from
        /// the method that executes it, through compiler-generated methods and the import's own
        /// <c>LibraryImport</c> stub, to the first method that is neither. A compiler-generated method
        /// nothing refers to is reported, since the walk cannot tell which method it belongs to.
        /// </summary>
        private bool AttributeHolders(TransitionSite site, Method executes)
        {
            var seen = new HashSet<MethodDefinitionHandle>();
            var queue = new Queue<Method>();
            var marked = false;
            if (site.Kind == TransitionSiteKind.Calli)
                queue.Enqueue(executes);
            else
                foreach (var caller in CallersOf(executes))
                    queue.Enqueue(caller);
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                if (!seen.Add(method.Handle))
                    continue;
                if (method.Generated || IsStubOf(method, executes))
                {
                    var referrers = CallersOf(method).Where(m => m.Handle != method.Handle).ToList();
                    if (referrers.Count == 0 && method.Generated && !IsImport(method))
                        _violations.Add(new ResidualGuardViolation(UnattributedRule, method.Display,
                            $"holds a transition of shape {SwiftTransitionShapes.Describe(site.Shapes)} ({site.Member}), and no method of the assembly creates it"));
                    foreach (var referrer in referrers)
                        queue.Enqueue(referrer);
                    continue;
                }
                method.IsDirect = true;
                method.Direct |= site.Shapes;
                marked = true;
            }
            return marked;
        }

        /// <summary>The methods whose calls run <paramref name="method"/> where they stand (no dispatch, no delegate to a declared method).</summary>
        private IEnumerable<Method> CallersOf(Method method) =>
            method.In.Distinct().Select(h => _methods[h]).Where(caller => caller.Out.Any(e => e.Target == method.Handle && e.Follow));

        private bool IsImport(Method method) =>
            (md.GetMethodDefinition(method.Handle).Attributes & MethodAttributes.PinvokeImpl) != 0;

        /// <summary>A <c>LibraryImport</c> stub: the method whose local function is the import that carries its name.</summary>
        private bool IsStub(Method method) =>
            method.Out.Any(e => _methods[e.Target] is var target && IsImport(target) && IsStubOf(method, target));

        private static bool IsStubOf(Method method, Method import) =>
            import.RawName.StartsWith("<" + method.RawName + ">g____PInvoke|", StringComparison.Ordinal)
            && method.DeclaringType == import.DeclaringType;

        private bool IsInitializer(Method method)
        {
            var definition = md.GetMethodDefinition(method.Handle);
            if (method.RawName == ".cctor")
                return true;
            if (method.RawName == "Finalize" && (definition.Attributes & MethodAttributes.Virtual) != 0 && definition.GetParameters().Count == 0)
                return true;
            return definition.GetCustomAttributes().Any(h => AttributeName(md.GetCustomAttribute(h)) == "System.Runtime.CompilerServices.ModuleInitializerAttribute");
        }

        /// <summary>Whether a call that binds to <paramref name="handle"/> may run another implementation.</summary>
        private bool Dispatches(MethodDefinitionHandle handle)
        {
            var method = md.GetMethodDefinition(handle);
            var type = md.GetTypeDefinition(method.GetDeclaringType());
            if ((type.Attributes & TypeAttributes.Interface) != 0)
                return true;
            if ((method.Attributes & MethodAttributes.Virtual) == 0 || (method.Attributes & MethodAttributes.Final) != 0)
                return false;
            return (type.Attributes & TypeAttributes.Sealed) == 0;
        }

        /// <summary>
        /// Whether code outside the assembly can call <paramref name="method"/>: it is public or
        /// protected in a type that is, or it overrides or implements a member that can be called.
        /// </summary>
        private bool IsVisible(Method method)
        {
            var definition = md.GetMethodDefinition(method.Handle);
            var attributes = definition.Attributes;
            if ((attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0)
                return true; // an override
            var type = md.GetTypeDefinition(method.DeclaringType);
            foreach (var implementation in type.GetMethodImplementations())
                if (md.GetMethodImplementation(implementation).MethodBody == method.Handle)
                    return true; // an explicit interface implementation
            if ((attributes & (MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot))
                == (MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot))
                return true; // an implicit interface implementation
            if ((attributes & MethodAttributes.MemberAccessMask) is not (MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem))
                return false;
            for (var current = method.DeclaringType; !current.IsNil; current = md.GetTypeDefinition(current).GetDeclaringType())
            {
                var visibility = md.GetTypeDefinition(current).Attributes & TypeAttributes.VisibilityMask;
                if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// The shapes the guard that begins <paramref name="method"/> refuses (in its state machine's
        /// <c>MoveNext</c> for an async or iterator method), or null when none does; <paramref name="late"/>
        /// says why a guard found does not begin it, and <paramref name="at"/> is where that guard sits.
        /// </summary>
        private ulong? GuardOf(Method method, out string? late, out (MethodDefinitionHandle, int)? at)
        {
            late = null;
            at = null;
            var body = method.StateMachine is { } moveNext ? _methods[moveNext] : method;
            var stateMachine = method.StateMachine is not null;
            var instructions = body.Instructions;
            for (var i = 0; i < instructions.Length; i++)
            {
                var instruction = instructions[i];
                if (instruction.OpCode != ILOpCode.Call || !IsGuardCall(MetadataTokens.EntityHandle(instruction.Token), out _))
                    continue;
                var offset = instruction.Offset;
                late = EntryPath(instructions, i, stateMachine ? method.InitialState : null);
                if (late is null && body.Regions.Any(r =>
                        Inside(offset, r.HandlerOffset, r.HandlerLength)
                        || r.Kind == ExceptionRegionKind.Filter && offset >= r.FilterOffset && offset < r.HandlerOffset
                        || !stateMachine && Inside(offset, r.TryOffset, r.TryLength)))
                    late = $"its guard at IL_{offset:X4} sits inside an exception region";
                at = (body.Handle, offset);
                return MaskBefore(instructions, i) ?? 0;
            }
            return null;

            static bool Inside(int offset, int start, int length) => offset >= start && offset < start + length;
        }

        /// <summary>
        /// Why the way into a body may reach native code, or leave, before its guard at
        /// <paramref name="guard"/>; null when every entry reaches the guard first. The way is followed
        /// as it runs: an unconditional branch is taken, and a conditional one only when its operands
        /// are known — constants, and a state machine's state on its first entry
        /// (<paramref name="initialState"/>), which is how the compiler's dispatch to its resume points
        /// is told from the way into the body. A later entry resumes after an await or a yield, which
        /// only an entry that passed the guard reaches. Any other conditional branch, a loop, and a jump
        /// past the guard are ways around it.
        /// </summary>
        private string? EntryPath(ImmutableArray<IlInstruction> instructions, int guard, int? initialState)
        {
            var indexOf = new Dictionary<int, int>(instructions.Length);
            for (var k = 0; k < instructions.Length; k++)
                indexOf[instructions[k].Offset] = k;
            var stack = new List<long?>();
            var locals = new Dictionary<long, long?>();
            // A local whose address was taken can change through it, so it is never known again.
            var aliased = new HashSet<long>();
            long? state = initialState;
            var stateAliased = false;
            var visited = new HashSet<int>();
            var i = 0;
            while (i != guard)
            {
                if (i >= instructions.Length || !visited.Add(i))
                    return $"a loop at IL_{instructions[Math.Min(i, instructions.Length - 1)].Offset:X4} runs before its guard";
                var instruction = instructions[i];
                var at = $"IL_{instruction.Offset:X4}";
                int? jump = null;
                switch (instruction.OpCode)
                {
                    case ILOpCode.Calli or ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp:
                        if (Enters(instruction) is { } why)
                            return $"{why} at {at} runs before its guard";
                        stack.Clear(); // what it took and left is not modelled
                        break;
                    case ILOpCode.Ret or ILOpCode.Throw or ILOpCode.Rethrow or ILOpCode.Leave or ILOpCode.Leave_s
                        or ILOpCode.Endfinally or ILOpCode.Endfilter:
                        return $"an exit at {at} runs before its guard";
                    case ILOpCode.Br or ILOpCode.Br_s:
                        jump = instruction.BranchTargets[0];
                        break;
                    case ILOpCode.Brfalse or ILOpCode.Brfalse_s or ILOpCode.Brtrue or ILOpCode.Brtrue_s:
                    {
                        if (Pop() is not { } value)
                            return $"a branch at {at} runs before its guard";
                        if ((value == 0) == (instruction.OpCode is ILOpCode.Brfalse or ILOpCode.Brfalse_s))
                            jump = instruction.BranchTargets[0];
                        break;
                    }
                    case ILOpCode.Switch:
                    {
                        if (Pop() is not { } value)
                            return $"a branch at {at} runs before its guard";
                        if ((uint)value < (uint)instruction.BranchTargets.Length)
                            jump = instruction.BranchTargets[(int)(uint)value];
                        break;
                    }
                    case var compare when Compares(compare) is { } test:
                    {
                        var right = Pop();
                        var left = Pop();
                        if (left is not { } a || right is not { } b)
                            return $"a branch at {at} runs before its guard";
                        if (test((int)a, (int)b))
                            jump = instruction.BranchTargets[0];
                        break;
                    }
                    case ILOpCode.Nop:
                        break;
                    case ILOpCode.Ldc_i4_m1 or ILOpCode.Ldc_i4_0 or ILOpCode.Ldc_i4_1 or ILOpCode.Ldc_i4_2 or ILOpCode.Ldc_i4_3
                        or ILOpCode.Ldc_i4_4 or ILOpCode.Ldc_i4_5 or ILOpCode.Ldc_i4_6 or ILOpCode.Ldc_i4_7 or ILOpCode.Ldc_i4_8:
                        stack.Add((int)instruction.OpCode - (int)ILOpCode.Ldc_i4_0);
                        break;
                    case ILOpCode.Ldc_i4 or ILOpCode.Ldc_i4_s:
                        stack.Add((int)instruction.Int64Operand);
                        break;
                    case ILOpCode.Ldarg_0 or ILOpCode.Ldarg_1 or ILOpCode.Ldarg_2 or ILOpCode.Ldarg_3 or ILOpCode.Ldarg_s or ILOpCode.Ldarg:
                        stack.Add(null);
                        break;
                    case ILOpCode.Ldloc_0 or ILOpCode.Ldloc_1 or ILOpCode.Ldloc_2 or ILOpCode.Ldloc_3:
                        stack.Add(Local((int)instruction.OpCode - (int)ILOpCode.Ldloc_0));
                        break;
                    case ILOpCode.Ldloc_s or ILOpCode.Ldloc:
                        stack.Add(Local(instruction.Int64Operand));
                        break;
                    case ILOpCode.Ldloca_s or ILOpCode.Ldloca:
                        aliased.Add(instruction.Int64Operand);
                        stack.Add(null);
                        break;
                    case ILOpCode.Ldflda:
                        Pop();
                        stateAliased |= IsStateField(instruction.Token);
                        stack.Add(null);
                        break;
                    case ILOpCode.Stind_i or ILOpCode.Stind_i1 or ILOpCode.Stind_i2 or ILOpCode.Stind_i4 or ILOpCode.Stind_i8
                        or ILOpCode.Stind_r4 or ILOpCode.Stind_r8 or ILOpCode.Stind_ref or ILOpCode.Stobj or ILOpCode.Initobj
                        or ILOpCode.Cpobj or ILOpCode.Cpblk or ILOpCode.Initblk:
                        // A write through an address: whatever it reaches is no longer known.
                        locals.Clear();
                        state = null;
                        stack.Clear();
                        break;
                    case ILOpCode.Stloc_0 or ILOpCode.Stloc_1 or ILOpCode.Stloc_2 or ILOpCode.Stloc_3:
                        locals[(int)instruction.OpCode - (int)ILOpCode.Stloc_0] = Pop();
                        break;
                    case ILOpCode.Stloc_s or ILOpCode.Stloc:
                        locals[instruction.Int64Operand] = Pop();
                        break;
                    case ILOpCode.Ldfld:
                        Pop();
                        stack.Add(IsStateField(instruction.Token) && !stateAliased ? state : null);
                        break;
                    case ILOpCode.Stfld:
                    {
                        var value = Pop();
                        Pop();
                        if (IsStateField(instruction.Token))
                            state = value;
                        break;
                    }
                    case ILOpCode.Dup:
                    {
                        var top = Pop();
                        stack.Add(top);
                        stack.Add(top);
                        break;
                    }
                    case ILOpCode.Pop:
                        Pop();
                        break;
                    case ILOpCode.Add or ILOpCode.Sub:
                    {
                        var right = Pop();
                        var left = Pop();
                        stack.Add(left is { } a && right is { } b
                            ? unchecked(instruction.OpCode == ILOpCode.Add ? (int)a + (int)b : (int)a - (int)b)
                            : null);
                        break;
                    }
                    default:
                        // Nothing is known of what it leaves, so a branch that tests it is unknown too.
                        stack.Clear();
                        break;
                }
                if (jump is not { } target)
                {
                    i++;
                    continue;
                }
                var next = indexOf[target];
                if (next > guard)
                    return $"a branch at {at} jumps past its guard";
                i = next;
            }
            return null;

            long? Local(long index) => aliased.Contains(index) ? null : locals.GetValueOrDefault(index);

            long? Pop()
            {
                if (stack.Count == 0)
                    return null;
                var top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return top;
            }

            static Func<int, int, bool>? Compares(ILOpCode opCode) => opCode switch
            {
                ILOpCode.Beq or ILOpCode.Beq_s => (a, b) => a == b,
                ILOpCode.Bne_un or ILOpCode.Bne_un_s => (a, b) => a != b,
                ILOpCode.Bge or ILOpCode.Bge_s => (a, b) => a >= b,
                ILOpCode.Bge_un or ILOpCode.Bge_un_s => (a, b) => (uint)a >= (uint)b,
                ILOpCode.Bgt or ILOpCode.Bgt_s => (a, b) => a > b,
                ILOpCode.Bgt_un or ILOpCode.Bgt_un_s => (a, b) => (uint)a > (uint)b,
                ILOpCode.Ble or ILOpCode.Ble_s => (a, b) => a <= b,
                ILOpCode.Ble_un or ILOpCode.Ble_un_s => (a, b) => (uint)a <= (uint)b,
                ILOpCode.Blt or ILOpCode.Blt_s => (a, b) => a < b,
                ILOpCode.Blt_un or ILOpCode.Blt_un_s => (a, b) => (uint)a < (uint)b,
                _ => null,
            };
        }

        /// <summary>Whether <paramref name="token"/> names a compiler-generated state machine's state field.</summary>
        private bool IsStateField(int token)
        {
            var handle = MetadataTokens.EntityHandle(token);
            var name = handle.Kind switch
            {
                HandleKind.FieldDefinition => md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name),
                _ => null,
            };
            return name == "<>1__state";
        }

        /// <summary>Why <paramref name="instruction"/>, a call, may enter native code; null when it cannot.</summary>
        private string? Enters(IlInstruction instruction)
        {
            if (instruction.OpCode == ILOpCode.Calli)
                return "a calli";
            var token = MetadataTokens.EntityHandle(instruction.Token);
            var targets = Resolve(token, "", out _, out var assembly);
            if (targets.Count == 0 ? !IsObjectConstructor(token, assembly) : !targets.All(t => Inert(t, new HashSet<MethodDefinitionHandle>())))
                return $"a call of {(targets.Count > 0 ? _methods[targets[0]].Display : DescribeReference(token, assembly))}";
            return null;
        }

        /// <summary>
        /// Whether <paramref name="handle"/> is compiler-generated code that cannot enter native code —
        /// the constructor of a lambda's closure class, which the compiler allocates on entry, ahead of
        /// the method's first statement: it makes no <c>calli</c>, and calls only <c>object</c>'s
        /// constructor or methods that are themselves inert.
        /// </summary>
        private bool Inert(MethodDefinitionHandle handle, HashSet<MethodDefinitionHandle> visiting)
        {
            var method = _methods[handle];
            if (!method.Generated || IsImport(method) || method.Instructions.IsDefaultOrEmpty)
                return false;
            if (!visiting.Add(handle))
                return true;
            foreach (var instruction in method.Instructions)
            {
                if (instruction.OpCode == ILOpCode.Calli)
                    return false;
                if (instruction.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp))
                    continue;
                var token = MetadataTokens.EntityHandle(instruction.Token);
                var targets = Resolve(token, "", out _, out var assembly);
                if (targets.Count == 0 ? !IsObjectConstructor(token, assembly) : !targets.All(t => Inert(t, visiting)))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Whether <paramref name="token"/> is the base library's <c>object</c> constructor, the one call
        /// outside the assembly a guarded method makes ahead of its guard: a constructor chains to it
        /// before its first statement. Any other outside call may allocate or enter native code.
        /// </summary>
        private bool IsObjectConstructor(EntityHandle token, string assembly)
        {
            if (assembly is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib" or "netstandard")
                || token.Kind != HandleKind.MemberReference)
                return false;
            var reference = md.GetMemberReference((MemberReferenceHandle)token);
            return md.GetString(reference.Name) == ".ctor"
                && reference.Parent.Kind == HandleKind.TypeReference
                && TypeNames.OfReference(md, (TypeReferenceHandle)reference.Parent) is ("System", "Object", _);
        }

        private string DescribeReference(EntityHandle token, string assembly)
        {
            if (token.Kind == HandleKind.MethodSpecification)
                token = md.GetMethodSpecification((MethodSpecificationHandle)token).Method;
            if (token.Kind != HandleKind.MemberReference)
                return $"[{assembly}] 0x{MetadataTokens.GetToken(token):X8}";
            var reference = md.GetMemberReference((MemberReferenceHandle)token);
            var parent = reference.Parent.Kind == HandleKind.TypeReference
                ? TypeNames.OfReference(md, (TypeReferenceHandle)reference.Parent) is var (ns, name, _) ? $"{ns}.{name}" : "?"
                : "?";
            return $"[{assembly}]{parent}::{md.GetString(reference.Name)}";
        }

        // ---- naming --------------------------------------------------------------------------------

        private string SerializedName(TypeDefinitionHandle handle)
        {
            var type = md.GetTypeDefinition(handle);
            var name = md.GetString(type.Name);
            var declaring = type.GetDeclaringType();
            if (!declaring.IsNil)
                return SerializedName(declaring) + "+" + name;
            var ns = md.GetString(type.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        private bool IsGeneratedType(TypeDefinitionHandle handle)
        {
            for (var current = handle; !current.IsNil; current = md.GetTypeDefinition(current).GetDeclaringType())
                if (md.GetString(md.GetTypeDefinition(current).Name).StartsWith('<'))
                    return true;
            return false;
        }

        private string Describe(MethodDefinitionHandle handle)
        {
            var definition = md.GetMethodDefinition(handle);
            var name = SerializedName(definition.GetDeclaringType()) + "::" + md.GetString(definition.Name);
            try
            {
                var signature = _provider.DecodeMethod(definition.Signature);
                return $"{name}({string.Join(", ", signature.ParameterTypes.Select(TypeNames.Display))})";
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or ArgumentException)
            {
                return name + "(?)";
            }
        }
    }

    /// <summary>Decodes a state-machine attribute's <c>typeof</c> argument as its serialized name.</summary>
    private sealed class TypeNameProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly TypeNameProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSystemType() => "System.Type";

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            TypeNames.OfDefinition(reader, handle) is var (ns, name) ? (ns.Length > 0 ? ns + "." : "") + name.Replace('/', '+') : "";

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            TypeNames.OfReference(reader, handle) is var (ns, name, _) ? (ns.Length > 0 ? ns + "." : "") + name.Replace('/', '+') : "";

        public string GetTypeFromSerializedName(string name) => name;

        public PrimitiveTypeCode GetUnderlyingEnumType(string type) =>
            throw new BadImageFormatException($"enum-typed attribute argument {type} is not one the check reads");

        public bool IsSystemType(string type) => type == "System.Type";
    }
}
