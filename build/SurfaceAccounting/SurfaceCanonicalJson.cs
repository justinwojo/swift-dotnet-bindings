// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

#pragma warning disable IL2026
#pragma warning disable IL3050

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

public static class SurfaceCanonicalJson
{
    public static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
    };

    public static string Hash<T>(T value)
    {
        var json = JsonSerializer.SerializeToNode(value, CompactOptions)
            ?? throw new InvalidOperationException("Cannot canonicalize a null JSON value.");
        return Sha256(Encoding.UTF8.GetBytes(Canonicalize(json)));
    }

    public static string Serialize<T>(T value, bool indented = true)
    {
        var options = indented ? OutputOptions : CompactOptions;
        return JsonSerializer.Serialize(value, options);
    }

    public static string Canonicalize(JsonNode node)
    {
        var normalized = Sort(node);
        return normalized.ToJsonString(CompactOptions);
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static JsonDocument ParseStrictFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return ParseStrict(bytes, path);
    }

    public static JsonDocument ParseStrict(byte[] utf8, string source)
    {
        ValidateNoDuplicateProperties(utf8, source);
        return JsonDocument.Parse(utf8);
    }

    public static T DeserializeStrict<T>(string path)
    {
        using var document = ParseStrictFile(path);
        return document.RootElement.Deserialize<T>(OutputOptions)
            ?? throw new InvalidDataException($"{path}: JSON deserialized to null.");
    }

    private static JsonNode Sort(JsonNode node)
        => node switch
        {
            JsonObject obj => new JsonObject(obj
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Sort(p.Value)))),
            JsonArray array => new JsonArray(array.Select(v => v is null ? null : Sort(v)).ToArray()),
            _ => node.DeepClone(),
        };

    private static void ValidateNoDuplicateProperties(ReadOnlySpan<byte> utf8, string path)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    objectProperties.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? "";
                    if (objectProperties.Count == 0 || !objectProperties.Peek().Add(name))
                        throw new InvalidDataException($"{path}: duplicate JSON property '{name}' at byte {reader.TokenStartIndex}.");
                    break;
            }
        }
    }
}
