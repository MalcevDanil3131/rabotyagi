using System.Security.Claims;
using rabotyagi.Core.Consts;

namespace rabotyagi.API.Utils;

public class ClaimsPrincipalUtils(IHttpContextAccessor httpContextAccessor)
{
    private ClaimsPrincipal User => httpContextAccessor.HttpContext!.User;
    
    public Guid? ReqUserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null;
    
    public bool IsDispatcher => User.IsInRole(RolesConstants.DispatcherRole);
    public bool IsAdminRole => User.IsInRole(RolesConstants.AdminRole);
    public bool IsSuperAdmin => User.IsInRole(RolesConstants.SuperAdminRole);
    public bool IsWorker => User.IsInRole(RolesConstants.WorkerRole);
}