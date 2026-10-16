using System.Net;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Worker.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Worker;

public class WorkerService(AppDbContext context, ClaimsPrincipalUtils claimsPrincipal)
{
    public async Task<ApiResponse<List<WorkerDto>>> GetWorkers()
    {
        var query = from user in context.Users
                    join ur in context.UserRoles on user.Id equals ur.UserId
                    join role in context.Roles on ur.RoleId equals role.Id
                    where role.Name == RolesConstants.WorkerRole && (!user.LockoutEnd.HasValue || user.LockoutEnd <= DateTimeOffset.UtcNow)
                    select user;
        if (!claimsPrincipal.IsAdminRole && !claimsPrincipal.IsDispatcher && !claimsPrincipal.IsSuperAdmin)
            query = query.Where(x => x.Id == claimsPrincipal.ReqUserId);
        var workers = await query.OrderBy(x => x.Name).Select(x => new WorkerDto
        {
            Id = x.Id,
            Name = x.Name + " " + x.Surname,
            PhoneNumber = x.PhoneNumber,
            AvailableUntil = x.AvailableUntil,
            ActiveOrders = x.ExecutedOrders.Count(o => o.Status != EOrderStatus.Completed && o.Status != EOrderStatus.Cancelled)
        }).ToListAsync();
        return new ApiResponse<List<WorkerDto>>().WithStatusCode(HttpStatusCode.OK).WithData(workers);
    }

    public async Task<ApiResponse> SetAvailability(SetAvailabilityDto dto)
    {
        var user = await context.Users.FirstAsync(x => x.Id == claimsPrincipal.ReqUserId);
        user.AvailableUntil = dto.Hours == 0 ? null : DateTimeOffset.UtcNow.AddHours(dto.Hours);
        user.Updated = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync();
        return new ApiResponse().WithStatusCode(HttpStatusCode.OK);
    }
}
