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
    /// <param name="options">Optional JSON options; defaults to <see cref="DefaultJsonOptions"/>.</param>
    /// <returns>A JSON representation of the diff tree.</returns>
    /// <remarks>
    /// <para>
    /// The output aligns every node by version with <c>versions</c> and <c>structures</c> arrays, allowing consumers to
    /// see additions or removals explicitly. Null entries in <c>structures</c> indicate a missing type for that version.
    /// </para>
    /// <para>Example: serializing a packet diff that exists only in versions 757 and 759:</para>
    /// <code>
    /// var json = ProtodefDiff.ToJson(ProtodefDiff.DiffTypes([
    ///     new TypeFinderResult(757, packet757),
    ///     new TypeFinderResult(758),
    ///     new TypeFinderResult(759, packet759)
    /// ]));
    /// </code>
    /// </remarks>
    public static string ToJson(ProtodefDiffNode root, JsonSerializerOptions? options = null)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = options?.WriteIndented ?? true });
        WriteJson(root, writer, options);
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Writes a diff node and its children to an existing JSON writer.
    /// </summary>
    /// <param name="root">Root node returned from <see cref="DiffTypes"/>.</param>
    /// <param name="writer">Destination writer.</param>
    /// <param name="options">Optional JSON options; defaults to <see cref="DefaultJsonOptions"/>.</param>
    public static void WriteJson(ProtodefDiffNode root, Utf8JsonWriter writer, JsonSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(writer);

        WriteNode(root, writer, options ?? DefaultJsonOptions);
    }

    private static void BuildRecursive(ProtodefDiffNode node)
    {
        foreach (var child in ProtodefSemanticChildrenBuilder.Build(node.Versions, node.Structures))
        {
            node.Children.Add(child);
            BuildRecursive(child);
        }
    }

    private static void WriteNode(ProtodefDiffNode node, Utf8JsonWriter writer, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (node.Key is not null)
            writer.WriteString("key", node.Key);

        writer.WritePropertyName("versions");
        JsonSerializer.Serialize(writer, node.Versions, options);

        writer.WritePropertyName("structures");
        JsonSerializer.Serialize(writer, node.Structures, options);

        if (node.Children.Count > 0)
        {
            writer.WritePropertyName("children");
            writer.WriteStartArray();

            foreach (var child in node.Children)
            {
                WriteNode(child, writer, options);
            }

            writer.WriteEndArray();
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
