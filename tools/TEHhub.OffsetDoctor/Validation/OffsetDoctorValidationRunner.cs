namespace TEHhub.OffsetDoctor.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;
using TEHhub.OffsetDoctor.Strategies;

public sealed class OffsetDoctorValidationRunner
{
    private readonly OffsetValidatorEngine _validator = new();

    public OffsetDoctorValidationReport RunValidation(IProcessMemoryReader reader, ValidationGroundTruth? groundTruth = null)
    {
        var manifestNodes = OffsetManifest.CreateFullRepositoryManifest();
        var context = RecoveryContext.FromGroundTruth(groundTruth, manifestNodes);

        // Strict validation mode: allowRecovery = false
        var results = _validator.ValidateChain(reader, manifestNodes, context, allowRecovery: false);

        return new OffsetDoctorValidationReport
        {
            ProcessMetadata = reader.Metadata,
            TimestampUtc = DateTime.UtcNow,
            Results = results,
            IsChainHealthy = results.All(r => r.Status == ValidationStatus.VALID)
        };
    }
}

public sealed class OffsetDoctorValidationReport
{
    public required ProcessMetadata ProcessMetadata { get; init; }
    public DateTime TimestampUtc { get; init; }
    public List<ValidationResult> Results { get; init; } = [];
    public bool IsChainHealthy { get; init; }

    public int ValidCount => Results.Count(r => r.Status == ValidationStatus.VALID);
    public int BrokenCount => Results.Count(r => r.Status == ValidationStatus.BROKEN);
    public int BlockedCount => Results.Count(r => r.Status == ValidationStatus.BLOCKED);
    public int UnverifiedCount => Results.Count(r => r.Status == ValidationStatus.UNVERIFIED);
    public int TotalNodesCount => Results.Count;
}
