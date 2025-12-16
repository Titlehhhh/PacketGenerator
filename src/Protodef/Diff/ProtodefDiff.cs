using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PacketGenerator;
using Protodef.Enumerable;

namespace Protodef;

/// <summary>
/// Provides utilities for building semantic difference trees between <see cref="ProtodefType"/> graphs.
/// </summary>
public static class ProtodefDiff
{
    /// <summary>
    /// Default JSON serialization options for diff nodes.
    /// </summary>
    public static readonly JsonSerializerOptions DefaultJsonOptions = new(ProtodefType.DefaultJsonOptions)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Default options that control how diff nodes are written to JSON.
    /// </summary>
    public static readonly ProtodefDiffJsonOptions DefaultDiffJsonOptions = new();

    /// <summary>
    /// Builds a diff tree from multiple versions of the same structure.
    /// </summary>
    /// <param name="inputs">Versioned results describing the structure in each protocol.</param>
    /// <remarks>
    /// The inputs are ordered by <see cref="TypeFinderResult.Version"/>, and null structures are allowed when a
    /// version does not provide the type. The resulting <see cref="ProtodefDiffNode"/> can be traversed to compare
    /// children across versions.
    /// </remarks>
    /// <example>
    /// <code>
    /// var diff = ProtodefDiff.DiffTypes([
    ///     new TypeFinderResult(735, packet735),
    ///     new TypeFinderResult(736), // removed in this version
    ///     new TypeFinderResult(737, packet737)
    /// ]);
    /// </code>
    /// </example>
    public static ProtodefDiffNode DiffTypes(IReadOnlyList<TypeFinderResult> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            throw new ArgumentException("At least one input is required", nameof(inputs));

        var ordered = inputs.OrderBy(x => x.Version).ToArray();
        var versions = ordered.Select(x => x.Version).ToArray();
        var structures = ordered.Select(x => x.Structure).ToArray();

        var root = new ProtodefDiffNode(null, versions, structures);
        BuildRecursive(root);
        return root;
    }

