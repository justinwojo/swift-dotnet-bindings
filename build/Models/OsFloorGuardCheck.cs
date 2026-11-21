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

/// <summary>An OS a binding supports whose Swift libraries lack a symbol a native entry point reaches.</summary>
internal sealed record OsFloorPoint(string Platform, Version Version)
{
    public override string ToString() => $"{Platform} {Version}";
}

/// <summary>
/// A native entry point (one P/Invoke target) that can reach a Swift symbol the OSes in
/// <see cref="RefuseOn"/> lack; <see cref="Reason"/> names the symbol and how it is reached.
/// </summary>
internal sealed record OsFloorHazard(string Library, string EntryPoint, IReadOnlyList<OsFloorPoint> RefuseOn, string Reason);

internal sealed record OsFloorViolation(string Rule, string Member, string Reason);

/// <summary>One P/Invoke the check judged: its hazard, and every leaf that holds a call to it.</summary>
internal sealed record OsFloorImport(string Member, OsFloorHazard Hazard, ImmutableArray<string> Leaves);

internal sealed record OsFloorGuardResult(
    string Assembly,
    int PInvokes,
    ImmutableArray<OsFloorImport> Hazards,
    int RefusingLeaves,
    int CoveredPrivateLeaves,
    ImmutableArray<OsFloorViolation> Violations,
    ImmutableArray<string> Errors)
{
    public bool Passed => Violations.IsEmpty && Errors.IsEmpty;
}

/// <summary>
/// Load-safety P2, read from the compiled assembly: on each OS a native entry point's
/// <see cref="OsFloorHazard"/> names, every managed path into that P/Invoke must throw
/// <see cref="PlatformNotSupportedException"/> before the P/Invoke runs. The wrapper weak-links its
/// OS frameworks, so a call that reaches an absent symbol jumps to null.
///
/// <para>The check finds each import's <b>leaves</b>: the methods that hold a call to it, walking up
/// from the import through its <c>LibraryImport</c> stub and through compiler-generated methods
/// (lambdas, local functions, state machines) to the first method that is neither. A leaf passes when
/// its IL, run from offset 0 with every <c>OperatingSystem</c> platform query answered for that OS,
/// reaches <c>newobj PlatformNotSupportedException; throw</c> using only constants, locals, branches
/// and those queries (<see cref="RefusesAtEntry"/>). So it reads the emitted guard itself, at whatever
/// floor it names. A private leaf that does not refuse is still covered when every method calling it
/// does (recursively): nothing outside the assembly can call it. A type or module initializer or a
/// finalizer as a leaf fails, since none can refuse; so does an import or stub code outside the
/// assembly can call, and a compiler-generated holder no method creates.</para>
/// </summary>
internal static class OsFloorGuardCheck
{
    public const string UnguardedRule = "OS-FLOOR-UNGUARDED";
    public const string ExposedRule = "OS-FLOOR-EXPOSED";
    public const string InitializerRule = "OS-FLOOR-INITIALIZER";
    public const string UnattributedRule = "OS-FLOOR-UNATTRIBUTED";

    public static OsFloorGuardResult Check(string path, Func<string, string, OsFloorHazard?> hazardOf) =>
        Check(File.ReadAllBytes(path), hazardOf);

    /// <summary>
    /// Judges every P/Invoke of the assembly in <paramref name="image"/> for which
    /// <paramref name="hazardOf"/>(library, entry point) returns a hazard.
    /// </summary>
    public static OsFloorGuardResult Check(byte[] image, Func<string, string, OsFloorHazard?> hazardOf)
    {
        using var pe = new PEReader(ImmutableArray.Create(image));
        var md = pe.GetMetadataReader();
        return new State(pe, md).Run(hazardOf);
    }

    /// <summary>Why a run of a method's entry IL stopped.</summary>
    public enum EntryOutcome { Throws, Proceeds }

