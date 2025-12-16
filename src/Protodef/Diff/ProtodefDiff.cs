using System.Collections.Immutable;
using System.Linq;
using PacketGenerator;
using Protodef.Enumerable;

namespace Protodef;

/// <summary>
/// Computes semantic comparison trees between protodef AST nodes using protocol-aware rules
/// instead of structural JSON comparisons.
/// </summary>
public static class ProtodefDiff
{
    /// <summary>
    /// Compares two protodef structures and produces a recursive tree that mirrors the AST.
    /// </summary>
    /// <param name="oldSchema">Previous schema node (may be <c>null</c>).</param>
    /// <param name="newSchema">New schema node (may be <c>null</c>).</param>
    /// <param name="path">Logical location of the compared node (root by default).</param>
    /// <returns>A root diff node that preserves structure even when nodes are unchanged.</returns>
    /// <remarks>
    /// <para>
    /// Containers are compared positionally (field order is significant), switch and mapper
    /// children are compared by key, and unchanged keys or fields are still emitted as
    /// <see cref="ProtodefDiffStatus.Same"/> nodes so the hierarchy remains intact.
    /// </para>
    /// <para>Example: detecting the single mapper key added for the <c>SoundSource</c> type.</para>
    /// <code>
    /// var before = new ProtodefMapper(varint, new()
    /// {
    ///     ["0"] = "master", ["1"] = "music", ["2"] = "record", ["3"] = "weather",
    ///     ["4"] = "block", ["5"] = "hostile", ["6"] = "neutral", ["7"] = "player",
    ///     ["8"] = "ambient", ["9"] = "voice"
    /// });
    /// var after = new ProtodefMapper(varint, new(before.Mappings)
    /// {
    ///     ["10"] = "ui"
    /// });
    /// var diff = ProtodefDiff.Diff(before, after, "SoundSource");
    /// // diff.Children contains one child for mappings, whose children have a single Added node for key "10".
    /// </code>
    /// </remarks>
    public static ProtodefDiffNode? Diff(ProtodefType? oldSchema, ProtodefType? newSchema, string? path = null)
    {
        return DiffInternal(oldSchema, newSchema, path ?? string.Empty);
    }

    /// <summary>
    /// Builds pairwise diff trees for each adjacent protocol range snapshot.
    /// </summary>
    public static IReadOnlyList<ProtodefRangeDiff> DiffRanges(IReadOnlyList<(ProtocolRange Range, ProtodefType? Schema)> history)
    {
        if (history is null) throw new ArgumentNullException(nameof(history));

        var ordered = history.OrderBy(h => h.Range.StartVersion).ToList();
        var results = new List<ProtodefRangeDiff>();

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            var root = Diff(previous.Schema, current.Schema, string.Empty);
            results.Add(new ProtodefRangeDiff(previous.Range, current.Range, root));
        }

