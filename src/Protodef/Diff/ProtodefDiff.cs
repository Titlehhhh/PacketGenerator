using Protodef.Enumerable;

namespace Protodef;

/// <summary>
/// Computes semantic diffs between two protodef AST nodes using protocol-aware rules
/// instead of structural JSON comparisons.
/// </summary>
public static class ProtodefDiff
{
    /// <summary>
    /// Builds a list of semantic changes between two versions of the same protodef structure.
    /// </summary>
    /// <param name="oldSchema">Previous schema node (may be <c>null</c>).</param>
    /// <param name="newSchema">New schema node (may be <c>null</c>).</param>
    /// <param name="path">Logical location of the compared node (root by default).</param>
    /// <returns>Protocol-aware change descriptions.</returns>
    /// <remarks>
    /// <para>
    /// Containers are compared positionally (field order is significant), switch and mapper
    /// children are compared by key, and unchanged keys or fields are omitted from the result.
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
    /// var changes = ProtodefDiff.Diff(before, after, "SoundSource");
    /// // changes contains exactly one MapperKeyAdded entry for key "10".
    /// </code>
    /// </remarks>
    public static IReadOnlyList<ProtodefChange> Diff(ProtodefType? oldSchema, ProtodefType? newSchema, string? path = null)
    {
        var changes = new List<ProtodefChange>();
        DiffInternal(oldSchema, newSchema, path ?? string.Empty, changes);
        return changes;
    }

    private static void DiffInternal(ProtodefType? oldType, ProtodefType? newType, string path, List<ProtodefChange> changes)
    {
        if (oldType is null && newType is null)
        {
            return;
        }

        if (oldType is null)
        {
            changes.Add(new TypeAdded(path, newType!));
            return;
        }

        if (newType is null)
        {
            changes.Add(new TypeRemoved(path, oldType));
            return;
        }

        if (oldType.GetType() != newType.GetType())
        {
            changes.Add(new TypeReplaced(path, oldType, newType));
            return;
        }

        switch (oldType)
        {
            case ProtodefMapper oldMapper when newType is ProtodefMapper newMapper:
                DiffMapper(path, oldMapper, newMapper, changes);
                DiffInternal(oldMapper.Type, newMapper.Type, AppendPath(path, "type"), changes);
                return;

            case ProtodefSwitch oldSwitch when newType is ProtodefSwitch newSwitch:
                DiffSwitch(path, oldSwitch, newSwitch, changes);
                return;

            case ProtodefContainer oldContainer when newType is ProtodefContainer newContainer:
                DiffContainer(path, oldContainer, newContainer, changes);
                return;

            default:
                if (oldType != newType)
                {
                    changes.Add(new TypeReplaced(path, oldType, newType));
                }
                return;
        }
    }

    private static void DiffMapper(string path, ProtodefMapper oldMapper, ProtodefMapper newMapper, List<ProtodefChange> changes)
    {
        foreach (var kv in newMapper.Mappings)
        {
            if (!oldMapper.Mappings.TryGetValue(kv.Key, out var previous))
            {
                changes.Add(new MapperKeyAdded(AppendPath(path, "mappings"), kv.Key, kv.Value));
            }
            else if (!string.Equals(previous, kv.Value, StringComparison.Ordinal))
            {
                changes.Add(new MapperValueChanged(AppendPath(path, "mappings"), kv.Key, previous, kv.Value));
            }
        }

        foreach (var kv in oldMapper.Mappings)
        {
            if (!newMapper.Mappings.ContainsKey(kv.Key))
            {
                changes.Add(new MapperKeyRemoved(AppendPath(path, "mappings"), kv.Key, kv.Value));
            }
        }
    }

    private static void DiffSwitch(string path, ProtodefSwitch oldSwitch, ProtodefSwitch newSwitch, List<ProtodefChange> changes)
    {
        if (!string.Equals(oldSwitch.CompareTo, newSwitch.CompareTo, StringComparison.Ordinal) ||
            !string.Equals(oldSwitch.CompareToValue, newSwitch.CompareToValue, StringComparison.Ordinal))
        {
            changes.Add(new SwitchSelectorChanged(path, oldSwitch.CompareTo, newSwitch.CompareTo, oldSwitch.CompareToValue, newSwitch.CompareToValue));
        }

        var oldCases = oldSwitch.Fields ?? new Dictionary<string, ProtodefType>();
        var newCases = newSwitch.Fields ?? new Dictionary<string, ProtodefType>();

        foreach (var kv in newCases)
        {
            if (!oldCases.TryGetValue(kv.Key, out var oldCase))
            {
                changes.Add(new SwitchCaseAdded(AppendPath(path, $"case[{kv.Key}]"), kv.Key, kv.Value));
                continue;
            }

            DiffInternal(oldCase, kv.Value, AppendPath(path, $"case[{kv.Key}]"), changes);
        }

        foreach (var kv in oldCases)
        {
            if (!newCases.ContainsKey(kv.Key))
            {
                changes.Add(new SwitchCaseRemoved(AppendPath(path, $"case[{kv.Key}]"), kv.Key, kv.Value));
            }
        }

        if (oldSwitch.Default is null && newSwitch.Default is not null)
        {
            changes.Add(new SwitchDefaultAdded(AppendPath(path, "default"), newSwitch.Default));
        }
        else if (oldSwitch.Default is not null && newSwitch.Default is null)
        {
            changes.Add(new SwitchDefaultRemoved(AppendPath(path, "default"), oldSwitch.Default));
        }
        else if (oldSwitch.Default is not null && newSwitch.Default is not null)
        {
            DiffInternal(oldSwitch.Default, newSwitch.Default, AppendPath(path, "default"), changes);
        }
    }

    private static void DiffContainer(string path, ProtodefContainer oldContainer, ProtodefContainer newContainer, List<ProtodefChange> changes)
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
                    DiffInternal(oldField.Type, newField.Type, AppendPath(path, SegmentForField(newName, i)), changes);
                    continue;
                }
            }

            if (newByName.TryGetValue(oldName, out var newIndex) && !matchedNew[newIndex])
            {
                matchedNew[newIndex] = true;
                changes.Add(new ContainerFieldReordered(path, oldName, i, newIndex));
                DiffInternal(oldField.Type, newFields[newIndex].Type, AppendPath(path, SegmentForField(oldName, newIndex)), changes);
                continue;
            }

            if (i < newFields.Count && !matchedNew[i])
            {
                var newField = newFields[i];
                var newName = newField.Name ?? string.Empty;
                if (!oldByName.ContainsKey(newName) && Equals(oldField.Type, newField.Type))
                {
                    matchedNew[i] = true;
                    changes.Add(new ContainerFieldRenamed(path, i, oldName, newName));
                    continue;
                }
            }

            changes.Add(new ContainerFieldRemoved(path, i, oldName, oldField.Type));
        }

        for (int i = 0; i < newFields.Count; i++)
        {
            if (!matchedNew[i])
            {
                var field = newFields[i];
                var name = field.Name ?? string.Empty;
                changes.Add(new ContainerFieldAdded(path, i, name, field.Type));
            }
        }
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
