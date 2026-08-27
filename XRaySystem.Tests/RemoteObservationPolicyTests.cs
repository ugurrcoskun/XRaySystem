using XRaySystem.Contracts;
using XRaySystem.Domain.ValueObjects;
using XRaySystem.RemoteClient;

namespace XRaySystem.Tests;

public sealed class RemoteObservationPolicyTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Command_permission_requires_explicit_control_ownership(
        bool hasCommandControl,
        bool expected) =>
        Assert.Equal(
            expected,
            RemoteObservationPolicy.CanIssueAcquisitionCommand(hasCommandControl));

    [Theory]
    [InlineData(AcquisitionOperation.Grab, true)]
    [InlineData(AcquisitionOperation.Snap, false)]
    public void Only_grab_frames_supersede_history_with_live_presentation(
        AcquisitionOperation operation,
        bool expected) =>
        Assert.Equal(
            expected,
            RemoteObservationPolicy.ShouldForceLivePresentation(operation));

    [Fact]
    public void Observer_auto_presents_only_a_committed_snap_with_a_valid_image_id()
    {
        var imageId = Guid.NewGuid();

        Assert.True(RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
            hasCommandControl: false,
            AcquisitionOperationContract.Snap,
            imageId));
        Assert.False(RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
            hasCommandControl: true,
            AcquisitionOperationContract.Snap,
            imageId));
        Assert.False(RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
            hasCommandControl: false,
            AcquisitionOperationContract.Grab,
            imageId));
        Assert.False(RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
            hasCommandControl: false,
            AcquisitionOperationContract.Snap,
            Guid.Empty));
        Assert.False(RemoteObservationPolicy.ShouldAutoPresentCommittedSnap(
            hasCommandControl: false,
            AcquisitionOperationContract.Snap,
            null));
    }
}
