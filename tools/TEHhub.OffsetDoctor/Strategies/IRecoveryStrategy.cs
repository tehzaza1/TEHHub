namespace TEHhub.OffsetDoctor.Strategies;

using TEHhub.OffsetDoctor.Evidence;
using TEHhub.OffsetDoctor.Manifest;
using TEHhub.OffsetDoctor.Process;

public interface IRecoveryStrategy
{
    ValueKind SupportedKind { get; }
    List<CandidateResult> SearchCandidates(IProcessMemoryReader reader, IntPtr parentAddress, OffsetNode node, RecoveryContext context);
}