        return results;
    }

    /// <summary>
    /// Flattens a diff tree into a list of semantic changes by walking every node.
    /// </summary>
    public static IReadOnlyList<ProtodefChange> CollectChanges(ProtodefDiffNode? root)
    {
        if (root is null)
        {
            return Array.Empty<ProtodefChange>();
        }

        var buffer = new List<ProtodefChange>();
        Traverse(root, buffer);
        return buffer;
    }

    private static void Traverse(ProtodefDiffNode node, List<ProtodefChange> buffer)
    {
        buffer.AddRange(node.Changes);
        foreach (var child in node.Children)
        {
            Traverse(child, buffer);
        }
    }

    private static ProtodefDiffNode? DiffInternal(ProtodefType? oldType, ProtodefType? newType, string path)
    {
        if (oldType is null && newType is null)
        {
            return null;
        }

        if (oldType is null)
        {
            var change = new TypeAdded(path, newType!);
            return new ProtodefDiffNode(path, ProtodefDiffStatus.Added, null, newType, new[] { change }, Array.Empty<ProtodefDiffNode>());
        }

        if (newType is null)
        {
            var change = new TypeRemoved(path, oldType);
            return new ProtodefDiffNode(path, ProtodefDiffStatus.Removed, oldType, null, new[] { change }, Array.Empty<ProtodefDiffNode>());
        }

        if (oldType.GetType() != newType.GetType())
        {
            var change = new TypeReplaced(path, oldType, newType);
            return new ProtodefDiffNode(path, ProtodefDiffStatus.Modified, oldType, newType, new[] { change }, Array.Empty<ProtodefDiffNode>());
        }

        var children = new List<ProtodefDiffNode>();
        var changes = new List<ProtodefChange>();

        switch (oldType)
        {
            case ProtodefMapper oldMapper when newType is ProtodefMapper newMapper:
                DiffMapper(path, oldMapper, newMapper, children, changes);
                break;

            case ProtodefSwitch oldSwitch when newType is ProtodefSwitch newSwitch:
                DiffSwitch(path, oldSwitch, newSwitch, children, changes);
                break;

            case ProtodefContainer oldContainer when newType is ProtodefContainer newContainer:
                DiffContainer(path, oldContainer, newContainer, children, changes);
                break;

            default:
                if (!oldType.Equals(newType))
                {
                    changes.Add(new TypeReplaced(path, oldType, newType));
                }
                break;
        }

        var status = DetermineStatus(oldType, newType, changes, children);
        return new ProtodefDiffNode(path, status, oldType, newType, changes.ToImmutableArray(), children.ToImmutableArray());
    }

    private static void DiffMapper(string path, ProtodefMapper oldMapper, ProtodefMapper newMapper, List<ProtodefDiffNode> children, List<ProtodefChange> changes)
    {
        var typeNode = DiffInternal(oldMapper.Type, newMapper.Type, AppendPath(path, "type"));
        if (typeNode is not null)
        {
            children.Add(typeNode);
        }

        var keys = new SortedSet<string>(oldMapper.Mappings.Keys, StringComparer.Ordinal);
        keys.UnionWith(newMapper.Mappings.Keys);

        foreach (var key in keys)
        {
            var childPath = AppendPath(path, $"mappings[{key}]");
            var hasOld = oldMapper.Mappings.TryGetValue(key, out var oldValue);
            var hasNew = newMapper.Mappings.TryGetValue(key, out var newValue);

            if (hasOld && hasNew)
            {
                if (string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Same, null, null, Array.Empty<ProtodefChange>(), Array.Empty<ProtodefDiffNode>()));
                }
                else
                {
                    var change = new MapperValueChanged(childPath, key, oldValue!, newValue!);
                    children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Modified, null, null, new[] { change }, Array.Empty<ProtodefDiffNode>()));
                }
            }
            else if (hasNew)
            {
                var change = new MapperKeyAdded(childPath, key, newValue!);
                children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Added, null, null, new[] { change }, Array.Empty<ProtodefDiffNode>()));
            }
            else if (hasOld)
            {
                var change = new MapperKeyRemoved(childPath, key, oldValue!);
                children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Removed, null, null, new[] { change }, Array.Empty<ProtodefDiffNode>()));
            }
        }
    }

    private static void DiffSwitch(string path, ProtodefSwitch oldSwitch, ProtodefSwitch newSwitch, List<ProtodefDiffNode> children, List<ProtodefChange> changes)
    {
        if (!string.Equals(oldSwitch.CompareTo, newSwitch.CompareTo, StringComparison.Ordinal) ||
            !string.Equals(oldSwitch.CompareToValue, newSwitch.CompareToValue, StringComparison.Ordinal))
        {
            changes.Add(new SwitchSelectorChanged(path, oldSwitch.CompareTo, newSwitch.CompareTo, oldSwitch.CompareToValue, newSwitch.CompareToValue));
        }

        var oldCases = oldSwitch.Fields ?? new Dictionary<string, ProtodefType>();
        var newCases = newSwitch.Fields ?? new Dictionary<string, ProtodefType>();

        var keys = new SortedSet<string>(oldCases.Keys, StringComparer.Ordinal);
        keys.UnionWith(newCases.Keys);

        foreach (var key in keys)
        {
            var childPath = AppendPath(path, $"case[{key}]");
            var hasOld = oldCases.TryGetValue(key, out var oldCase);
            var hasNew = newCases.TryGetValue(key, out var newCase);

            if (hasOld && hasNew)
            {
                var childNode = DiffInternal(oldCase, newCase, childPath) ??
                                new ProtodefDiffNode(childPath, ProtodefDiffStatus.Same, oldCase, newCase, Array.Empty<ProtodefChange>(), Array.Empty<ProtodefDiffNode>());
                children.Add(childNode);
            }
            else if (hasNew)
            {
                var change = new SwitchCaseAdded(childPath, key, newCase!);
                children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Added, null, newCase, new[] { change }, Array.Empty<ProtodefDiffNode>()));
            }
            else if (hasOld)
            {
                var change = new SwitchCaseRemoved(childPath, key, oldCase!);
                children.Add(new ProtodefDiffNode(childPath, ProtodefDiffStatus.Removed, oldCase, null, new[] { change }, Array.Empty<ProtodefDiffNode>()));
            }
        }

        var defaultPath = AppendPath(path, "default");
        if (oldSwitch.Default is not null || newSwitch.Default is not null)
        {
            var defaultNode = DiffInternal(oldSwitch.Default, newSwitch.Default, defaultPath);
            if (defaultNode is not null)
            {
                children.Add(defaultNode);
            }
        }
    }

    private static void DiffContainer(string path, ProtodefContainer oldContainer, ProtodefContainer newContainer, List<ProtodefDiffNode> children, List<ProtodefChange> changes)
    {
        var oldFields = oldContainer.Fields;
        var newFields = newContainer.Fields;

        var newByName = newFields
            .Select((f, i) => new { Name = f.Name ?? string.Empty, Index = i })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);

        var oldByName = oldFields
            .Select((f, i) => new { Name = f.Name ?? string.Empty, Index = i })
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.Ordinal);

        var matchedNew = new bool[newFields.Count];

        for (int i = 0; i < oldFields.Count; i++)
        {
            var oldField = oldFields[i];
            var oldName = oldField.Name ?? string.Empty;

            if (i < newFields.Count)
            {
                var newField = newFields[i];
                var newName = newField.Name ?? string.Empty;

                if (string.Equals(oldName, newName, StringComparison.Ordinal))
                {
                    matchedNew[i] = true;
                    var childNode = DiffInternal(oldField.Type, newField.Type, AppendPath(path, SegmentForField(newName, i))) ??
                                    new ProtodefDiffNode(AppendPath(path, SegmentForField(newName, i)), ProtodefDiffStatus.Same, oldField.Type, newField.Type, Array.Empty<ProtodefChange>(), Array.Empty<ProtodefDiffNode>());
                    children.Add(childNode);
                    continue;
                }
            }

            if (newByName.TryGetValue(oldName, out var newIndex) && !matchedNew[newIndex])
            {
                matchedNew[newIndex] = true;
                var childPath = AppendPath(path, SegmentForField(oldName, newIndex));
                var reorderChange = new ContainerFieldReordered(path, oldName, i, newIndex);
                var childNode = DiffInternal(oldField.Type, newFields[newIndex].Type, childPath) ??
                                new ProtodefDiffNode(childPath, ProtodefDiffStatus.Same, oldField.Type, newFields[newIndex].Type, Array.Empty<ProtodefChange>(), Array.Empty<ProtodefDiffNode>());
                var withChange = childNode with { Status = ProtodefDiffStatus.Modified, Changes = childNode.Changes.Concat(new[] { reorderChange }).ToImmutableArray() };
                children.Add(withChange);
                continue;
            }

            if (i < newFields.Count && !matchedNew[i])
            {
                var newField = newFields[i];
                var newName = newField.Name ?? string.Empty;
                if (!oldByName.ContainsKey(newName) && Equals(oldField.Type, newField.Type))
                {
                    matchedNew[i] = true;
                    var renameChange = new ContainerFieldRenamed(path, i, oldName, newName);
                    children.Add(new ProtodefDiffNode(AppendPath(path, SegmentForField(newName, i)), ProtodefDiffStatus.Modified, oldField.Type, newField.Type, new[] { renameChange }, Array.Empty<ProtodefDiffNode>()));
                    continue;
                }
            }

            var removedChange = new ContainerFieldRemoved(path, i, oldName, oldField.Type);
            children.Add(new ProtodefDiffNode(AppendPath(path, SegmentForField(oldName, i)), ProtodefDiffStatus.Removed, oldField.Type, null, new[] { removedChange }, Array.Empty<ProtodefDiffNode>()));
        }

        for (int i = 0; i < newFields.Count; i++)
        {
            if (!matchedNew[i])
            {
                var field = newFields[i];
                var name = field.Name ?? string.Empty;
                var addedChange = new ContainerFieldAdded(path, i, name, field.Type);
                children.Add(new ProtodefDiffNode(AppendPath(path, SegmentForField(name, i)), ProtodefDiffStatus.Added, null, field.Type, new[] { addedChange }, Array.Empty<ProtodefDiffNode>()));
            }
        }
    }

    private static ProtodefDiffStatus DetermineStatus(ProtodefType oldType, ProtodefType newType, IReadOnlyCollection<ProtodefChange> changes, IReadOnlyCollection<ProtodefDiffNode> children)
    {
        if (changes.Count == 0 && children.All(c => c.Status == ProtodefDiffStatus.Same))
        {
            return oldType.Equals(newType) ? ProtodefDiffStatus.Same : ProtodefDiffStatus.Modified;
        }

        return ProtodefDiffStatus.Modified;
    }

    private static string AppendPath(string path, string segment)
    {
        return string.IsNullOrEmpty(path) ? segment : $"{path}.{segment}";
    }

    private static string SegmentForField(string name, int index)
    {
        return string.IsNullOrEmpty(name) ? $"field[{index}]" : name;
    }
}

