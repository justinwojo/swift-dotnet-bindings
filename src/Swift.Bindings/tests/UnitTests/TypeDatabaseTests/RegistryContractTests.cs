// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Finding 47: the type registry's write contract. Pins (a) the explicit
    /// <see cref="ConflictPolicy"/> on the write primitive (first-wins vs last-wins, named at the
    /// call site instead of implied by surrounding code), (b) the Session-6 collision observability
    /// (SWIFTBIND024) folded into that primitive, (c) the post-finalization freeze point that makes
    /// a structural write after the bound module is registered a hard, observable contract violation
    /// (SWIFTBIND045), and (d) <c>ApplyEmissionResult</c> / <see cref="TypeEmissionResult"/> as the
    /// one sanctioned post-freeze mutation channel for emission-discovered facts.
    /// </summary>
    public class RegistryContractTests
    {
        private static SwiftTypeName Name(string moduleQualified) =>
            SwiftTypeName.FromModuleQualifiedName(moduleQualified);

        private static TypeRecord Record(SwiftTypeName name, TypeRecordKind kind, string accessor = "acc") =>
            new TypeRecord
            {
                CSharpTypeName = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", name.Name),
                SwiftTypeName = name,
                MetadataAccessor = accessor,
                Flags = TypeRecordFlags.None,
                Kind = kind,
            };

        // ---- ConflictPolicy: intent named at the call site ----

        [Fact]
        public void Register_KeepExisting_FirstWins()
        {
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");

            module.Register(name, Record(name, TypeRecordKind.Struct, "first"), ConflictPolicy.KeepExisting);
            module.Register(name, Record(name, TypeRecordKind.Class, "second"), ConflictPolicy.KeepExisting);

            Assert.True(module.TryGetTypeRecord(name, out var stored));
            Assert.Equal("first", stored!.MetadataAccessor);
            Assert.Equal(TypeRecordKind.Struct, stored.Kind);
        }

        [Fact]
        public void Register_Overwrite_LastWins()
        {
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");

            module.Register(name, Record(name, TypeRecordKind.Struct, "first"), ConflictPolicy.Overwrite);
            module.Register(name, Record(name, TypeRecordKind.Class, "second"), ConflictPolicy.Overwrite);

            Assert.True(module.TryGetTypeRecord(name, out var stored));
            Assert.Equal("second", stored!.MetadataAccessor);
            Assert.Equal(TypeRecordKind.Class, stored.Kind);
        }

        [Fact]
        public void RegisterType_Convenience_DefaultsToOverwrite()
        {
            // The back-compat convenience overload must behave as the historical unconditional
            // last-write-wins so the many existing registration/test sites keep their semantics.
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");

            module.RegisterType(name, Record(name, TypeRecordKind.Struct, "first"));
            module.RegisterType(name, Record(name, TypeRecordKind.Class, "second"));

            Assert.True(module.TryGetTypeRecord(name, out var stored));
            Assert.Equal("second", stored!.MetadataAccessor);
        }

        // ---- SWIFTBIND024 collision observability ----

        [Fact]
        public void Register_Overwrite_KindChange_LogsSwiftbind024()
        {
            var logger = new ListLogger();
            var module = new ModuleTypeDatabase("M", "/p", logger);
            var name = Name("M.T");

            module.Register(name, Record(name, TypeRecordKind.Struct), ConflictPolicy.Overwrite);
            module.Register(name, Record(name, TypeRecordKind.Class), ConflictPolicy.Overwrite);

            Assert.Contains(logger.Entries, e => e.Message.Contains("SWIFTBIND024"));
        }

        [Fact]
        public void Register_KeepExisting_DroppedDifferingWrite_LogsSwiftbind024()
        {
            var logger = new ListLogger();
            var module = new ModuleTypeDatabase("M", "/p", logger);
            var name = Name("M.T");

            module.Register(name, Record(name, TypeRecordKind.Struct), ConflictPolicy.KeepExisting);
            module.Register(name, Record(name, TypeRecordKind.Class), ConflictPolicy.KeepExisting);

            Assert.Contains(logger.Entries, e => e.Message.Contains("SWIFTBIND024"));
        }

        [Fact]
        public void Register_FirstWriteForKey_DoesNotLog()
        {
            var logger = new ListLogger();
            var module = new ModuleTypeDatabase("M", "/p", logger);
            var name = Name("M.A");

            module.Register(name, Record(name, TypeRecordKind.Struct), ConflictPolicy.Overwrite);

            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("SWIFTBIND024"));
        }

        // ---- Freeze point / SWIFTBIND045 ----

        [Fact]
        public void Freeze_ThenRegister_ThrowsSwiftbind045()
        {
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");
            module.Freeze();

            var ex = Assert.Throws<InvalidOperationException>(
                () => module.Register(name, Record(name, TypeRecordKind.Struct), ConflictPolicy.Overwrite));
            Assert.Contains("SWIFTBIND045", ex.Message);
        }

        [Fact]
        public void Freeze_ThenRegisterTypeConvenience_ThrowsSwiftbind045()
        {
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");
            module.Freeze();

            var ex = Assert.Throws<InvalidOperationException>(
                () => module.RegisterType(name, Record(name, TypeRecordKind.Struct)));
            Assert.Contains("SWIFTBIND045", ex.Message);
        }

        [Fact]
        public void Freeze_IsIdempotent_AndReflectedByIsFrozen()
        {
            var module = new ModuleTypeDatabase("M", "/p");

            Assert.False(module.IsFrozen);
            module.Freeze();
            module.Freeze(); // idempotent — no throw
            Assert.True(module.IsFrozen);
        }

        [Fact]
        public void TypeDatabase_Freeze_PropagatesToModuleDatabases()
        {
            var db = new TypeDatabase();
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");
            module.RegisterType(name, Record(name, TypeRecordKind.Struct));
            db.AddModuleDatabase(module);

            db.Freeze();

            Assert.True(module.IsFrozen);
        }

        [Fact]
        public void TypeDatabase_Freeze_ThenUpdateTypeRecord_ThrowsSwiftbind045()
        {
            var db = new TypeDatabase();
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");
            module.RegisterType(name, Record(name, TypeRecordKind.Struct));
            db.AddModuleDatabase(module);
            db.Freeze();

            var ex = Assert.Throws<InvalidOperationException>(
                () => db.UpdateTypeRecord(name, Record(name, TypeRecordKind.Class)));
            Assert.Contains("SWIFTBIND045", ex.Message);
        }

        // ---- TypeEmissionResult: null-means-unchanged delta ----

        [Fact]
        public void TypeEmissionResult_ApplyTo_AppliesSetFacts_PreservesUnset()
        {
            var name = Name("M.T");
            var existing = Record(name, TypeRecordKind.Class) with
            {
                EmittedMemberCount = 3,
                EmittedMetadataPInvoke = false,
            };

            // Only EmittedMemberCount is set on the delta; the rest must come from `existing`.
            var updated = new TypeEmissionResult { EmittedMemberCount = 7 }.ApplyTo(existing);

            Assert.Equal(7, updated.EmittedMemberCount);
            Assert.Equal((bool?)false, updated.EmittedMetadataPInvoke);
            Assert.Equal(existing.CSharpTypeName, updated.CSharpTypeName);
        }

        [Fact]
        public void TypeEmissionResult_ApplyTo_CSharpTypeNameFact_RefinesNameOnly()
        {
            var name = Name("M.T");
            var existing = Record(name, TypeRecordKind.Class) with { EmittedMemberCount = 4 };
            var renamed = CSharpTypeName.FromNamespaceAndName("BindingsGeneration.Tests", "TType");

            var updated = new TypeEmissionResult { CSharpTypeName = renamed }.ApplyTo(existing);

            Assert.Equal("TType", updated.CSharpTypeName.Name);
            Assert.Equal(4, updated.EmittedMemberCount); // structural-ish facts untouched
            Assert.Equal(existing.SwiftTypeName, updated.SwiftTypeName);
        }

        // ---- ApplyEmissionResult: the sanctioned post-freeze stamp ----

        [Fact]
        public void ApplyEmissionResult_StampsFactsPostFreeze_OnModuleRecord()
        {
            var db = new TypeDatabase();
            var module = new ModuleTypeDatabase("M", "/p");
            var name = Name("M.T");
            module.RegisterType(name, Record(name, TypeRecordKind.Class));
            db.AddModuleDatabase(module);
            db.Freeze();

            // Sanctioned even though the registry is frozen.
            db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 5 });

            Assert.True(db.TryGetTypeRecord(name, out var stored));
            Assert.Equal(5, stored!.EmittedMemberCount);
        }

        [Fact]
        public void ApplyEmissionResult_StampsFactsPostFreeze_OnOutOfModuleRecord()
        {
            var db = new TypeDatabase();
            db.AddModuleDatabase(new ModuleTypeDatabase("M", "/p"));
            var name = Name("Other.T");
            db.AddOutOfModuleTypes(new[] { (name, Record(name, TypeRecordKind.Struct)) });
            db.Freeze();

            db.ApplyEmissionResult(name, new TypeEmissionResult { EmittedMemberCount = 9 });

            Assert.True(db.TryGetTypeRecord(name, out var stored));
            Assert.Equal(9, stored!.EmittedMemberCount);
        }

        [Fact]
        public void ApplyEmissionResult_UnknownType_IsNoOp()
        {
            // Emission only ever refines an already-registered identity; an unknown key has nothing
            // to stamp and must not introduce a new record.
            var db = new TypeDatabase();
            db.AddModuleDatabase(new ModuleTypeDatabase("M", "/p"));
            db.Freeze();

            db.ApplyEmissionResult(Name("M.Ghost"), new TypeEmissionResult { EmittedMemberCount = 1 });

            Assert.False(db.TryGetTypeRecord(Name("M.Ghost"), out _));
        }

        private sealed class ListLogger : ILogger
        {
            public List<(LogLevel Level, string Message)> Entries { get; } = new();

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
