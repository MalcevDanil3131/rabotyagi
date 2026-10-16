namespace rabotyagi.Core.Consts;

public class RolesConstants
{
    public const string UserRole = "User";
    public const string DispatcherRole = "Dispatcher";
    public const string WorkerRole = "Worker";
    public const string AdminRole = "Admin";
    public const string SuperAdminRole = "SuperAdmin";
    public static readonly string[] AllRoles = [UserRole, DispatcherRole, WorkerRole, AdminRole, SuperAdminRole];
}