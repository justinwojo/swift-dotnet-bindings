// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// A speculative emission of one member across both output buffers, undone as a unit when a
    /// contract trips mid-emission.
    /// <para>A member is written into the C# buffer and the Swift wrapper buffer by the same call,
    /// so rolling back only the C# side leaves the Swift side holding a wrapper block whose
    /// managed counterpart was dropped. This pairs the two.</para>
    /// <para>The Swift side cannot be rolled back unconditionally. Emitting a member can also
    /// commit <em>module-shared</em> Swift helpers — the <c>SBW_Utf8Slice</c> struct, the
    /// closure-context box helpers, typed-error extractors — which are written into the same
    /// buffer span and simultaneously recorded in <see cref="ModuleEmissionContext"/> so no later
    /// member re-emits them. Those registrations have no undo, so truncating that span would
    /// delete a definition nothing will write again and break every later member that refers to
    /// it: a wrapper source that does not compile at all, which is worse than the orphaned block
    /// the pairing exists to prevent. So the Swift rollback is gated on
    /// <see cref="ModuleEmissionContext.SharedSwiftArtifactEpoch"/> being unchanged across the
    /// transaction — proof that every Swift byte in the span belongs to this member alone. When
    /// the epoch moved, the Swift text is kept: a complete, uncalled <c>@_cdecl</c> wrapper is
    /// dead code that still compiles, so keeping it is the safe direction.</para>
    /// </summary>
    public readonly struct MemberEmissionTransaction
    {
        private readonly CSharpWriter _csWriter;
        private readonly BufferedSourceWriter.WriterCheckpoint _csCheckpoint;
        private readonly SwiftWriter? _swiftWriter;
        private readonly BufferedSourceWriter.WriterCheckpoint _swiftCheckpoint;
        private readonly ModuleEmissionContext? _context;
        private readonly int _epochAtBegin;

        private MemberEmissionTransaction(
            CSharpWriter csWriter,
            BufferedSourceWriter.WriterCheckpoint csCheckpoint,
            SwiftWriter? swiftWriter,
            BufferedSourceWriter.WriterCheckpoint swiftCheckpoint,
            ModuleEmissionContext? context,
            int epochAtBegin)
        {
            _csWriter = csWriter;
            _csCheckpoint = csCheckpoint;
            _swiftWriter = swiftWriter;
            _swiftCheckpoint = swiftCheckpoint;
            _context = context;
            _epochAtBegin = epochAtBegin;
        }

        /// <summary>
        /// Captures both buffer positions and the shared-helper epoch before a member is emitted
        /// speculatively.
        /// <para><paramref name="context"/> MUST be the same instance the emitters write through
        /// (<c>context.GetEmissionContext()</c>, which every <c>WrapperEmitter</c> path in
        /// <c>MethodHandler</c> is already required to thread). Passing a different instance is
        /// not a fail-safe degradation — it is unsound in the dangerous direction: the epoch is
        /// read and re-read on that same unrelated instance, so it never moves, and the rollback
        /// is reported safe while helpers committed through the real context get truncated. A
        /// <see langword="null"/> context is the fail-safe spelling for "no epoch available"; it
        /// keeps the Swift text.</para>
        /// </summary>
        public static MemberEmissionTransaction Begin(
            CSharpWriter csWriter,
            SwiftWriter? swiftWriter,
            ModuleEmissionContext? context)
        {
            ArgumentNullException.ThrowIfNull(csWriter);

            return new MemberEmissionTransaction(
                csWriter,
                csWriter.Checkpoint(),
                swiftWriter,
                swiftWriter is null ? default : swiftWriter.Checkpoint(),
                context,
                context?.SharedSwiftArtifactEpoch ?? 0);
        }

        /// <summary>
        /// Whether the Swift span written since <see cref="Begin"/> is provably member-private —
        /// a Swift writer was supplied, its emission context is known, and no module-shared Swift
        /// helper was committed in between.
        /// </summary>
        public bool SwiftRollbackIsSafe => SwiftKeepReason == SwiftKeep.RolledBack;

        /// <summary>
        /// Why a transaction kept the Swift span instead of discarding it. Read from
        /// <see cref="SwiftKeepReason"/> this is a prediction; returned from
        /// <see cref="Rollback"/> it is what happened.
        /// </summary>
        public enum SwiftKeep
        {
            /// <summary>The span is provably member-private, so it is safe to discard.</summary>
            RolledBack,

            /// <summary>No Swift writer took part in this member's emission.</summary>
            NoSwiftWriter,

            /// <summary>No emission context, so no epoch could be compared.</summary>
            NoEmissionContext,

            /// <summary>A module-shared Swift helper was committed inside the span.</summary>
            SharedHelperCommitted,
        }

        /// <summary>
        /// The reason the Swift span was (or would be) kept. Distinguishes the "cannot prove it is
        /// safe" cases from the one case where shared helper text is genuinely sitting in the span,
        /// so the diagnostic on the keep path does not blame a cause that did not occur.
        /// </summary>
        public SwiftKeep SwiftKeepReason =>
            _swiftWriter is null ? SwiftKeep.NoSwiftWriter
            : _context is null ? SwiftKeep.NoEmissionContext
            : _context.SharedSwiftArtifactEpoch != _epochAtBegin ? SwiftKeep.SharedHelperCommitted
            : SwiftKeep.RolledBack;

        /// <summary>
        /// Discards everything written since <see cref="Begin"/>: the C# span always, and the
        /// Swift span when <see cref="SwiftRollbackIsSafe"/>. Returns why the Swift span was kept,
        /// so a caller can report the member whose wrapper block had to be retained.
        /// </summary>
        public SwiftKeep Rollback()
        {
            _csWriter.RollbackTo(_csCheckpoint);

            var reason = SwiftKeepReason;
            if (reason == SwiftKeep.RolledBack)
            {
                _swiftWriter!.RollbackTo(_swiftCheckpoint);
            }

            return reason;
        }
    }
}
