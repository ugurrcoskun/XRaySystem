using XRaySystem.Domain.ValueObjects;
using XRaySystem.Infrastructure.Imaging;

namespace XRaySystem.Mobile.Services;

public class MauiWindowLevelRenderer
{
    public byte[] RenderToBmp(RawGray16Frame frame, int windowCenter, int windowWidth)
        => Gray8BmpEncoder.Encode(frame, windowCenter, windowWidth);
}
