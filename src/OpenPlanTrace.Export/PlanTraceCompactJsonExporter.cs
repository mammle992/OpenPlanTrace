using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenPlanTrace.Export;

public sealed record PlanTraceCompactJsonExportOptions
{
    public bool WriteIndented { get; init; }
}

public static class PlanTraceCompactJsonExporter
{
    public const string CurrentSchemaVersion = "openplantrace.scan.compact.v1";

    private const string EncodingName = "shape-string-token-v1";

    public static string Serialize(
        PlanScanResult result,
        PlanTraceCompactJsonExportOptions? options = null) =>
        Serialize(PlanTraceExport.From(result), options);

    public static string Serialize(
        PlanTraceExport export,
        PlanTraceCompactJsonExportOptions? options = null)
    {
        options ??= new PlanTraceCompactJsonExportOptions();

        var sourceJson = JsonSerializer.Serialize(export, CreateSourceJsonOptions());
        using var document = JsonDocument.Parse(sourceJson);
        var encoder = new CompactScanEncoder();
        var data = encoder.Encode(document.RootElement);
        var dictionary = CompactStringDictionary.Create(encoder.Strings);
        var root = CreateRoot(export, sourceJson, encoder, dictionary, data);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = options.WriteIndented
        }))
        {
            root.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static async ValueTask WriteAsync(
        PlanScanResult result,
        Stream stream,
        PlanTraceCompactJsonExportOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var json = Serialize(result, options);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static string ExpandToScanJson(
        string compactJson,
        bool writeIndented = false)
    {
        using var document = JsonDocument.Parse(compactJson);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = writeIndented }))
        {
            ExpandToScanJson(document.RootElement, writer);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static void ExpandToScanJson(
        JsonElement compactRoot,
        Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ValidateEnvelope(compactRoot);
        var dictionary = DecodedCompactStringDictionary.From(compactRoot.GetProperty("dictionary"));
        var data = compactRoot.GetProperty("data");
        WriteDecodedValue(data, dictionary, writer);
    }

    private static JsonObject CreateRoot(
        PlanTraceExport export,
        string sourceJson,
        CompactScanEncoder encoder,
        CompactStringDictionary dictionary,
        JsonNode? data)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["sourceSchemaVersion"] = PlanTraceExport.CurrentSchemaVersion,
            ["encoding"] = EncodingName,
            ["generatedAt"] = export.GeneratedAt,
            ["dictionary"] = new JsonObject
            {
                ["stringPrefixes"] = ToJsonArray(dictionary.Prefixes),
                ["strings"] = dictionary.Entries,
                ["shapes"] = ToShapeArray(encoder.Shapes)
            },
            ["data"] = data,
            ["stats"] = new JsonObject
            {
                ["sourceUtf8Bytes"] = Encoding.UTF8.GetByteCount(sourceJson),
                ["stringCount"] = encoder.Strings.Count,
                ["stringPrefixCount"] = dictionary.Prefixes.Count,
                ["shapeCount"] = encoder.Shapes.Count,
                ["encodedObjectCount"] = encoder.EncodedObjectCount,
                ["encodedArrayCount"] = encoder.EncodedArrayCount,
                ["encodedStringReferenceCount"] = encoder.EncodedStringReferenceCount,
                ["topShapeUseCounts"] = ToShapeUseArray(encoder.ShapeUseCounts)
            }
        };

        return root;
    }

    private static JsonSerializerOptions CreateSourceJsonOptions() =>
        new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array;
    }

    private static JsonArray ToShapeArray(IReadOnlyList<IReadOnlyList<int>> shapes)
    {
        var array = new JsonArray();
        foreach (var shape in shapes)
        {
            var item = new JsonArray();
            foreach (var key in shape)
            {
                item.Add(key);
            }

            array.Add(item);
        }

        return array;
    }

    private static JsonArray ToShapeUseArray(IReadOnlyList<int> shapeUseCounts)
    {
        var ranked = shapeUseCounts
            .Select((count, index) => new { ShapeId = index, Count = count })
            .Where(item => item.Count > 1)
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.ShapeId)
            .Take(16);
        var array = new JsonArray();
        foreach (var item in ranked)
        {
            array.Add(new JsonObject
            {
                ["shapeId"] = item.ShapeId,
                ["useCount"] = item.Count
            });
        }

        return array;
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Compact scan root must be an object.");
        }

        var schemaVersion = root.GetProperty("schemaVersion").GetString();
        if (!string.Equals(schemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unsupported compact scan schemaVersion '{schemaVersion ?? "(missing)"}'. Expected '{CurrentSchemaVersion}'.");
        }

        var sourceSchemaVersion = root.GetProperty("sourceSchemaVersion").GetString();
        if (!string.Equals(sourceSchemaVersion, PlanTraceExport.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unsupported compact scan sourceSchemaVersion '{sourceSchemaVersion ?? "(missing)"}'. Expected '{PlanTraceExport.CurrentSchemaVersion}'.");
        }

        var encoding = root.GetProperty("encoding").GetString();
        if (!string.Equals(encoding, EncodingName, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Unsupported compact scan encoding '{encoding ?? "(missing)"}'. Expected '{EncodingName}'.");
        }

        _ = root.GetProperty("dictionary");
        _ = root.GetProperty("data");
    }

    private static void WriteDecodedValue(
        JsonElement value,
        DecodedCompactStringDictionary dictionary,
        Utf8JsonWriter writer)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                WriteDecodedToken(value, dictionary, writer);
                return;
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                value.WriteTo(writer);
                return;
            default:
                throw new JsonException($"Unsupported compact scan encoded value kind '{value.ValueKind}'.");
        }
    }

    private static void WriteDecodedToken(
        JsonElement token,
        DecodedCompactStringDictionary dictionary,
        Utf8JsonWriter writer)
    {
        if (token.GetArrayLength() == 0
            || token[0].ValueKind != JsonValueKind.Number
            || !token[0].TryGetInt32(out var tag))
        {
            throw new JsonException("Compact scan token arrays must start with a numeric tag.");
        }

        switch (tag)
        {
            case 0:
                WriteDecodedObject(token, dictionary, writer);
                return;
            case 1:
                writer.WriteStartArray();
                foreach (var item in token.EnumerateArray().Skip(1))
                {
                    WriteDecodedValue(item, dictionary, writer);
                }

                writer.WriteEndArray();
                return;
            case 2:
                if (token.GetArrayLength() != 2
                    || token[1].ValueKind != JsonValueKind.Number
                    || !token[1].TryGetInt32(out var stringId))
                {
                    throw new JsonException("Compact scan string token must be [2,stringId].");
                }

                writer.WriteStringValue(dictionary.StringAt(stringId));
                return;
            default:
                throw new JsonException($"Unsupported compact scan token tag '{tag}'.");
        }
    }

    private static void WriteDecodedObject(
        JsonElement token,
        DecodedCompactStringDictionary dictionary,
        Utf8JsonWriter writer)
    {
        if (token.GetArrayLength() < 2
            || token[1].ValueKind != JsonValueKind.Number
            || !token[1].TryGetInt32(out var shapeId))
        {
            throw new JsonException("Compact scan object token must be [0,shapeId,...values].");
        }

        var shape = dictionary.ShapeAt(shapeId);
        if (token.GetArrayLength() != shape.Count + 2)
        {
            throw new JsonException(
                $"Compact scan object token for shape {shapeId} has {token.GetArrayLength() - 2} value(s), expected {shape.Count}.");
        }

        writer.WriteStartObject();
        for (var index = 0; index < shape.Count; index++)
        {
            writer.WritePropertyName(dictionary.StringAt(shape[index]));
            WriteDecodedValue(token[index + 2], dictionary, writer);
        }

        writer.WriteEndObject();
    }

    private sealed class CompactScanEncoder
    {
        private readonly Dictionary<string, int> _stringIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _shapeIds = new(StringComparer.Ordinal);
        private readonly List<string> _strings = new();
        private readonly List<IReadOnlyList<int>> _shapes = new();
        private readonly List<int> _shapeUseCounts = new();

        public IReadOnlyList<string> Strings => _strings;

        public IReadOnlyList<IReadOnlyList<int>> Shapes => _shapes;

        public IReadOnlyList<int> ShapeUseCounts => _shapeUseCounts;

        public int EncodedObjectCount { get; private set; }

        public int EncodedArrayCount { get; private set; }

        public int EncodedStringReferenceCount { get; private set; }

        public JsonNode? Encode(JsonElement element) =>
            element.ValueKind switch
            {
                JsonValueKind.Object => EncodeObject(element),
                JsonValueKind.Array => EncodeArray(element),
                JsonValueKind.String => EncodeString(element.GetString() ?? string.Empty),
                JsonValueKind.Number => EncodeNumber(element),
                JsonValueKind.True => JsonValue.Create(true),
                JsonValueKind.False => JsonValue.Create(false),
                JsonValueKind.Null => null,
                _ => throw new JsonException($"Unsupported JSON value kind '{element.ValueKind}'.")
            };

        private JsonArray EncodeObject(JsonElement element)
        {
            EncodedObjectCount++;
            var propertyNames = element.EnumerateObject()
                .Select(property => StringId(property.Name))
                .ToArray();
            var shapeId = ShapeId(propertyNames);
            var array = new JsonArray { 0, shapeId };

            foreach (var property in element.EnumerateObject())
            {
                array.Add(Encode(property.Value));
            }

            return array;
        }

        private JsonArray EncodeArray(JsonElement element)
        {
            EncodedArrayCount++;
            var array = new JsonArray { 1 };
            foreach (var item in element.EnumerateArray())
            {
                array.Add(Encode(item));
            }

            return array;
        }

        private JsonArray EncodeString(string value)
        {
            EncodedStringReferenceCount++;
            return new JsonArray { 2, StringId(value) };
        }

        private static JsonNode EncodeNumber(JsonElement element)
        {
            if (element.TryGetInt64(out var integer))
            {
                return JsonValue.Create(integer);
            }

            if (element.TryGetDecimal(out var number))
            {
                return JsonValue.Create(number);
            }

            return JsonValue.Create(element.GetDouble());
        }

        private int StringId(string value)
        {
            if (_stringIds.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var id = _strings.Count;
            _strings.Add(value);
            _stringIds[value] = id;
            return id;
        }

        private int ShapeId(IReadOnlyList<int> propertyNameIds)
        {
            var signature = string.Join(",", propertyNameIds);
            if (_shapeIds.TryGetValue(signature, out var existing))
            {
                _shapeUseCounts[existing]++;
                return existing;
            }

            var id = _shapes.Count;
            _shapes.Add(propertyNameIds.ToArray());
            _shapeUseCounts.Add(1);
            _shapeIds[signature] = id;
            return id;
        }
    }

    private sealed record CompactStringDictionary(
        IReadOnlyList<string> Prefixes,
        JsonArray Entries)
    {
        public static CompactStringDictionary Create(IReadOnlyList<string> strings)
        {
            var prefixes = BuildPrefixes(strings);
            var entries = new JsonArray();
            foreach (var value in strings)
            {
                var prefixIndex = BestPrefixIndex(value, prefixes);
                if (prefixIndex is int index)
                {
                    entries.Add(new JsonArray { "p", index, value[prefixes[index].Length..] });
                }
                else
                {
                    entries.Add(value);
                }
            }

            return new CompactStringDictionary(prefixes, entries);
        }

        private static IReadOnlyList<string> BuildPrefixes(IReadOnlyList<string> strings)
        {
            var candidates = new Dictionary<string, PrefixCandidate>(StringComparer.Ordinal);
            foreach (var value in strings.Distinct(StringComparer.Ordinal))
            {
                if (value.Length < 20)
                {
                    continue;
                }

                var max = Math.Min(value.Length - 3, 96);
                for (var index = 8; index < max; index++)
                {
                    if (!IsPrefixBoundary(value[index]))
                    {
                        continue;
                    }

                    var prefix = value[..(index + 1)];
                    if (!candidates.TryGetValue(prefix, out var candidate))
                    {
                        candidate = new PrefixCandidate(prefix);
                        candidates[prefix] = candidate;
                    }

                    candidate.Count++;
                }
            }

            return candidates.Values
                .Where(candidate => candidate.Count >= 3)
                .Select(candidate => candidate with
                {
                    SavingsScore = (candidate.Prefix.Length * (candidate.Count - 1)) - (candidate.Count * 6)
                })
                .Where(candidate => candidate.SavingsScore > 48)
                .OrderByDescending(candidate => candidate.SavingsScore)
                .ThenByDescending(candidate => candidate.Prefix.Length)
                .Take(96)
                .Select(candidate => candidate.Prefix)
                .ToArray();
        }

        private static bool IsPrefixBoundary(char character) =>
            character is ':' or '/' or '\\' or '-' or '_' or '.';

        private static int? BestPrefixIndex(string value, IReadOnlyList<string> prefixes)
        {
            var bestIndex = default(int?);
            var bestLength = 0;
            for (var index = 0; index < prefixes.Count; index++)
            {
                var prefix = prefixes[index];
                if (prefix.Length <= bestLength
                    || prefix.Length >= value.Length
                    || !value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                bestIndex = index;
                bestLength = prefix.Length;
            }

            return bestIndex;
        }

        private sealed record PrefixCandidate(string Prefix)
        {
            public int Count { get; set; }

            public int SavingsScore { get; init; }
        }
    }

    private sealed class DecodedCompactStringDictionary
    {
        private DecodedCompactStringDictionary(
            IReadOnlyList<string> strings,
            IReadOnlyList<IReadOnlyList<int>> shapes)
        {
            Strings = strings;
            Shapes = shapes;
        }

        private IReadOnlyList<string> Strings { get; }

        private IReadOnlyList<IReadOnlyList<int>> Shapes { get; }

        public static DecodedCompactStringDictionary From(JsonElement dictionary)
        {
            var prefixes = dictionary.GetProperty("stringPrefixes")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray();
            var strings = dictionary.GetProperty("strings")
                .EnumerateArray()
                .Select(item => DecodeStringEntry(item, prefixes))
                .ToArray();
            var shapes = dictionary.GetProperty("shapes")
                .EnumerateArray()
                .Select(shape => shape.EnumerateArray().Select(ReadNonNegativeInt32).ToArray())
                .Cast<IReadOnlyList<int>>()
                .ToArray();

            return new DecodedCompactStringDictionary(strings, shapes);
        }

        public string StringAt(int index)
        {
            if (index < 0 || index >= Strings.Count)
            {
                throw new JsonException($"Compact scan string id {index} is out of range.");
            }

            return Strings[index];
        }

        public IReadOnlyList<int> ShapeAt(int index)
        {
            if (index < 0 || index >= Shapes.Count)
            {
                throw new JsonException($"Compact scan shape id {index} is out of range.");
            }

            return Shapes[index];
        }

        private static string DecodeStringEntry(JsonElement item, IReadOnlyList<string> prefixes)
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString() ?? string.Empty;
            }

            if (item.ValueKind != JsonValueKind.Array
                || item.GetArrayLength() != 3
                || item[0].ValueKind != JsonValueKind.String
                || !string.Equals(item[0].GetString(), "p", StringComparison.Ordinal)
                || item[1].ValueKind != JsonValueKind.Number
                || !item[1].TryGetInt32(out var prefixIndex)
                || item[2].ValueKind != JsonValueKind.String)
            {
                throw new JsonException("Compact scan string table entries must be strings or [\"p\",prefixId,suffix].");
            }

            if (prefixIndex < 0 || prefixIndex >= prefixes.Count)
            {
                throw new JsonException($"Compact scan string prefix id {prefixIndex} is out of range.");
            }

            return prefixes[prefixIndex] + item[2].GetString();
        }

        private static int ReadNonNegativeInt32(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Number
                || !item.TryGetInt32(out var value)
                || value < 0)
            {
                throw new JsonException("Compact scan shape ids must be non-negative integers.");
            }

            return value;
        }
    }
}
