using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using rabotyagi.API.Features.Worker.DTOs;
using rabotyagi.Core.Consts;

namespace rabotyagi.API.Features.Worker;

[ApiController]
[Route("api/worker")]
[Authorize(Roles = $"{RolesConstants.WorkerRole},{RolesConstants.DispatcherRole},{RolesConstants.AdminRole},{RolesConstants.SuperAdminRole}")]
public class WorkerApiController(WorkerService service) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> GetWorkers() => await service.GetWorkers();

    [HttpPut("availability")]
    [Authorize(Roles = RolesConstants.WorkerRole)]
    public async Task<ActionResult> SetAvailability(SetAvailabilityDto dto) => await service.SetAvailability(dto);
}
