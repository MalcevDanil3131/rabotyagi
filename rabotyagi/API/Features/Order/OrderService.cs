using System.Net;
using Microsoft.EntityFrameworkCore;
using rabotyagi.API.Features.Order.DTOs;
using rabotyagi.API.Utils;
using rabotyagi.Core.Consts;
using rabotyagi.Core.Response;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Order;

public class OrderService(AppDbContext dbContext, ClaimsPrincipalUtils claimsPrincipal, IConfiguration configuration)
{
    private string UploadPath => configuration["Uploads:Path"] ?? Path.Combine(AppContext.BaseDirectory, "uploads");
    private bool IsAdmin => claimsPrincipal.IsAdminRole || claimsPrincipal.IsSuperAdmin;
    private bool IsDispatcher => claimsPrincipal.IsDispatcher || claimsPrincipal.IsSuperAdmin;
    private bool IsStaff => IsAdmin || IsDispatcher;

    private IQueryable<DataAccess.Entities.Order> VisibleOrders()
    {
        var orders = dbContext.Orders.AsQueryable();
        if (IsStaff)
            return orders;
        var userId = claimsPrincipal.ReqUserId;
        return orders.Where(x => x.InitiatorId == userId || x.ExecutorId == userId ||
            (claimsPrincipal.IsWorker && x.Status == EOrderStatus.Published));
    }

    private IQueryable<DataAccess.Entities.Order> WithDetails(IQueryable<DataAccess.Entities.Order> query) =>
        query.Include(x => x.Initiator).Include(x => x.Executor).Include(x => x.Address).Include(x => x.Photos);

    public async Task<ApiResponse<OrderDto>> CreateOrder(PlaceOrderDto dto)
    {
        var response = new ApiResponse<OrderDto>();
        var user = await dbContext.Users.Include(x => x.Address).FirstOrDefaultAsync(x => x.Id == claimsPrincipal.ReqUserId);
        if (user?.Address == null)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Сначала заполните адрес в профиле.");
        if (!Enum.IsDefined(dto.OrderType) || string.IsNullOrWhiteSpace(dto.Comment) || string.IsNullOrWhiteSpace(dto.AddressText))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Заполните описание, адрес и тип заявки.");
        if (dto.ScheduledAt.HasValue && dto.ScheduledAt < DateTimeOffset.UtcNow.AddMinutes(-5))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Выберите время в будущем.");
        var order = new DataAccess.Entities.Order
        {
            Initiator = user,
            Address = user.Address,
            AddressId = user.Address.Id,
            Comment = dto.Comment.Trim(),
            Title = dto.Title.Trim(),
            OrderType = dto.OrderType,
            AddressText = dto.AddressText.Trim(),
            ScheduledAt = dto.ScheduledAt?.ToUniversalTime()
        };
        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync();
        return response.WithStatusCode(HttpStatusCode.Created).WithData(MapToDto(order));
    }

    public async Task<ApiResponse> ChangeOrderStatus(ChangeOrderStatusDto dto)
    {
        var response = new ApiResponse();
        var order = await dbContext.Orders.FirstOrDefaultAsync(x => x.Id == dto.OrderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        var permitted = (IsAdmin && dto.Status is EOrderStatus.Published or EOrderStatus.Completed or EOrderStatus.Cancelled)
            || (claimsPrincipal.IsWorker && order.ExecutorId == claimsPrincipal.ReqUserId && dto.Status == EOrderStatus.InProgress);
        if (!permitted)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Нет прав на это действие.");
        var valid = (order.Status == EOrderStatus.Created && dto.Status is EOrderStatus.Published or EOrderStatus.Cancelled)
            || (order.Status == EOrderStatus.Approved && dto.Status == EOrderStatus.Completed)
            || (order.Status == EOrderStatus.WorkerOnTheWay && dto.Status == EOrderStatus.InProgress);
        if (!valid)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Этот переход статуса недоступен. Обновите заявку.");
        order.Status = dto.Status;
        return await SaveOrder(order);
    }

    public async Task<ApiResponse> SetExecutor(SetExecutorDto dto)
    {
        var response = new ApiResponse();
        if (!claimsPrincipal.IsWorker && !IsDispatcher)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Нет прав назначать исполнителя.");
        if (!IsDispatcher && dto.ExecutorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Можно взять заявку только себе.");
        var now = DateTimeOffset.UtcNow;
        var available = await (from u in dbContext.Users
                               join ur in dbContext.UserRoles on u.Id equals ur.UserId
                               join r in dbContext.Roles on ur.RoleId equals r.Id
                               where u.Id == dto.ExecutorId && r.Name == RolesConstants.WorkerRole && u.AvailableUntil > now
                                   && (!u.LockoutEnd.HasValue || u.LockoutEnd <= now)
                               select u.Id).AnyAsync();
        if (!available)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("У специалиста нет активного слота.");
        // Conditional UPDATE makes taking an order atomic: only one worker can win.
        var changed = await dbContext.Orders.Where(x => x.Id == dto.OrderId && x.Status == EOrderStatus.Published && x.ExecutorId == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExecutorId, dto.ExecutorId)
                .SetProperty(x => x.Status, EOrderStatus.WorkerOnTheWay).SetProperty(x => x.Updated, now));
        if (changed == 0)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка уже занята или не опубликована.");
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ChangeOrderDetails(ChangeOrderDto dto)
    {
        var response = new ApiResponse();
        var order = await dbContext.Orders.FirstOrDefaultAsync(x => x.Id == dto.OrderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        if (order.InitiatorId != claimsPrincipal.ReqUserId && !IsAdmin)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Нет прав редактировать заявку.");
        if (order.Status != EOrderStatus.Created)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Редактировать можно только заявку на модерации.");
        if (!Enum.IsDefined(dto.OrderType) || string.IsNullOrWhiteSpace(dto.Comment))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Заполните описание и тип заявки.");
        order.Comment = dto.Comment.Trim();
        order.OrderType = dto.OrderType;
        return await SaveOrder(order);
    }

    public async Task<ApiResponse<OrderDto>> GetOrder(Guid orderId)
    {
        var response = new ApiResponse<OrderDto>();
        var order = await WithDetails(VisibleOrders()).AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId);
        return order == null
            ? response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена или недоступна.")
            : response.WithStatusCode(HttpStatusCode.OK).WithData(MapToDto(order));
    }

    public async Task<ApiResponse<List<OrderDto>>> GetOrders(Guid? userId, int page = 1, int pageSize = 50)
    {
        var query = VisibleOrders();
        if (userId.HasValue)
            query = query.Where(x => x.InitiatorId == userId);
        var orders = await WithDetails(query).AsNoTracking().OrderByDescending(x => x.Created).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100000) - 1) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToListAsync();
        return new ApiResponse<List<OrderDto>>().WithStatusCode(HttpStatusCode.OK).WithData(orders.Select(MapToDto).ToList());
    }

