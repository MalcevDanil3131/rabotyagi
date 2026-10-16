using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace rabotyagi.API.Conventions;

public class ApiExplorerGroupByNamespaceConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
        var controllerNamespace = controller.ControllerType.Namespace;
        var parts = controllerNamespace!.Split('.');
        controller.ApiExplorer.GroupName = parts[1];
    }
}