    /// <summary>
    /// Runs <paramref name="instructions"/> from offset 0 on <paramref name="point"/>'s OS and says
    /// whether they throw <see cref="PlatformNotSupportedException"/> before doing anything else;
    /// <paramref name="stoppedAt"/> describes the instruction a proceeding run stopped at.
    /// </summary>
    internal static EntryOutcome RunEntry(
        ImmutableArray<IlInstruction> instructions, OsFloorPoint point, Func<IlInstruction, EntryCall> classify,
        Func<int, string?> userString, out string stoppedAt, Func<int, bool>? insideTry = null)
    {
        var index = new Dictionary<int, int>();
        for (var i = 0; i < instructions.Length; i++)
            index[instructions[i].Offset] = i;
        var stack = new Stack<object>();
        var locals = new Dictionary<long, object>();
        var pc = 0;
        for (var steps = 0; steps < 4096 && pc < instructions.Length; steps++)
        {
            var instruction = instructions[pc];
            stoppedAt = $"IL_{instruction.Offset:X4} {instruction.OpCode}";
            bool Jump(int target)
            {
                if (!index.TryGetValue(target, out var next))
                    return false;
                pc = next;
                return true;
            }
            switch (instruction.OpCode)
            {
                case ILOpCode.Nop:
                    pc++;
                    continue;
                // A constructor's field initializers run before its base call and any guard in its
                // body: loading an argument or a static field, or storing an instance field, calls
                // nothing (a static field's type initializer, if it runs, is judged as its own
                // method), so the run steps over them. Anything else still stops it.
                case ILOpCode.Ldarg_0: case ILOpCode.Ldarg_1: case ILOpCode.Ldarg_2: case ILOpCode.Ldarg_3:
                case ILOpCode.Ldarg_s: case ILOpCode.Ldarg:
                case ILOpCode.Ldsfld:
                    stack.Push(Unknown.Instance);
                    pc++;
                    continue;
                case ILOpCode.Ldnull:
                    stack.Push(Unknown.Instance);
                    pc++;
                    continue;
                case ILOpCode.Stfld:
                    if (stack.Count < 2)
                        return EntryOutcome.Proceeds;
                    stack.Pop();
                    stack.Pop();
                    pc++;
                    continue;
                case ILOpCode.Ldstr:
                    if (userString(instruction.Token) is not { } text)
                        return EntryOutcome.Proceeds;
                    stack.Push(text);
                    pc++;
                    continue;
                case ILOpCode.Ldc_i4_m1: stack.Push(-1L); pc++; continue;
                case ILOpCode.Ldc_i4_0: stack.Push(0L); pc++; continue;
                case ILOpCode.Ldc_i4_1: stack.Push(1L); pc++; continue;
                case ILOpCode.Ldc_i4_2: stack.Push(2L); pc++; continue;
                case ILOpCode.Ldc_i4_3: stack.Push(3L); pc++; continue;
                case ILOpCode.Ldc_i4_4: stack.Push(4L); pc++; continue;
                case ILOpCode.Ldc_i4_5: stack.Push(5L); pc++; continue;
                case ILOpCode.Ldc_i4_6: stack.Push(6L); pc++; continue;
                case ILOpCode.Ldc_i4_7: stack.Push(7L); pc++; continue;
                case ILOpCode.Ldc_i4_8: stack.Push(8L); pc++; continue;
                case ILOpCode.Ldc_i4_s:
                case ILOpCode.Ldc_i4:
                case ILOpCode.Ldc_i8:
                    stack.Push(instruction.Int64Operand);
                    pc++;
                    continue;
                case ILOpCode.Stloc_0: case ILOpCode.Stloc_1: case ILOpCode.Stloc_2: case ILOpCode.Stloc_3:
                case ILOpCode.Stloc_s: case ILOpCode.Stloc:
                    if (stack.Count == 0)
                        return EntryOutcome.Proceeds;
                    locals[LocalIndex(instruction)] = stack.Pop();
                    pc++;
                    continue;
                case ILOpCode.Ldloc_0: case ILOpCode.Ldloc_1: case ILOpCode.Ldloc_2: case ILOpCode.Ldloc_3:
                case ILOpCode.Ldloc_s: case ILOpCode.Ldloc:
                    if (!locals.TryGetValue(LocalIndex(instruction), out var local))
                        return EntryOutcome.Proceeds;
                    stack.Push(local);
                    pc++;
                    continue;
                case ILOpCode.Dup:
                    if (stack.Count == 0)
                        return EntryOutcome.Proceeds;
                    stack.Push(stack.Peek());
                    pc++;
                    continue;
                case ILOpCode.Pop:
                    if (stack.Count == 0)
                        return EntryOutcome.Proceeds;
                    stack.Pop();
                    pc++;
                    continue;
                case ILOpCode.Ceq:
                {
                    if (stack.Count < 2 || stack.Pop() is not long right || stack.Pop() is not long left)
                        return EntryOutcome.Proceeds;
                    stack.Push(left == right ? 1L : 0L);
                    pc++;
                    continue;
                }
                case ILOpCode.Br: case ILOpCode.Br_s:
                    if (!Jump(instruction.BranchTargets[0]))
                        return EntryOutcome.Proceeds;
                    continue;
                case ILOpCode.Brtrue: case ILOpCode.Brtrue_s: case ILOpCode.Brfalse: case ILOpCode.Brfalse_s:
                {
                    if (stack.Count == 0 || stack.Pop() is not long value)
                        return EntryOutcome.Proceeds;
                    var taken = instruction.OpCode is ILOpCode.Brtrue or ILOpCode.Brtrue_s ? value != 0 : value == 0;
                    if (!taken)
                        pc++;
                    else if (!Jump(instruction.BranchTargets[0]))
                        return EntryOutcome.Proceeds;
                    continue;
                }
                case ILOpCode.Call:
                case ILOpCode.Newobj:
                {
                    var call = classify(instruction);
                    if (call.Kind == EntryCallKind.Other || stack.Count < call.ArgumentCount)
                        return EntryOutcome.Proceeds;
                    var arguments = new object[call.ArgumentCount];
                    for (var i = call.ArgumentCount - 1; i >= 0; i--)
                        arguments[i] = stack.Pop();
                    if (call.Kind == EntryCallKind.NewPlatformNotSupported)
                    {
                        stack.Push(PlatformNotSupported.Instance);
                        pc++;
                        continue;
                    }
                    if (call.Kind == EntryCallKind.ObjectConstructor)
                    {
                        pc++;
                        continue;
                    }
                    if (Query(call, arguments, point) is not { } answer)
                        return EntryOutcome.Proceeds;
                    stack.Push(answer ? 1L : 0L);
                    pc++;
                    continue;
                }
                case ILOpCode.Throw:
                    // A throw inside a try block runs its handlers (a finally, a catch) before the
                    // exception leaves the method, and they may call anything.
                    if (insideTry?.Invoke(instruction.Offset) == true)
                    {
                        stoppedAt = $"IL_{instruction.Offset:X4} throw inside a try block, whose handlers run first";
                        return EntryOutcome.Proceeds;
                    }
                    stoppedAt = $"IL_{instruction.Offset:X4} throw";
                    return stack.Count > 0 && stack.Peek() is PlatformNotSupported ? EntryOutcome.Throws : EntryOutcome.Proceeds;
                default:
                    return EntryOutcome.Proceeds;
            }
        }
        stoppedAt = "the end of the method";
        return EntryOutcome.Proceeds;
    }

