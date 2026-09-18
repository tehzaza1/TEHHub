namespace TEHhub.OffsetDoctor.Validation;

public sealed class ValidationGroundTruth
{
    public int? ExpectedGold { get; init; }
    public int? ExpectedHpCurrent { get; init; }
    public int? ExpectedHpTotal { get; init; }
    public int? ExpectedMpCurrent { get; init; }
    public int? ExpectedMpTotal { get; init; }
    public int? ExpectedEsCurrent { get; init; }
    public int? ExpectedEsTotal { get; init; }
}
