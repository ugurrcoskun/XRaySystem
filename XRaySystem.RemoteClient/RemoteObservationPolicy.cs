using XRaySystem.Contracts;
using XRaySystem.Domain.ValueObjects;

namespace XRaySystem.RemoteClient;

/// <summary>
/// Keeps read-only observation semantics separate from command-lease ownership.
/// </summary>
public static class RemoteObservationPolicy
{
    public static bool CanIssueAcquisitionCommand(bool hasCommandControl) =>
        hasCommandControl;

    public static bool ShouldForceLivePresentation(AcquisitionOperation operation) =>
        operation == AcquisitionOperation.Grab;

    public static bool ShouldAutoPresentCommittedSnap(
        bool hasCommandControl,
        AcquisitionOperationContract operation,
        Guid? imageId) =>
        !hasCommandControl &&
        operation == AcquisitionOperationContract.Snap &&
        imageId is { } id && id != Guid.Empty;
}
