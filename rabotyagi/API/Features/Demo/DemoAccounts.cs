using rabotyagi.Core.Consts;

namespace rabotyagi.API.Features.Demo;

public sealed record DemoAccount(string Key, Guid Id, string Label, string Role);

public static class DemoAccounts
{
    public const string ClaimType = "rabotyagi:demo-account";
    public static readonly DemoAccount[] All =
    [
        new("client", Guid.Parse("d3000000-0000-0000-0000-000000000001"), "Клиент", RolesConstants.UserRole),
        new("worker1", Guid.Parse("d3000000-0000-0000-0000-000000000002"), "Исполнитель №1", RolesConstants.WorkerRole),
        new("worker2", Guid.Parse("d3000000-0000-0000-0000-000000000003"), "Исполнитель №2", RolesConstants.WorkerRole),
        new("dispatcher", Guid.Parse("d3000000-0000-0000-0000-000000000004"), "Диспетчер", RolesConstants.DispatcherRole),
        new("admin", Guid.Parse("d3000000-0000-0000-0000-000000000005"), "Администратор", RolesConstants.AdminRole),
        new("superadmin", Guid.Parse("d3000000-0000-0000-0000-000000000006"), "Суперадминистратор", RolesConstants.SuperAdminRole)
    ];
}
