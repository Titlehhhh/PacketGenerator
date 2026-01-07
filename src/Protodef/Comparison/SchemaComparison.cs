using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json.Serialization;
using Protodef;

namespace Protodef.Comparison;

public readonly record struct ProtocolRange(int Start, int End)
{
    public bool Contains(int version) => version >= Start && version <= End;

    public IEnumerable<int> ToEnumerable()
    {
        for (var i = Start; i <= End; i++)
        {
            yield return i;
        }
    }

    public override string ToString() => Start == End ? Start.ToString() : $"{Start}-{End}";
}

[JsonConverter(typeof(JsonStringEnumConverter<SchemaComparisonStatus>))]
public enum SchemaComparisonStatus
{
    Same,
    Added,
    Removed,
    Modified
}

public sealed class SchemaComparisonNode
{
    [JsonInclude]
    public string Path { get; set; } = string.Empty;

    [JsonInclude]
    public string? Key { get; set; }

    [JsonInclude]
    public SchemaComparisonStatus Status { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Value { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? OldValue { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? NewValue { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProtodefType? OldSchema { get; set; }

    [JsonInclude]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProtodefType? NewSchema { get; set; }

    [JsonInclude]
    public Collection<SchemaComparisonNode> Children { get; } = new();

    [JsonIgnore]
    public bool HasChanges => Status != SchemaComparisonStatus.Same || Children.Any(child => child.HasChanges);
}

public sealed record SchemaChange(string Path, SchemaComparisonStatus Status, string? Key, object? OldValue, object? NewValue);

public sealed record SchemaComparisonResult(ProtocolRange OldRange, ProtocolRange NewRange, SchemaComparisonNode Root);

public static class SchemaComparer
{
    public static SchemaComparisonNode Compare(string path, ProtodefType? oldSchema, ProtodefType? newSchema)
    {
        return CompareInternal(path, oldSchema, newSchema);
    }

    public static IReadOnlyList<SchemaComparisonResult> CompareHistory(
        IReadOnlyList<(ProtocolRange Range, ProtodefType? Schema)> ranges,
        string rootPath)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Count < 2)
        {
            return Array.Empty<SchemaComparisonResult>();
        }

        var results = new List<SchemaComparisonResult>(ranges.Count - 1);
        for (var i = 1; i < ranges.Count; i++)
        {
            var previous = ranges[i - 1];
            var current = ranges[i];
            var comparison = Compare(rootPath, previous.Schema, current.Schema);
            results.Add(new SchemaComparisonResult(previous.Range, current.Range, comparison));
        }

        return results;
    }

    public static IReadOnlyList<SchemaChange> ExtractChanges(SchemaComparisonNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var changes = new List<SchemaChange>();
        Walk(root, null, changes);
        return changes;

        static void Walk(SchemaComparisonNode node, string? parentPath, ICollection<SchemaChange> acc)
        {
            var currentPath = string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(node.Path)
                ? (string.IsNullOrEmpty(parentPath) ? node.Path : parentPath)
                : $"{parentPath}.{node.Path}";

            if (node.Status != SchemaComparisonStatus.Same)
            {
                acc.Add(new SchemaChange(currentPath, node.Status, node.Key, node.OldValue ?? node.Value, node.NewValue ?? node.Value));
            }

            foreach (var child in node.Children)
            {
                Walk(child, currentPath, acc);
            }
        }
    }

    private static SchemaComparisonNode CompareInternal(string path, ProtodefType? oldSchema, ProtodefType? newSchema)
    {
        if (oldSchema is null && newSchema is null)
        {
            return new SchemaComparisonNode
            {
                Path = path,
                Status = SchemaComparisonStatus.Same
            };
        }

        if (oldSchema is null)
        {
            return new SchemaComparisonNode
            {
                Path = path,
                Status = SchemaComparisonStatus.Added,
                NewSchema = newSchema
            };
        }

        if (newSchema is null)
        {
            return new SchemaComparisonNode
            {
                Path = path,
                Status = SchemaComparisonStatus.Removed,
                OldSchema = oldSchema
            };
        }

        if (oldSchema.GetType() != newSchema.GetType())
        {
            return new SchemaComparisonNode
            {
                Path = path,
                Status = SchemaComparisonStatus.Modified,
                OldSchema = oldSchema,
                NewSchema = newSchema
            };
        }

        return oldSchema switch
        {
            Protodef.Enumerable.ProtodefContainer oldContainer when newSchema is Protodef.Enumerable.ProtodefContainer newContainer
                => CompareContainer(path, oldContainer, newContainer),
            Protodef.Enumerable.ProtodefSwitch oldSwitch when newSchema is Protodef.Enumerable.ProtodefSwitch newSwitch
                => CompareSwitch(path, oldSwitch, newSwitch),
            Protodef.Enumerable.ProtodefMapper oldMapper when newSchema is Protodef.Enumerable.ProtodefMapper newMapper
                => CompareMapper(path, oldMapper, newMapper),
            _ => CompareDefault(path, oldSchema, newSchema)
        };
    }

    private static SchemaComparisonNode CompareContainer(string path, Protodef.Enumerable.ProtodefContainer oldContainer, Protodef.Enumerable.ProtodefContainer newContainer)
    {
        var node = CreateBaseNode(path, oldContainer, newContainer);
        var max = Math.Max(oldContainer.Fields.Count, newContainer.Fields.Count);

        for (var index = 0; index < max; index++)
        {
            var oldField = index < oldContainer.Fields.Count ? oldContainer.Fields[index] : null;
            var newField = index < newContainer.Fields.Count ? newContainer.Fields[index] : null;
            var childPath = oldField?.Name ?? newField?.Name ?? $"[{index}]";
            var childNode = CompareInternal(childPath, oldField?.Type, newField?.Type);
            childNode.Key = childPath;
            childNode.OldValue = oldField?.Name;
            childNode.NewValue = newField?.Name;
            node.Children.Add(childNode);
        }

        node.Status = DetermineStatus(node.Status, oldContainer, newContainer, node.Children);
        return node;
    }

    private static SchemaComparisonNode CompareSwitch(string path, Protodef.Enumerable.ProtodefSwitch oldSwitch, Protodef.Enumerable.ProtodefSwitch newSwitch)
    {
        var node = CreateBaseNode(path, oldSwitch, newSwitch);
        var oldFields = oldSwitch.Fields ?? new Dictionary<string, ProtodefType>();
        var newFields = newSwitch.Fields ?? new Dictionary<string, ProtodefType>();

        foreach (var key in oldFields.Keys.Union(newFields.Keys).OrderBy(k => k))
        {
            oldFields.TryGetValue(key, out var oldValue);
            newFields.TryGetValue(key, out var newValue);
            var child = CompareInternal(key, oldValue, newValue);
            child.Key = key;
            node.Children.Add(child);
        }

        if (oldSwitch.Default is not null || newSwitch.Default is not null)
        {
            node.Children.Add(CompareInternal("default", oldSwitch.Default, newSwitch.Default));
        }

        node.Status = DetermineStatus(node.Status, oldSwitch, newSwitch, node.Children);
        return node;
    }

    private static SchemaComparisonNode CompareMapper(string path, Protodef.Enumerable.ProtodefMapper oldMapper, Protodef.Enumerable.ProtodefMapper newMapper)
    {
        var node = CreateBaseNode(path, oldMapper, newMapper);
        node.Children.Add(CompareInternal("type", oldMapper.Type, newMapper.Type));

        foreach (var key in oldMapper.Mappings.Keys.Union(newMapper.Mappings.Keys).OrderBy(k => k))
        {
            oldMapper.Mappings.TryGetValue(key, out var oldValue);
            newMapper.Mappings.TryGetValue(key, out var newValue);
            var status = DetermineSimpleStatus(oldValue, newValue);
            node.Children.Add(new SchemaComparisonNode
            {
                Path = key,
                Key = key,
                Status = status,
                Value = status switch
                {
                    SchemaComparisonStatus.Added => newValue,
                    SchemaComparisonStatus.Removed => oldValue,
                    _ => null
                },
                OldValue = oldValue,
                NewValue = newValue
            });
        }

        node.Status = DetermineStatus(node.Status, oldMapper, newMapper, node.Children);
        return node;
    }

    private static SchemaComparisonNode CompareDefault(string path, ProtodefType oldSchema, ProtodefType newSchema)
    {
        var node = CreateBaseNode(path, oldSchema, newSchema);
        var oldChildren = oldSchema.Children.ToList();
        var newChildren = newSchema.Children.ToList();

        if (oldChildren.Count == 0 && newChildren.Count == 0)
        {
            node.Status = oldSchema.Equals(newSchema) ? SchemaComparisonStatus.Same : SchemaComparisonStatus.Modified;
            return node;
        }

        var allKeys = oldChildren.Select(c => c.Key).Union(newChildren.Select(c => c.Key)).ToList();

        if (allKeys.All(k => k is null))
        {
            var max = Math.Max(oldChildren.Count, newChildren.Count);
            for (var i = 0; i < max; i++)
            {
                var oldChild = i < oldChildren.Count ? oldChildren[i].Value : null;
                var newChild = i < newChildren.Count ? newChildren[i].Value : null;
                var childPath = oldChildren.ElementAtOrDefault(i).Key ?? newChildren.ElementAtOrDefault(i).Key ?? $"[{i}]";
                node.Children.Add(CompareInternal(childPath, oldChild, newChild));
            }
        }
        else
        {
            foreach (var key in allKeys.Where(k => k is not null)!.Cast<string>().Distinct().OrderBy(k => k))
            {
                var oldChild = oldChildren.FirstOrDefault(c => c.Key == key).Value;
                var newChild = newChildren.FirstOrDefault(c => c.Key == key).Value;
                node.Children.Add(CompareInternal(key, oldChild, newChild));
            }
        }

        node.Status = DetermineStatus(node.Status, oldSchema, newSchema, node.Children);
        return node;
    }

    private static SchemaComparisonStatus DetermineStatus(
        SchemaComparisonStatus current,
        ProtodefType oldSchema,
        ProtodefType newSchema,
        IEnumerable<SchemaComparisonNode> children)
    {
        if (current is SchemaComparisonStatus.Added or SchemaComparisonStatus.Removed)
        {
            return current;
        }

        if (children.Any(child => child.HasChanges))
        {
            return SchemaComparisonStatus.Modified;
        }

        return oldSchema.Equals(newSchema) ? SchemaComparisonStatus.Same : SchemaComparisonStatus.Modified;
    }

    private static SchemaComparisonStatus DetermineSimpleStatus(string? oldValue, string? newValue)
    {
        if (oldValue is null && newValue is not null) return SchemaComparisonStatus.Added;
        if (oldValue is not null && newValue is null) return SchemaComparisonStatus.Removed;
        return oldValue == newValue ? SchemaComparisonStatus.Same : SchemaComparisonStatus.Modified;
    }

    private static SchemaComparisonNode CreateBaseNode(string path, ProtodefType oldSchema, ProtodefType newSchema)
    {
        return new SchemaComparisonNode
        {
            Path = path,
            OldSchema = oldSchema,
            NewSchema = newSchema,
            Status = SchemaComparisonStatus.Same
        };
    }
}