    /// <summary>
    /// Serializes a diff node and its children to a JSON string.
    /// </summary>
    /// <param name="root">Root node returned from <see cref="DiffTypes"/>.</param>
    /// <param name="diffOptions">Controls whether compact history ranges and/or raw version arrays are emitted.</param>
    /// <param name="options">Optional JSON options; defaults to <see cref="DefaultJsonOptions"/>.</param>
    /// <returns>A JSON representation of the diff tree.</returns>
    /// <remarks>
    /// <para>
    /// By default the output favors compact <c>history</c> entries that collapse identical structures into contiguous
    /// version ranges and emit <c>delta</c> objects for supported types (e.g., mapper key additions/removals). Raw
    /// <c>versions</c> and <c>structures</c> arrays can be included by setting <see cref="ProtodefDiffJsonOptions.IncludeVersionArrays"/>.
    /// </para>
    /// <para>Example: serializing a mapper that adds the <c>ui</c> key only for versions 771-772:</para>
    /// <code>
    /// var sound = ProtodefDiff.DiffTypes([
    ///     new TypeFinderResult(761, mapper9Keys),
    ///     new TypeFinderResult(770, mapper9Keys),
    ///     new TypeFinderResult(771, mapper10Keys)
    /// ]);
    /// var json = ProtodefDiff.ToJson(sound);
    /// // Produces a history array with two ranges and a delta listing the newly added mapping.
    /// </code>
    /// </remarks>
    public static string ToJson(
        ProtodefDiffNode root,
        ProtodefDiffJsonOptions? diffOptions = null,
        JsonSerializerOptions? options = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = options?.WriteIndented ?? true });
        WriteJson(root, writer, diffOptions, options);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a diff node and its children to an existing JSON writer.
    /// </summary>
    /// <param name="root">Root node returned from <see cref="DiffTypes"/>.</param>
    /// <param name="writer">Destination writer.</param>
    /// <param name="options">Optional JSON options; defaults to <see cref="DefaultJsonOptions"/>.</param>
    public static void WriteJson(
        ProtodefDiffNode root,
        Utf8JsonWriter writer,
        ProtodefDiffJsonOptions? diffOptions = null,
        JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(writer);

        var serializerOptions = options ?? DefaultJsonOptions;
        var diff = diffOptions ?? DefaultDiffJsonOptions;
        WriteNode(root, writer, serializerOptions, diff);
    }

    private static void BuildRecursive(ProtodefDiffNode node)
    {
        foreach (var child in ProtodefSemanticChildrenBuilder.Build(node.Versions, node.Structures))
        {
            node.Children.Add(child);
            BuildRecursive(child);
        }
    }

    private static void WriteNode(
        ProtodefDiffNode node,
        Utf8JsonWriter writer,
        JsonSerializerOptions options,
        ProtodefDiffJsonOptions diffOptions)
    {
        writer.WriteStartObject();

        if (node.Key is not null)
            writer.WriteString("key", node.Key);

        if (diffOptions.IncludeVersionArrays)
        {
            writer.WritePropertyName("versions");
            JsonSerializer.Serialize(writer, node.Versions, options);

            writer.WritePropertyName("structures");
            JsonSerializer.Serialize(writer, node.Structures, options);
        }

        if (diffOptions.IncludeHistory)
        {
            var history = HistorySegment.Create(node.Versions, node.Structures);
            writer.WritePropertyName("history");
            writer.WriteStartArray();

            HistorySegment? previousNonNull = null;
            foreach (var segment in history)
            {
                writer.WriteStartObject();

                writer.WritePropertyName("range");
                if (segment.Start == segment.End)
                {
                    writer.WriteNumberValue(segment.Start);
                }
                else
                {
                    writer.WriteStartArray();
                    writer.WriteNumberValue(segment.Start);
                    writer.WriteNumberValue(segment.End);
                    writer.WriteEndArray();
                }

                writer.WritePropertyName("structure");
                JsonSerializer.Serialize(writer, segment.Structure, options);

                if (diffOptions.IncludeDeltas && previousNonNull?.Structure is { } prevStruct && segment.Structure is { } currentStruct)
                {
                    WriteDelta(prevStruct, currentStruct, writer, options);
                }

                writer.WriteEndObject();

                if (segment.Structure is not null)
                {
                    previousNonNull = segment;
                }
            }

            writer.WriteEndArray();
        }

        if (node.Children.Count > 0)
        {
            writer.WritePropertyName("children");
            writer.WriteStartArray();

            foreach (var child in node.Children)
            {
                WriteNode(child, writer, options, diffOptions);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteDelta(ProtodefType previous, ProtodefType current, Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        switch (previous, current)
        {
            case (ProtodefMapper prevMapper, ProtodefMapper currentMapper):
                WriteMapperDelta(prevMapper, currentMapper, writer, options);
                break;
        }
    }

    private static void WriteMapperDelta(ProtodefMapper previous, ProtodefMapper current, Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        var added = new Dictionary<string, string>();
        var removed = new Dictionary<string, string>();

        foreach (var kv in current.Mappings)
        {
            if (!previous.Mappings.TryGetValue(kv.Key, out var prevValue))
            {
                added[kv.Key] = kv.Value;
            }
            else if (!string.Equals(prevValue, kv.Value, StringComparison.Ordinal))
            {
                removed[kv.Key] = prevValue;
                added[kv.Key] = kv.Value;
            }
        }

        foreach (var kv in previous.Mappings)
        {
            if (!current.Mappings.ContainsKey(kv.Key))
            {
                removed[kv.Key] = kv.Value;
            }
        }

        if (added.Count == 0 && removed.Count == 0)
            return;

        writer.WritePropertyName("delta");
        writer.WriteStartObject();

        if (added.Count > 0)
        {
            writer.WritePropertyName("addedMappings");
            JsonSerializer.Serialize(writer, added, options);
        }

        if (removed.Count > 0)
        {
            writer.WritePropertyName("removedMappings");
            JsonSerializer.Serialize(writer, removed, options);
        }

        writer.WriteEndObject();
    }
}

internal static class ProtodefSemanticChildrenBuilder
{
    public static IEnumerable<ProtodefDiffNode> Build(IReadOnlyList<int> versions, IReadOnlyList<ProtodefType?> structures)
    {
        var sample = structures.FirstOrDefault(static x => x is not null);
        return sample switch
        {
            ProtodefContainer => BuildContainerChildren(versions, structures),
            ProtodefSwitch => BuildSwitchChildren(versions, structures),
            ProtodefMapper => BuildMapperChildren(versions, structures),
            ProtodefNamespace => BuildNamespaceChildren(versions, structures),
            ProtodefProtocol => BuildProtocolChildren(versions, structures),
            _ => Enumerable.Empty<ProtodefDiffNode>()
        };
    }

    private static IEnumerable<ProtodefDiffNode> BuildProtocolChildren(IReadOnlyList<int> versions,
        IReadOnlyList<ProtodefType?> structures)
    {
        var children = new Dictionary<string, ProtodefType?[]>(StringComparer.Ordinal);

        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i] is not ProtodefProtocol protocol)
                continue;

            foreach (var kv in protocol.Children)
            {
                var array = GetOrCreateChildSlot(children, kv.Key!, structures.Count);
                array[i] = kv.Value;
            }
        }

        return BuildNodes(versions, children);
    }

    private static IEnumerable<ProtodefDiffNode> BuildNamespaceChildren(IReadOnlyList<int> versions,
        IReadOnlyList<ProtodefType?> structures)
    {
        var children = new Dictionary<string, ProtodefType?[]>(StringComparer.Ordinal);

        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i] is not ProtodefNamespace ns)
                continue;

            foreach (var kv in ns.Types)
            {
                var array = GetOrCreateChildSlot(children, kv.Key, structures.Count);
                array[i] = kv.Value;
            }
        }

        return BuildNodes(versions, children);
    }

    private static IEnumerable<ProtodefDiffNode> BuildContainerChildren(IReadOnlyList<int> versions,
        IReadOnlyList<ProtodefType?> structures)
    {
        var children = new Dictionary<string, ProtodefType?[]>(StringComparer.Ordinal);

        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i] is not ProtodefContainer container)
                continue;

            foreach (var field in container.Fields)
            {
                var key = field.Name ?? string.Empty;
                var array = GetOrCreateChildSlot(children, key, structures.Count);
                array[i] = field.Type;
            }
        }

        return BuildNodes(versions, children);
    }

    private static IEnumerable<ProtodefDiffNode> BuildSwitchChildren(IReadOnlyList<int> versions,
        IReadOnlyList<ProtodefType?> structures)
    {
        var children = new Dictionary<string, ProtodefType?[]>(StringComparer.Ordinal);

        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i] is not ProtodefSwitch sw)
                continue;

            if (sw.Fields is not null)
            {
                foreach (var kv in sw.Fields)
                {
                    var array = GetOrCreateChildSlot(children, kv.Key, structures.Count);
                    array[i] = kv.Value;
                }
            }

            if (sw.Default is not null)
            {
                var array = GetOrCreateChildSlot(children, "default", structures.Count);
                array[i] = sw.Default;
            }
        }

        return BuildNodes(versions, children);
    }

    private static IEnumerable<ProtodefDiffNode> BuildMapperChildren(IReadOnlyList<int> versions,
        IReadOnlyList<ProtodefType?> structures)
    {
        var children = new Dictionary<string, ProtodefType?[]>(StringComparer.Ordinal);

        for (int i = 0; i < structures.Count; i++)
        {
            if (structures[i] is not ProtodefMapper mapper)
                continue;

            var array = GetOrCreateChildSlot(children, "type", structures.Count);
            array[i] = mapper.Type;
        }

        return BuildNodes(versions, children);
    }

    private static ProtodefType?[] GetOrCreateChildSlot(Dictionary<string, ProtodefType?[]> children, string key, int size)
    {
        if (!children.TryGetValue(key, out var array))
        {
            array = new ProtodefType?[size];
            children.Add(key, array);
        }

        return array;
    }

    private static IEnumerable<ProtodefDiffNode> BuildNodes(IReadOnlyList<int> versions,
        Dictionary<string, ProtodefType?[]> children)
    {
        foreach (var kv in children.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            yield return new ProtodefDiffNode(kv.Key, versions, kv.Value);
        }
    }
}

