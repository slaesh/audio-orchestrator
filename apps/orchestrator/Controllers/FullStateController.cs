using Microsoft.AspNetCore.Mvc;
using AudioOrchestrator.Services;
using AudioOrchestrator.Contracts;

namespace AudioOrchestrator.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FullStateController : ControllerBase
{
    private readonly FullStateBuilder _stateBuilder;

    public FullStateController(FullStateBuilder stateBuilder)
    {
        _stateBuilder = stateBuilder;
    }

    [HttpGet]
    public async Task<FullStateDto> Get()
    {
        return await _stateBuilder.BuildAsync();
    }
}
