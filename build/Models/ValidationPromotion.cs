// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>An invocation-local candidate. No cached candidate can authorize a write.</summary>
public sealed class ValidationPromotion
{
    readonly string path;
    readonly byte[] startingHash;
    readonly Dictionary<string, ValidationBaseline.LibraryResult> results;
    readonly HashSet<string> receipts = new(StringComparer.Ordinal);
    readonly string[] requiredReceipts;
    public bool Eligible { get; }

    public ValidationPromotion(string path, bool eligible,
        IDictionary<string, ValidationBaseline.LibraryResult> results, params string[] requiredReceipts)
    {
        this.path = path;
        startingHash = SHA256.HashData(File.ReadAllBytes(path));
        Eligible = eligible;
        this.results = new(results, StringComparer.Ordinal);
        this.requiredReceipts = requiredReceipts.ToArray();
    }

    public void Record(string receipt) => receipts.Add(receipt);
    public void UpdateResults(IDictionary<string, ValidationBaseline.LibraryResult> current)
    {
        results.Clear();
        foreach (var (name, result) in current) results.Add(name, result);
    }

    public void WriteInspection(string inspectionPath) => File.WriteAllText(inspectionPath,
        JsonSerializer.Serialize(new { diagnosticOnly = true, baselinePath = path,
            startingHash = Convert.ToHexString(startingHash), eligible = Eligible,
            requiredReceipts, results }, new JsonSerializerOptions { WriteIndented = true }));

    public bool Promote()
    {
        if (!Eligible || !requiredReceipts.All(receipts.Contains)) return false;
        // An exclusive handle prevents other harness promotions from interleaving the read/merge.
        // Compare the original bytes, including fields owned by unrelated gates.
        using var guard = new FileStream(path + ".promotion-lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var original = File.ReadAllBytes(path);
        if (!SHA256.HashData(original).SequenceEqual(startingHash))
            throw new InvalidDataException("Validation baseline changed during this invocation; candidate retained");
        var root = JsonNode.Parse(original)!.AsObject();
        var libraries = root["compile_gate"]?["libraries"]?.AsObject()
            ?? throw new InvalidDataException("Baseline has no compile_gate.libraries");
        foreach (var (name, result) in results)
        {
            var entry = libraries[name]?.AsObject();
            if (entry == null)
            {
                if (result.WithdrawalPolicy is not { SchemaVersion: 1, Exceptions.Length: 0 })
                    throw new InvalidDataException($"Target {name} requires validated zero enrollment");
                entry = new JsonObject();
                libraries[name] = entry;
            }
            entry["compile"] = result.Compile;
            entry["errors"] = result.Errors;
            entry["lines"] = result.Lines;
            entry["dep_compile"] = result.DepCompile;
            entry["swift_compile"] = result.SwiftCompile;
            if (entry["withdrawal_policy"] == null && result.WithdrawalPolicy is { SchemaVersion: 1, Exceptions.Length: 0 } enrollment)
                entry["withdrawal_policy"] = JsonSerializer.SerializeToNode(enrollment);
            // Exception retirement needs public/accessor/route proof. The policy is deliberately
            // preserved until that proof is joined, even when the candidate set is smaller.
        }
        var temporary = path + ".candidate-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, root.ToJsonString(ValidationBaseline.WriteOptions) + Environment.NewLine);
            if (!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(startingHash))
                throw new InvalidDataException("Concurrent baseline modification before promotion");
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