/// <summary>
/// Represents a node in the semantic difference tree for protodef structures.
/// </summary>
public sealed class ProtodefDiffNode
{
    public ProtodefDiffNode(string? key, IReadOnlyList<int> versions, IReadOnlyList<ProtodefType?> structures)
    {
        Key = key;
        Versions = versions ?? throw new ArgumentNullException(nameof(versions));
        Structures = structures ?? throw new ArgumentNullException(nameof(structures));
        if (Versions.Count != Structures.Count)
            throw new ArgumentException("Versions and structures collections must have the same length.");
    }

    /// <summary>Child nodes aligned by <see cref="Versions"/>.</summary>
    public List<ProtodefDiffNode> Children { get; } = new();

    /// <summary>The logical name of this node (e.g. field name or case label).</summary>
    public string? Key { get; }

    /// <summary>Version values passed to the diff.</summary>
    public IReadOnlyList<int> Versions { get; }

    /// <summary>Structures corresponding to each <see cref="Versions"/> entry.</summary>
    public IReadOnlyList<ProtodefType?> Structures { get; }
}

/// <summary>
/// Options to control how <see cref="ProtodefDiffNode"/> trees are serialized to JSON.
/// </summary>
public sealed class ProtodefDiffJsonOptions
{
    /// <summary>
    /// Includes raw <c>versions</c> and <c>structures</c> arrays in the output.
    /// </summary>
    /// <remarks>Defaults to <c>false</c> to favor compact history entries.</remarks>
    public bool IncludeVersionArrays { get; init; }

    /// <summary>
    /// Emits a compact <c>history</c> array that groups identical structures by contiguous version ranges.
    /// </summary>
    public bool IncludeHistory { get; init; } = true;

    /// <summary>
    /// When <see cref="IncludeHistory"/> is enabled, emit per-segment deltas when the structure type supports them
    /// (e.g., added/removed mapper keys).
    /// </summary>
    public bool IncludeDeltas { get; init; } = true;
}

internal readonly record struct HistorySegment(int Start, int End, ProtodefType? Structure)
{
    public static IReadOnlyList<HistorySegment> Create(IReadOnlyList<int> versions, IReadOnlyList<ProtodefType?> structures)
    {
        var segments = new List<HistorySegment>();

        int start = 0;
        for (int i = 1; i <= structures.Count; i++)
        {
            var previous = structures[start];
            var current = i < structures.Count ? structures[i] : null;

            var isSame = i < structures.Count && previous == current;
            if (isSame)
                continue;

            segments.Add(new HistorySegment(versions[start], versions[i - 1], previous));
            start = i;
        }

        return segments;
    }
}
