namespace TEHhub.OffsetDoctor.Manifest;

public sealed class OffsetNode
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Category { get; init; }
    public string? ParentId { get; init; }
    public int DefaultOffset { get; init; }
    public ValueKind Kind { get; init; }
    public ScalarType ScalarType { get; init; } = ScalarType.None;
    public int VectorElementSize { get; init; } = 8;
    public bool VectorIsPointerElements { get; init; } = false;
    public int Alignment { get; init; } = 8;
    public int SearchRadius { get; init; } = 0x200;
    public string? StaticPatternName { get; init; }
    public string? StructTypeName { get; init; }
    public string? FieldName { get; init; }
    public string? ProductionSourceLocation { get; init; }
    public string? ComponentName { get; init; }
    public bool IsOptionalStateDependent { get; init; }
    public bool ConservativeUnverifiedOnly { get; init; }
    public double? ExpectedMinNumeric { get; init; }
    public double? ExpectedMaxNumeric { get; init; }
}
