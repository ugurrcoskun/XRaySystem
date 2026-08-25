using System.Threading.Tasks;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Application.UseCases;

public class StartCaptureUseCase
{
    private readonly ISensorService _sensorService;

    public StartCaptureUseCase(ISensorService sensorService)
    {
        _sensorService = sensorService;
    }

    public async Task ExecuteAsync()
    {
        await _sensorService.StartCaptureAsync();
    }
}
