using System.Linq;
using Protodef;

namespace PacketGenerator;

/// <summary>
/// Represents a continuous range of protocol versions.
/// </summary>
public readonly record struct VersionRange(int StartVersion, int EndVersion)
{
    /// <summary>
    /// Creates a range that targets a single protocol version.
    /// </summary>
    public static VersionRange Create(int version) => new(version, version);

    /// <summary>
    /// Checks whether the supplied version falls into the range.
    /// </summary>
    public bool Contains(int version) => version >= StartVersion && version <= EndVersion;

    /// <summary>
    /// Expands the range into all concrete protocol versions.
    /// </summary>
    public int[] ToArray() => Enumerable.Range(StartVersion, EndVersion - StartVersion + 1).ToArray();

    /// <inheritdoc />
    public override string ToString() => StartVersion == EndVersion ? $"{StartVersion}" : $"{StartVersion}-{EndVersion}";

    private bool IsOne => StartVersion == EndVersion;

    /// <summary>
    /// Returns a boolean expression that matches this version range.
    /// </summary>
    public string Cond(string variableName) => IsOne
        ? $"{variableName} == {StartVersion}"
        : $"{variableName} >= {StartVersion} && {variableName} <= {EndVersion}";

    /// <summary>
    /// Returns a human readable description of the range for switch expressions.
    /// </summary>
    public string CondSw => IsOne ? StartVersion.ToString() : $">= {StartVersion} and <= {EndVersion}";
}

/// <summary>
/// Represents a continuous protocol interval when comparing schemas.
/// </summary>
public readonly record struct ProtocolRange(int StartVersion, int EndVersion)
{
    public ProtocolRange(VersionRange range) : this(range.StartVersion, range.EndVersion)
    {
    }

    public static ProtocolRange Create(int version) => new(version, version);

    public bool Contains(int version) => version >= StartVersion && version <= EndVersion;

    public int[] ToArray() => Enumerable.Range(StartVersion, EndVersion - StartVersion + 1).ToArray();

    public override string ToString() => StartVersion == EndVersion ? $"{StartVersion}" : $"{StartVersion}-{EndVersion}";
}

/// <summary>
/// A single version interval paired with the corresponding type structure, if any.
/// </summary>
public sealed record TypeStructureRecord(VersionRange Interval, ProtodefType? Structure);

/// <summary>
/// An ordered collection of <see cref="TypeStructureRecord"/> entries describing the
/// evolution of a structure across multiple versions.
/// </summary>
public sealed class TypeStructureHistory : List<TypeStructureRecord>
{
    public TypeStructureHistory()
    {
    }

    public TypeStructureHistory(IEnumerable<TypeStructureRecord> records) : base(records)
    {
    }
}

/// <summary>
/// Represents a concrete structure snapshot for a specific protocol version.
/// </summary>
public sealed record TypeFinderResult(int Version, ProtodefType? Structure)
{
    public TypeFinderResult(int version) : this(version, null)
    {
    }
}