    public async Task<ApiResponse> SubmitReport(Guid orderId, SubmitReportDto dto)
    {
        var response = new ApiResponse();
        var order = await dbContext.Orders.Include(x => x.Photos).FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        if (!claimsPrincipal.IsWorker || order.ExecutorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Отчёт отправляет назначенный специалист.");
        if (order.Status is not (EOrderStatus.InProgress or EOrderStatus.Rework))
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Сейчас нельзя отправить отчёт.");
        var kept = order.Photos.Where(x => dto.KeepPhotoIds.Contains(x.Id)).ToList();
        if (!dto.WorkDone || !dto.WorkplaceClean || !dto.ClientInformed || string.IsNullOrWhiteSpace(dto.Summary)
            || kept.Count + dto.Photos.Count is < 1 or > 4 || dto.KeepPhotoIds.Any(id => kept.All(x => x.Id != id)))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Заполните отчёт, три пункта проверки и добавьте от 1 до 4 фотографий.");
        // Validate file signatures, not just the client supplied MIME type.
        var types = new List<(string Extension, string ContentType)>();
        foreach (var file in dto.Photos)
        {
            if (file.Length is <= 0 or > 10 * 1024 * 1024)
                return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Фото должно быть от 1 байта до 10 МБ.");
            await using var stream = file.OpenReadStream();
            var header = new byte[12];
            var read = await stream.ReadAsync(header);
            if (read >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff)
                types.Add((".jpg", "image/jpeg"));
            else if (read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                types.Add((".png", "image/png"));
            else if (read == 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP")
                types.Add((".webp", "image/webp"));
            else
                return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Поддерживаются только JPG, PNG и WebP.");
        }
        Directory.CreateDirectory(UploadPath);
        var added = new List<OrderPhoto>();
        var removed = order.Photos.Except(kept).ToList();
        var saved = false;
        try
        {
            for (var i = 0; i < dto.Photos.Count; i++)
            {
                var photo = new OrderPhoto { OrderId = orderId, OriginalName = Path.GetFileName(dto.Photos[i].FileName), ContentType = types[i].ContentType };
                photo.FileName = photo.Id + types[i].Extension;
                added.Add(photo);
                await using var stream = File.Create(Path.Combine(UploadPath, photo.FileName));
                await dto.Photos[i].CopyToAsync(stream);
                dbContext.OrderPhotos.Add(photo);
            }
            dbContext.OrderPhotos.RemoveRange(removed);
            order.ReportSummary = dto.Summary.Trim();
            order.WorkDone = dto.WorkDone;
            order.WorkplaceClean = dto.WorkplaceClean;
            order.ClientInformed = dto.ClientInformed;
            order.ReportSubmittedAt = DateTimeOffset.UtcNow;
            order.ReviewFeedback = null;
            order.ReviewedAt = null;
            order.ReviewerId = null;
            order.Status = EOrderStatus.WorkFinished;
            order.Updated = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync();
            saved = true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка изменилась. Обновите её и повторите отправку.");
        }
        finally
        {
            foreach (var photo in saved ? removed : added)
                File.Delete(Path.Combine(UploadPath, photo.FileName));
        }
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ReviewReport(Guid orderId, ReviewReportDto dto)
    {
        var response = new ApiResponse();
        if (!IsDispatcher)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Отчёт проверяет диспетчер.");
        var order = await dbContext.Orders.FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        if (order.Status != EOrderStatus.WorkFinished || order.ReportSubmittedAt == null)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка не ожидает проверки отчёта.");
        if (!dto.Approved && string.IsNullOrWhiteSpace(dto.Feedback))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Укажите, что нужно исправить.");
        order.Status = dto.Approved ? EOrderStatus.Approved : EOrderStatus.Rework;
        order.ReviewFeedback = dto.Feedback?.Trim();
        order.ReviewedAt = DateTimeOffset.UtcNow;
        order.ReviewerId = claimsPrincipal.ReqUserId;
        return await SaveOrder(order);
    }

    public async Task<FileApiResponse> GetPhoto(Guid orderId, Guid photoId)
    {
        var response = new FileApiResponse();
        if (!await VisibleOrders().AnyAsync(x => x.Id == orderId))
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Фото недоступно.");
        var photo = await dbContext.OrderPhotos.FirstOrDefaultAsync(x => x.Id == photoId && x.OrderId == orderId);
        if (photo == null || !File.Exists(Path.Combine(UploadPath, photo.FileName)))
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Фото не найдено.");
        return response.WithStatusCode(HttpStatusCode.OK).WithFile(await File.ReadAllBytesAsync(Path.Combine(UploadPath, photo.FileName)), photo.OriginalName, photo.ContentType);
    }

    public async Task<ApiResponse<List<OrderMessageDto>>> GetMessages(Guid orderId)
    {
        var response = new ApiResponse<List<OrderMessageDto>>();
        var order = await VisibleOrders().AsNoTracking().FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка недоступна.");
        if (!IsStaff && order.InitiatorId != claimsPrincipal.ReqUserId && order.ExecutorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.OK).WithData([]);
        var messages = await dbContext.OrderMessages.Where(x => x.OrderId == orderId).OrderByDescending(x => x.Created)
            .Take(100).Select(x => new OrderMessageDto
            {
                Id = x.Id,
                SenderId = x.SenderId,
                SenderName = x.SenderName,
                Text = x.Text,
                Created = x.Created
            }).ToListAsync();
        return response.WithStatusCode(HttpStatusCode.OK).WithData(messages.OrderBy(x => x.Created).ToList());
    }

    public async Task<ApiResponse> SendMessage(Guid orderId, SendMessageDto dto)
    {
        var response = new ApiResponse();
        var order = await VisibleOrders().FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка недоступна.");
        if ((!IsStaff && order.ExecutorId != claimsPrincipal.ReqUserId && order.InitiatorId != claimsPrincipal.ReqUserId)
            || order.Status is EOrderStatus.Completed or EOrderStatus.Cancelled)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Обсуждение этой заявки недоступно.");
        if (string.IsNullOrWhiteSpace(dto.Text))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Введите сообщение.");
        var user = await dbContext.Users.FirstAsync(x => x.Id == claimsPrincipal.ReqUserId);
        dbContext.OrderMessages.Add(new OrderMessage
        {
            OrderId = orderId,
            SenderId = user.Id,
            SenderName = $"{user.Name} {user.Surname}",
            Text = dto.Text.Trim()
        });
        await dbContext.SaveChangesAsync();
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    private async Task<ApiResponse> SaveOrder(DataAccess.Entities.Order order)
    {
        var response = new ApiResponse();
        order.Updated = DateTimeOffset.UtcNow;
        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException) { return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка уже изменилась. Обновите данные."); }
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    private static OrderDto MapToDto(DataAccess.Entities.Order order) => new()
    {
        Id = order.Id,
        InitiatorId = order.InitiatorId,
        ExecutorId = order.ExecutorId,
        InitiatorName = $"{order.Initiator.Name} {order.Initiator.Surname}",
        InitiatorPhone = order.Initiator.PhoneNumber,
        ExecutorName = order.Executor == null ? null : $"{order.Executor.Name} {order.Executor.Surname}",
        ExecutorPhone = order.Executor?.PhoneNumber,
        Title = order.Title,
        AddressText = string.IsNullOrEmpty(order.AddressText) ? $"{order.Address.City}, {order.Address.Street}, {order.Address.HouseNumber}" : order.AddressText,
        ScheduledAt = order.ScheduledAt,
        Comment = order.Comment,
        OrderType = order.OrderType,
        Status = order.Status,
        Created = order.Created,
        Updated = order.Updated,
        ReportSummary = order.ReportSummary,
        WorkDone = order.WorkDone,
        WorkplaceClean = order.WorkplaceClean,
        ClientInformed = order.ClientInformed,
        ReportSubmittedAt = order.ReportSubmittedAt,
        ReviewFeedback = order.ReviewFeedback,
        ReviewedAt = order.ReviewedAt,
        Photos = order.Photos.Select(x => new OrderPhotoDto { Id = x.Id, Name = x.OriginalName, Url = $"/api/order/{order.Id}/photos/{x.Id}" }).ToList()
    };
}