    private static long LocalIndex(IlInstruction instruction) => instruction.OpCode switch
    {
        ILOpCode.Stloc_0 or ILOpCode.Ldloc_0 => 0,
        ILOpCode.Stloc_1 or ILOpCode.Ldloc_1 => 1,
        ILOpCode.Stloc_2 or ILOpCode.Ldloc_2 => 2,
        ILOpCode.Stloc_3 or ILOpCode.Ldloc_3 => 3,
        _ => instruction.Int64Operand,
    };

    /// <summary>The answer an <c>OperatingSystem</c> query gives on <paramref name="point"/>'s OS, or null for one this check does not model.</summary>
    private static bool? Query(EntryCall call, object[] arguments, OsFloorPoint point)
    {
        bool Is(string platform) =>
            string.Equals(platform, point.Platform, StringComparison.OrdinalIgnoreCase)
            // Mac Catalyst answers to iOS too, at its iOS version.
            || (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)
                && string.Equals(point.Platform, "maccatalyst", StringComparison.OrdinalIgnoreCase));
        bool AtLeast(IEnumerable<object> parts)
        {
            var numbers = parts.Select(p => p is long l ? (int)l : -1).ToList();
            while (numbers.Count < 4)
                numbers.Add(0);
            var have = new Version(point.Version.Major, point.Version.Minor, Math.Max(point.Version.Build, 0), Math.Max(point.Version.Revision, 0));
            return have >= new Version(Math.Max(numbers[0], 0), Math.Max(numbers[1], 0), Math.Max(numbers[2], 0), Math.Max(numbers[3], 0));
        }
        switch (call.Kind)
        {
            case EntryCallKind.IsOSPlatform:
                return arguments.Length == 1 && arguments[0] is string named ? Is(named) : null;
            case EntryCallKind.IsOSPlatformVersionAtLeast:
                if (arguments.Length < 2 || arguments[0] is not string platform || arguments.Skip(1).Any(a => a is not long))
                    return null;
                return Is(platform) && AtLeast(arguments.Skip(1));
            case EntryCallKind.IsPlatform:
                return Is(call.Platform!);
            case EntryCallKind.IsPlatformVersionAtLeast:
                if (arguments.Any(a => a is not long))
                    return null;
                return Is(call.Platform!) && AtLeast(arguments);
            default:
                return null;
        }
    }

    internal enum EntryCallKind { Other, IsOSPlatform, IsOSPlatformVersionAtLeast, IsPlatform, IsPlatformVersionAtLeast, NewPlatformNotSupported, ObjectConstructor }

    internal readonly record struct EntryCall(EntryCallKind Kind, int ArgumentCount, string? Platform = null);

    private sealed class PlatformNotSupported
    {
        public static readonly PlatformNotSupported Instance = new();
    }

    /// <summary>A value the run does not model; a branch on it stops the run.</summary>
    private sealed class Unknown
    {
        public static readonly Unknown Instance = new();
    }

    private sealed class Method
    {
        public required MethodDefinitionHandle Handle { get; init; }
        public required string Display { get; init; }
        public required string RawName { get; init; }
        public required TypeDefinitionHandle DeclaringType { get; init; }
        public required bool Generated { get; init; }
        public ImmutableArray<IlInstruction> Instructions { get; set; } = ImmutableArray<IlInstruction>.Empty;
        /// <summary>The IL ranges [start, end) of the body's try blocks.</summary>
        public ImmutableArray<(int Start, int End)> TryBlocks { get; set; } = ImmutableArray<(int Start, int End)>.Empty;
        /// <summary>The methods whose code runs this one where it stands (calls), and those that take its address.</summary>
        public HashSet<MethodDefinitionHandle> Callers { get; } = new();
        public HashSet<MethodDefinitionHandle> AddressTakenBy { get; } = new();
    }

    private sealed class State(PEReader pe, MetadataReader md)
    {
        private readonly Dictionary<MethodDefinitionHandle, Method> _methods = new();
        private readonly Dictionary<string, TypeDefinitionHandle> _typesByName = new(StringComparer.Ordinal);
        private readonly List<OsFloorViolation> _violations = new();
        private readonly List<string> _errors = new();

        public OsFloorGuardResult Run(Func<string, string, OsFloorHazard?> hazardOf)
        {
            foreach (var typeHandle in md.TypeDefinitions)
            {
                _typesByName[SerializedName(typeHandle)] = typeHandle;
                var generatedType = IsGeneratedType(typeHandle);
                foreach (var handle in md.GetTypeDefinition(typeHandle).GetMethods())
                {
                    var raw = md.GetString(md.GetMethodDefinition(handle).Name);
                    _methods[handle] = new Method
                    {
                        Handle = handle,
                        RawName = raw,
                        Display = SerializedName(typeHandle) + "::" + raw,
                        DeclaringType = typeHandle,
                        Generated = generatedType || raw.StartsWith('<'),
                    };
                }
            }
            foreach (var method in _methods.Values)
                Read(method);

            var pinvokes = 0;
            var hazards = new List<OsFloorImport>();
            int refusing = 0, coveredPrivate = 0;
            foreach (var import in _methods.Values.Where(IsImport).OrderBy(m => m.Display, StringComparer.Ordinal))
            {
                pinvokes++;
                var definition = md.GetMethodDefinition(import.Handle);
                var info = definition.GetImport();
                if (info.Module.IsNil)
                    continue;
                var library = md.GetString(md.GetModuleReference(info.Module).Name);
                var entryPoint = info.Name.IsNil ? import.RawName : md.GetString(info.Name);
                if (string.IsNullOrEmpty(entryPoint))
                    entryPoint = import.RawName;
                if (hazardOf(library, entryPoint) is not { } hazard)
                    continue;

                var leaves = LeavesOf(import, hazard);
                hazards.Add(new OsFloorImport(import.Display, hazard, leaves.Select(l => l.Display).ToImmutableArray()));
                foreach (var leaf in leaves)
                {
                    if (IsInitializer(leaf))
                    {
                        _violations.Add(new OsFloorViolation(InitializerRule, leaf.Display,
                            $"calls {entryPoint} ({hazard.Reason}), and an initializer or finalizer cannot refuse before it runs"));
                        continue;
                    }
                    var unrefused = new List<string>();
                    var covered = false;
                    string? stoppedAt = null;
                    foreach (var point in hazard.RefuseOn)
                    {
                        if (Refuses(leaf, point, out var stop))
                            continue;
                        if (IsCoveredByCallers(leaf, point, new HashSet<MethodDefinitionHandle>()))
                        {
                            covered = true;
                            continue;
                        }
                        unrefused.Add(point.ToString());
                        stoppedAt ??= stop;
                    }
                    if (unrefused.Count > 0)
                        _violations.Add(new OsFloorViolation(UnguardedRule, leaf.Display,
                            $"calls {entryPoint} in {library} ({hazard.Reason}) and does not refuse first on {string.Join(" or ", unrefused)}: " +
                            $"its entry runs to {stoppedAt} without throwing PlatformNotSupportedException" +
                            (IsPrivate(leaf) ? ", and not every caller refuses either" : "")));
                    else if (covered)
                        coveredPrivate++;
                    else
                        refusing++;
                }
            }

            return new OsFloorGuardResult(
                md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "",
                pinvokes,
                hazards.ToImmutableArray(),
                refusing,
                coveredPrivate,
                _violations.Distinct().OrderBy(v => v.Rule, StringComparer.Ordinal).ThenBy(v => v.Member, StringComparer.Ordinal).ToImmutableArray(),
                _errors.ToImmutableArray());
        }

        /// <summary>
        /// The methods holding a call to <paramref name="import"/>: its callers, with its <c>LibraryImport</c>
        /// stub and every compiler-generated method replaced by their own callers.
        /// </summary>
        private List<Method> LeavesOf(Method import, OsFloorHazard hazard)
        {
            var leaves = new List<Method>();
            var seen = new HashSet<MethodDefinitionHandle> { import.Handle };
            var queue = new Queue<Method>();
            if (IsVisible(import))
                _violations.Add(new OsFloorViolation(ExposedRule, import.Display,
                    $"code outside the assembly can call {hazard.EntryPoint} ({hazard.Reason}) with no guard before it"));
            foreach (var caller in import.Callers.Concat(import.AddressTakenBy))
                queue.Enqueue(_methods[caller]);
            while (queue.Count > 0)
            {
                var method = queue.Dequeue();
                if (!seen.Add(method.Handle))
                    continue;
                if (IsStubOf(method, import) || method.Generated)
                {
                    if (IsStubOf(method, import) && IsVisible(method))
                        _violations.Add(new OsFloorViolation(ExposedRule, method.Display,
                            $"code outside the assembly can call {hazard.EntryPoint} ({hazard.Reason}) through this stub with no guard before it"));
                    var referrers = method.Callers.Concat(method.AddressTakenBy).Where(h => h != method.Handle).ToList();
                    if (referrers.Count == 0 && method.Generated)
                        _violations.Add(new OsFloorViolation(UnattributedRule, method.Display,
                            $"calls {hazard.EntryPoint} ({hazard.Reason}), and no method of the assembly creates it"));
                    foreach (var referrer in referrers)
                        queue.Enqueue(_methods[referrer]);
                    continue;
                }
                leaves.Add(method);
            }
            return leaves;
        }

        /// <summary>
        /// Whether a private <paramref name="method"/> is reached only from methods that refuse on
        /// <paramref name="point"/> (or are themselves so covered). A method whose address is taken can
        /// run from anywhere the delegate goes, so it is never covered this way.
        /// </summary>
        private bool IsCoveredByCallers(Method method, OsFloorPoint point, HashSet<MethodDefinitionHandle> visiting)
        {
            if (!IsPrivate(method) || method.AddressTakenBy.Count > 0 || method.Callers.Count == 0 || !visiting.Add(method.Handle))
                return false;
            foreach (var handle in method.Callers)
            {
                var caller = _methods[handle];
                if (caller.Generated)
                {
                    // A lambda or local function calling the method runs inside its creator.
                    if (!caller.Callers.Concat(caller.AddressTakenBy).Any()
                        || !caller.Callers.Concat(caller.AddressTakenBy).All(h => Covers(_methods[h], point, visiting)))
                        return false;
                    continue;
                }
                if (!Covers(caller, point, visiting))
                    return false;
            }
            return true;
        }

        private bool Covers(Method caller, OsFloorPoint point, HashSet<MethodDefinitionHandle> visiting) =>
            !IsInitializer(caller) && (Refuses(caller, point, out _) || IsCoveredByCallers(caller, point, visiting));

        private bool Refuses(Method method, OsFloorPoint point, out string stoppedAt)
        {
            if (method.Instructions.IsDefaultOrEmpty)
            {
                stoppedAt = "a method with no IL";
                return false;
            }
            return RunEntry(method.Instructions, point, Classify, token =>
            {
                try
                {
                    return md.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));
                }
                catch (Exception ex) when (ex is BadImageFormatException or ArgumentException)
                {
                    return null;
                }
            }, out stoppedAt, offset => method.TryBlocks.Any(t => offset >= t.Start && offset < t.End)) == EntryOutcome.Throws;
        }

        private EntryCall Classify(IlInstruction instruction)
        {
            var token = MetadataTokens.EntityHandle(instruction.Token);
            if (token.Kind != HandleKind.MemberReference)
                return default;
            var reference = md.GetMemberReference((MemberReferenceHandle)token);
            if (reference.Parent.Kind != HandleKind.TypeReference)
                return default;
            var (ns, type, _) = TypeNames.OfReference(md, (TypeReferenceHandle)reference.Parent);
            var name = md.GetString(reference.Name);
            var parameters = ParameterCount(reference.Signature);
            if (ns != "System")
                return default;
            if (instruction.OpCode == ILOpCode.Newobj)
                return type == "PlatformNotSupportedException" && name == ".ctor" ? new EntryCall(EntryCallKind.NewPlatformNotSupported, parameters) : default;
            // A constructor's base call, when the base is object: it runs nothing.
            if (type == "Object" && name == ".ctor" && parameters == 0)
                return new EntryCall(EntryCallKind.ObjectConstructor, 1);
            if (type != "OperatingSystem")
                return default;
            if (name == "IsOSPlatform")
                return new EntryCall(EntryCallKind.IsOSPlatform, parameters);
            if (name == "IsOSPlatformVersionAtLeast")
                return new EntryCall(EntryCallKind.IsOSPlatformVersionAtLeast, parameters);
            foreach (var (prefix, platform) in new[] { ("IsMacCatalyst", "maccatalyst"), ("IsIOS", "ios"), ("IsTvOS", "tvos"), ("IsMacOS", "macos"), ("IsWatchOS", "watchos") })
            {
                if (name == prefix)
                    return new EntryCall(EntryCallKind.IsPlatform, parameters, platform);
                if (name == prefix + "VersionAtLeast")
                    return new EntryCall(EntryCallKind.IsPlatformVersionAtLeast, parameters, platform);
            }
            return default;
        }

        private int ParameterCount(BlobHandle signature)
        {
            var reader = md.GetBlobReader(signature);
            var header = reader.ReadSignatureHeader();
            if (header.IsGeneric)
                reader.ReadCompressedInteger();
            return reader.ReadCompressedInteger();
        }

        private void Read(Method method)
        {
            var definition = md.GetMethodDefinition(method.Handle);
            if (StateMachineOf(definition) is { } moveNext)
                _methods[moveNext].Callers.Add(method.Handle);
            if (definition.RelativeVirtualAddress == 0)
                return;
            MethodBodyBlock body;
            try
            {
                body = pe.GetMethodBody(definition.RelativeVirtualAddress);
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException)
            {
                _errors.Add($"{method.Display}: method body unreadable: {ex.Message}");
                return;
            }
            var decoded = IlDecoder.Decode(body);
            if (decoded.Error is { } error)
            {
                _errors.Add($"{method.Display}: IL does not decode at IL_{error.Offset:X4}: {error.Message}");
                return;
            }
            method.Instructions = decoded.Instructions;
            method.TryBlocks = body.ExceptionRegions.Select(r => (r.TryOffset, r.TryOffset + r.TryLength)).ToImmutableArray();
            foreach (var instruction in decoded.Instructions)
            {
                if (instruction.OpCode is not (ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp))
                    continue;
                foreach (var target in Resolve(MetadataTokens.EntityHandle(instruction.Token)))
                {
                    if (!_methods.TryGetValue(target, out var callee))
                        continue;
                    if (instruction.OpCode is ILOpCode.Ldftn or ILOpCode.Ldvirtftn)
                        callee.AddressTakenBy.Add(method.Handle);
                    else
                        callee.Callers.Add(method.Handle);
                }
            }
        }

        /// <summary>
        /// The methods of this assembly <paramref name="token"/> names; a member reference on a generic
        /// instantiation of one of its types resolves by name, to every method of that name when no
        /// signature matches exactly.
        /// </summary>
        private IEnumerable<MethodDefinitionHandle> Resolve(EntityHandle token)
        {
            switch (token.Kind)
            {
                case HandleKind.MethodDefinition:
                    return new[] { (MethodDefinitionHandle)token };
                case HandleKind.MethodSpecification:
                    return Resolve(md.GetMethodSpecification((MethodSpecificationHandle)token).Method);
                case HandleKind.MemberReference:
                {
                    var reference = md.GetMemberReference((MemberReferenceHandle)token);
                    if (reference.GetKind() != MemberReferenceKind.Method)
                        return Array.Empty<MethodDefinitionHandle>();
                    if (reference.Parent.Kind == HandleKind.MethodDefinition)
                        return new[] { (MethodDefinitionHandle)reference.Parent };
                    if (TypeOf(reference.Parent) is not { } type)
                        return Array.Empty<MethodDefinitionHandle>();
                    var name = md.GetString(reference.Name);
                    var signature = md.GetBlobBytes(reference.Signature);
                    var named = md.GetTypeDefinition(type).GetMethods().Where(h => md.GetString(md.GetMethodDefinition(h).Name) == name).ToList();
                    var exact = named.Where(h => md.GetBlobBytes(md.GetMethodDefinition(h).Signature).AsSpan().SequenceEqual(signature)).ToList();
                    return exact.Count > 0 ? exact : named;
                }
                default:
                    return Array.Empty<MethodDefinitionHandle>();
            }
        }

        private TypeDefinitionHandle? TypeOf(EntityHandle handle)
        {
            switch (handle.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (TypeDefinitionHandle)handle;
                case HandleKind.TypeReference:
                {
                    var (ns, name, scope) = TypeNames.OfReference(md, (TypeReferenceHandle)handle);
                    if (scope.Length > 0)
                        return null;
                    return _typesByName.TryGetValue((ns.Length > 0 ? ns + "." : "") + name.Replace('/', '+'), out var local) ? local : null;
                }
                case HandleKind.TypeSpecification:
                {
                    var reader = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
                    if (reader.ReadSignatureTypeCode() != SignatureTypeCode.GenericTypeInstance)
                        return null;
                    reader.ReadSignatureTypeCode();
                    return TypeOf(reader.ReadTypeHandle());
                }
                default:
                    return null;
            }
        }

        /// <summary>The <c>MoveNext</c> of the state machine a C# async method or iterator creates, which runs its body.</summary>
        private MethodDefinitionHandle? StateMachineOf(MethodDefinition definition)
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
                    _errors.Add($"a state machine attribute does not decode: {ex.Message}");
                    continue;
                }
                var comma = typeName?.IndexOf(',') ?? -1;
                if (comma >= 0)
                    typeName = typeName![..comma].Trim();
                if (typeName is null || !_typesByName.TryGetValue(typeName, out var type))
                    continue;
                foreach (var method in md.GetTypeDefinition(type).GetMethods())
                    if (md.GetString(md.GetMethodDefinition(method).Name) == "MoveNext")
                        return method;
            }
            return null;
        }

        private string? AttributeName(CustomAttribute attribute)
        {
            EntityHandle type;
            if (attribute.Constructor.Kind == HandleKind.MemberReference)
                type = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
            else if (attribute.Constructor.Kind == HandleKind.MethodDefinition)
                type = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
            else
                return null;
            if (type.Kind == HandleKind.TypeReference)
            {
                var (ns, name, _) = TypeNames.OfReference(md, (TypeReferenceHandle)type);
                return ns.Length > 0 ? ns + "." + name : name;
            }
            if (type.Kind == HandleKind.TypeDefinition)
                return SerializedName((TypeDefinitionHandle)type);
            return null;
        }

        private bool IsImport(Method method) =>
            (md.GetMethodDefinition(method.Handle).Attributes & MethodAttributes.PinvokeImpl) != 0;

        /// <summary>A <c>LibraryImport</c> stub: the method whose local function is the import that carries its name.</summary>
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

        private bool IsPrivate(Method method) =>
            (md.GetMethodDefinition(method.Handle).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Private;

        /// <summary>
        /// Whether code outside the assembly can call <paramref name="method"/>: public or protected in a
        /// type that is, or an override or interface implementation.
        /// </summary>
        private bool IsVisible(Method method)
        {
            var attributes = md.GetMethodDefinition(method.Handle).Attributes;
            if ((attributes & MethodAttributes.Virtual) != 0 && (attributes & MethodAttributes.NewSlot) == 0)
                return true;
            var access = attributes & MethodAttributes.MemberAccessMask;
            if (access is not (MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem))
                return false;
            for (var type = method.DeclaringType; !type.IsNil; type = md.GetTypeDefinition(type).GetDeclaringType())
            {
                var visibility = md.GetTypeDefinition(type).Attributes & TypeAttributes.VisibilityMask;
                if (visibility is not (TypeAttributes.Public or TypeAttributes.NestedPublic or TypeAttributes.NestedFamily or TypeAttributes.NestedFamORAssem))
                    return false;
            }
            return true;
        }

        private bool IsGeneratedType(TypeDefinitionHandle handle)
        {
            for (var current = handle; !current.IsNil; current = md.GetTypeDefinition(current).GetDeclaringType())
                if (md.GetString(md.GetTypeDefinition(current).Name).StartsWith('<'))
                    return true;
            return false;
        }

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
    }

    private sealed class TypeNameProvider : ICustomAttributeTypeProvider<string>
    {
        public static readonly TypeNameProvider Instance = new();
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
        public string GetSystemType() => "System.Type";
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeDefinition(handle).Name);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            reader.GetString(reader.GetTypeReference(handle).Name);
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string type) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string type) => type is "System.Type" or "Type";
    }
}
