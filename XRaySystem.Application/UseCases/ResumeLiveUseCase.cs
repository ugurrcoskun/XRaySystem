using System.Threading.Tasks;
using XRaySystem.Domain.Interfaces;

namespace XRaySystem.Application.UseCases;

public class ResumeLiveUseCase
{
    private readonly ISensorService _sensorService;

    public ResumeLiveUseCase(ISensorService sensorService)
    {
        _sensorService = sensorService;
    }

    public async Task ExecuteAsync()
    {
        await _sensorService.ResumeLiveAsync();
    }
}