/// <summary>
/// Describes the comparison result for a single protodef node.
/// </summary>
/// <param name="Path">Logical path of the node.</param>
/// <param name="Status">Semantic relationship between old and new versions.</param>
/// <param name="OldType">Previous AST node.</param>
/// <param name="NewType">Current AST node.</param>
/// <param name="Changes">Semantic changes attached to this node.</param>
/// <param name="Children">Children that mirror the AST structure.</param>
public sealed record ProtodefDiffNode(
    string Path,
    ProtodefDiffStatus Status,
    ProtodefType? OldType,
    ProtodefType? NewType,
    IReadOnlyList<ProtodefChange> Changes,
    IReadOnlyList<ProtodefDiffNode> Children);

/// <summary>
/// Indicates how a protodef node relates between two snapshots.
/// </summary>
public enum ProtodefDiffStatus
{
    Same,
    Added,
    Removed,
    Modified
}

/// <summary>
/// Represents a pairwise diff between adjacent protocol ranges.
/// </summary>
/// <param name="From">Earlier protocol interval.</param>
/// <param name="To">Later protocol interval.</param>
/// <param name="Root">Root diff node describing the transition.</param>
public sealed record ProtodefRangeDiff(ProtocolRange From, ProtocolRange To, ProtodefDiffNode? Root);

