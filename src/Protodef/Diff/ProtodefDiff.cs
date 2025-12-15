using PacketGenerator;
using Protodef.Enumerable;

namespace Protodef;

/// <summary>
/// Provides utilities for building semantic difference trees between <see cref="ProtodefType"/> graphs.
/// </summary>
public static class ProtodefDiff
{
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

    private static void BuildRecursive(ProtodefDiffNode node)
    {
        foreach (var child in ProtodefSemanticChildrenBuilder.Build(node.Versions, node.Structures))
        {
            node.Children.Add(child);
            BuildRecursive(child);
        }
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
