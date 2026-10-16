using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using rabotyagi.API.Features.Order.DTOs;
using rabotyagi.Core.Consts;
using rabotyagi.DataAccess.Entities;

namespace rabotyagi.API.Features.Order;

[ApiController]
[Route("api/order")]
[Authorize]
public class OrderApiController(OrderService service) : ControllerBase
{
    /// <summary>
    /// Создать заявку
    /// </summary>
    [HttpPost]
    public async Task<ActionResult> CreateOrder(PlaceOrderDto dto) =>
        await service.CreateOrder(dto);

    /// <summary>
    /// Изменить статус заявки
    /// </summary>
    [HttpPatch("status")]
    [Authorize(Roles = $"{RolesConstants.WorkerRole},{RolesConstants.DispatcherRole},{RolesConstants.AdminRole},{RolesConstants.SuperAdminRole}")]
    public async Task<ActionResult> ChangeOrderStatus(ChangeOrderStatusDto dto) =>
        await service.ChangeOrderStatus(dto);

    /// <summary>
    /// Изменить детали заказа
    /// </summary>
    [HttpPut]
    public async Task<ActionResult> ChangeOrderDetails(ChangeOrderDto dto) =>
        await service.ChangeOrderDetails(dto);

    /// <summary>
    /// Назначить исполнителя к заказу
    /// </summary>
    [HttpPatch("executor")]
    [Authorize(Roles = $"{RolesConstants.WorkerRole},{RolesConstants.DispatcherRole},{RolesConstants.SuperAdminRole}")]
    public async Task<ActionResult> SetExecutor(SetExecutorDto dto) =>
        await service.SetExecutor(dto);

    /// <summary>
    /// Получить заказ
    /// </summary>
    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult> GetOrder(Guid orderId) =>
        await service.GetOrder(orderId);

    /// <summary>
    /// Получить все заказы пользователя
    /// </summary>
    [HttpGet]
    public async Task<ActionResult> GetAllOrdersByUser(Guid? userId, int page = 1, int pageSize = 50) =>
        await service.GetOrders(userId, page, pageSize);

    [HttpPost("{orderId:guid}/report")]
    [Authorize(Roles = RolesConstants.WorkerRole)]
    [RequestSizeLimit(45 * 1024 * 1024)]
    public async Task<ActionResult> SubmitReport(Guid orderId, [FromForm] SubmitReportDto dto) =>
        await service.SubmitReport(orderId, dto);

    [HttpPost("{orderId:guid}/review")]
    [Authorize(Roles = $"{RolesConstants.DispatcherRole},{RolesConstants.SuperAdminRole}")]
    public async Task<ActionResult> ReviewReport(Guid orderId, ReviewReportDto dto) =>
        await service.ReviewReport(orderId, dto);

    [HttpGet("{orderId:guid}/photos/{photoId:guid}")]
    public async Task<ActionResult> GetPhoto(Guid orderId, Guid photoId) =>
        await service.GetPhoto(orderId, photoId);

    [HttpGet("{orderId:guid}/messages")]
    public async Task<ActionResult> GetMessages(Guid orderId) => await service.GetMessages(orderId);

    [HttpPost("{orderId:guid}/messages")]
    public async Task<ActionResult> SendMessage(Guid orderId, SendMessageDto dto) => await service.SendMessage(orderId, dto);
}