/// <summary>
/// Base record for semantic protodef changes.
/// </summary>
/// <param name="Path">Logical path to the changed node.</param>
public abstract record ProtodefChange(string Path);

public sealed record TypeAdded(string Path, ProtodefType NewType) : ProtodefChange(Path);
public sealed record TypeRemoved(string Path, ProtodefType OldType) : ProtodefChange(Path);
public sealed record TypeReplaced(string Path, ProtodefType OldType, ProtodefType NewType) : ProtodefChange(Path);

public sealed record MapperKeyAdded(string Path, string Key, string Value) : ProtodefChange(Path);
public sealed record MapperKeyRemoved(string Path, string Key, string Value) : ProtodefChange(Path);
public sealed record MapperValueChanged(string Path, string Key, string OldValue, string NewValue) : ProtodefChange(Path);

public sealed record SwitchSelectorChanged(string Path, string OldCompareTo, string NewCompareTo, string? OldValue, string? NewValue)
    : ProtodefChange(Path);
public sealed record SwitchCaseAdded(string Path, string CaseKey, ProtodefType CaseType) : ProtodefChange(Path);
public sealed record SwitchCaseRemoved(string Path, string CaseKey, ProtodefType CaseType) : ProtodefChange(Path);
public sealed record SwitchDefaultAdded(string Path, ProtodefType CaseType) : ProtodefChange(Path);
public sealed record SwitchDefaultRemoved(string Path, ProtodefType CaseType) : ProtodefChange(Path);

public sealed record ContainerFieldAdded(string Path, int Index, string Name, ProtodefType FieldType) : ProtodefChange(Path);
public sealed record ContainerFieldRemoved(string Path, int Index, string Name, ProtodefType FieldType) : ProtodefChange(Path);
public sealed record ContainerFieldRenamed(string Path, int Index, string OldName, string NewName) : ProtodefChange(Path);
public sealed record ContainerFieldReordered(string Path, string Name, int OldIndex, int NewIndex) : ProtodefChange(Path);
