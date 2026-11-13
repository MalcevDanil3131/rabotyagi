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

public class OrderService(AppDbContext dbContext, ClaimsPrincipalUtils claimsPrincipal, IConfiguration configuration, PhotoFileCleanup photoCleanup)
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
        await AddHistory(order.Id, EOrderHistoryEventType.Created, null, order.Status);
        await dbContext.SaveChangesAsync();
        return response.WithStatusCode(HttpStatusCode.Created).WithData(MapToDto(order));
    }

    public async Task<ApiResponse> ChangeOrderStatus(ChangeOrderStatusDto dto)
    {
        var response = new ApiResponse();
        var order = await dbContext.Orders.FirstOrDefaultAsync(x => x.Id == dto.OrderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        var ownerCancellation = order.InitiatorId == claimsPrincipal.ReqUserId && dto.Status == EOrderStatus.Cancelled;
        var permitted = ownerCancellation || (IsAdmin && dto.Status is EOrderStatus.Published or EOrderStatus.Cancelled)
            || (claimsPrincipal.IsWorker && order.ExecutorId == claimsPrincipal.ReqUserId && dto.Status == EOrderStatus.InProgress);
        if (!permitted)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Нет прав на это действие.");
        if (ownerCancellation && order.Status is not (EOrderStatus.Created or EOrderStatus.Published or EOrderStatus.WorkerOnTheWay))
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Отменить заявку можно только до начала работ. Обновите заявку.");
        var valid = ownerCancellation
            || (order.Status == EOrderStatus.Created && dto.Status is EOrderStatus.Published or EOrderStatus.Cancelled)
            || (order.Status == EOrderStatus.WorkerOnTheWay && dto.Status == EOrderStatus.InProgress);
        if (!valid)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Этот переход статуса недоступен. Обновите заявку.");
        if (dto.Status == EOrderStatus.InProgress && (order.PriceStatus != EOrderPriceStatus.Accepted
            || order.PriceExecutorId != order.ExecutorId || order.PriceAmount is not > 0))
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Начать работу можно только после согласования стоимости клиентом.");
        await AddHistory(order.Id, EOrderHistoryEventType.StatusChanged, order.Status, dto.Status);
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
        var eligible = from u in dbContext.Users
                               join ur in dbContext.UserRoles on u.Id equals ur.UserId
                               join r in dbContext.Roles on ur.RoleId equals r.Id
                               where u.Id == dto.ExecutorId && r.Name == RolesConstants.WorkerRole && u.AvailableUntil > now
                                   && (!u.LockoutEnd.HasValue || u.LockoutEnd <= now)
                               select u.Id;
        // Conditional UPDATE makes taking an order atomic: only one worker can win.
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        var changed = await dbContext.Orders.Where(x => x.Id == dto.OrderId && x.Status == EOrderStatus.Published && x.ExecutorId == null && eligible.Any())
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ExecutorId, dto.ExecutorId)
                .SetProperty(x => x.Status, EOrderStatus.WorkerOnTheWay).SetProperty(x => x.Updated, now)
                .SetProperty(x => x.PriceStatus, EOrderPriceStatus.None).SetProperty(x => x.PriceAmount, (decimal?)null)
                .SetProperty(x => x.PriceScope, (string?)null).SetProperty(x => x.PriceExecutorId, (Guid?)null)
                .SetProperty(x => x.PriceAcceptedAt, (DateTimeOffset?)null));
        if (changed == 0)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка уже занята, не опубликована или специалист недоступен.");
        await AddHistory(dto.OrderId, EOrderHistoryEventType.ExecutorAssigned,
            EOrderStatus.Published, EOrderStatus.WorkerOnTheWay, executorId: dto.ExecutorId);
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ReassignExecutor(Guid orderId, ReassignExecutorDto dto)
    {
        var response = new ApiResponse();
        if (!IsDispatcher)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Заменить исполнителя может диспетчер.");
        if (dto.ExecutorId == Guid.Empty || dto.ExpectedExecutorId == Guid.Empty || !dto.ExpectedUpdated.HasValue
            || string.IsNullOrWhiteSpace(dto.Reason))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Выберите исполнителя и укажите причину замены.");
        var order = await dbContext.Orders.AsNoTracking().Include(x => x.Executor).FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        if (order.ExecutorId != dto.ExpectedExecutorId || order.Updated != dto.ExpectedUpdated.Value
            || order.Status is not (EOrderStatus.WorkerOnTheWay or EOrderStatus.InProgress or EOrderStatus.Rework))
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка изменилась или замена недоступна. Обновите данные.");
        if (order.ExecutorId == dto.ExecutorId)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Этот специалист уже назначен.");

        var now = DateTimeOffset.UtcNow;
        var eligible = from user in dbContext.Users
                       join ur in dbContext.UserRoles on user.Id equals ur.UserId
                       join role in dbContext.Roles on ur.RoleId equals role.Id
                       where user.Id == dto.ExecutorId && role.Name == RolesConstants.WorkerRole
                           && user.AvailableUntil > now && (!user.LockoutEnd.HasValue || user.LockoutEnd <= now)
                       select user.Id;
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        // Expected version protects against concurrent replacement, report, start or cancellation.
        var changed = await dbContext.Orders.Where(x => x.Id == orderId && x.ExecutorId == dto.ExpectedExecutorId
                && x.Updated == dto.ExpectedUpdated.Value && x.Status == order.Status && eligible.Any())
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExecutorId, dto.ExecutorId)
                .SetProperty(x => x.Status, EOrderStatus.WorkerOnTheWay).SetProperty(x => x.Updated, now)
                .SetProperty(x => x.PriceStatus, EOrderPriceStatus.None).SetProperty(x => x.PriceAmount, (decimal?)null)
                .SetProperty(x => x.PriceScope, (string?)null).SetProperty(x => x.PriceExecutorId, (Guid?)null)
                .SetProperty(x => x.PriceAcceptedAt, (DateTimeOffset?)null));
        if (changed == 0)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка изменилась или новый исполнитель недоступен. Обновите данные.");
        await AddHistory(orderId, EOrderHistoryEventType.ExecutorChanged, order.Status,
            EOrderStatus.WorkerOnTheWay, executorId: dto.ExecutorId, comment: dto.Reason.Trim(),
            previousExecutorId: order.ExecutorId,
            previousExecutorName: order.Executor == null ? null : $"{order.Executor.Name} {order.Executor.Surname}".Trim());
        await dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ProposePrice(Guid orderId, ProposePriceDto dto)
    {
        var response = new ApiResponse();
        var order = await VisibleOrders().FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null) return response.WithStatusCode(HttpStatusCode.NotFound);
        if (!claimsPrincipal.IsWorker || order.ExecutorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Стоимость предлагает назначенный исполнитель.");
        if (!dto.ExpectedUpdated.HasValue || dto.Amount is <= 0 or > 10000000
            || decimal.Round(dto.Amount, 2) != dto.Amount || string.IsNullOrWhiteSpace(dto.Scope) || dto.Scope.Length > 2000)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Укажите полную стоимость от 0,01 до 10 000 000 ₽, не более двух знаков после запятой, и состав работ.");
        if (order.Updated != dto.ExpectedUpdated || order.Status != EOrderStatus.WorkerOnTheWay
            || order.PriceStatus == EOrderPriceStatus.Accepted)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка изменилась или стоимость уже согласована. Обновите данные.");
        order.PriceAmount = dto.Amount;
        order.PriceScope = dto.Scope.Trim();
        order.PriceStatus = EOrderPriceStatus.Proposed;
        order.PriceExecutorId = order.ExecutorId;
        order.PriceAcceptedAt = null;
        await AddHistory(order.Id, EOrderHistoryEventType.PriceProposed, order.Status, order.Status,
            comment: order.PriceScope, priceAmount: order.PriceAmount);
        return await SaveOrder(order);
    }

    public async Task<ApiResponse> DecidePrice(Guid orderId, DecidePriceDto dto)
    {
        var response = new ApiResponse();
        var order = await VisibleOrders().FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null) return response.WithStatusCode(HttpStatusCode.NotFound);
        if (order.InitiatorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Стоимость согласовывает только клиент — автор заявки.");
        if (!dto.ExpectedUpdated.HasValue || !dto.Accepted.HasValue
            || (dto.Accepted == false && string.IsNullOrWhiteSpace(dto.Reason)) || dto.Reason?.Length > 2000)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Для отказа укажите причину (до 2000 символов).");
        if (order.Updated != dto.ExpectedUpdated || order.Status != EOrderStatus.WorkerOnTheWay
            || order.PriceStatus != EOrderPriceStatus.Proposed || order.PriceExecutorId != order.ExecutorId
            || order.PriceAmount is not > 0)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Предложение изменилось или уже рассмотрено. Обновите заявку и проверьте сумму.");
        order.PriceStatus = dto.Accepted.Value ? EOrderPriceStatus.Accepted : EOrderPriceStatus.Rejected;
        order.PriceAcceptedAt = dto.Accepted.Value ? DateTimeOffset.UtcNow : null;
        await AddHistory(order.Id, dto.Accepted.Value ? EOrderHistoryEventType.PriceAccepted : EOrderHistoryEventType.PriceRejected,
            order.Status, order.Status, comment: dto.Accepted.Value ? order.PriceScope : dto.Reason!.Trim(), priceAmount: order.PriceAmount);
        return await SaveOrder(order);
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

    public async Task<ApiResponse<List<OrderDto>>> GetOrders(Guid? userId, int page = 1, int pageSize = 50, EOrderStatus? status = null)
    {
        if (status.HasValue && !Enum.IsDefined(status.Value))
            return new ApiResponse<List<OrderDto>>().WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Неизвестный статус.");
        var query = VisibleOrders();
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);
        if (userId.HasValue)
            query = query.Where(x => x.InitiatorId == userId);
        var orders = await WithDetails(query).AsNoTracking().OrderByDescending(x => x.Created).ThenBy(x => x.Id)
            .Skip((Math.Clamp(page, 1, 100000) - 1) * Math.Clamp(pageSize, 1, 100)).Take(Math.Clamp(pageSize, 1, 100)).ToListAsync();
        return new ApiResponse<List<OrderDto>>().WithStatusCode(HttpStatusCode.OK).WithData(orders.Select(MapToDto).ToList());
    }

    public async Task<ApiResponse<List<OrderQueueCountDto>>> GetQueueCounts()
    {
        var response = new ApiResponse<List<OrderQueueCountDto>>();
        if (!IsStaff) return response.WithStatusCode(HttpStatusCode.Forbidden);
        var counts = await VisibleOrders().AsNoTracking().GroupBy(x => x.Status)
            .Select(x => new OrderQueueCountDto(x.Key, x.Count())).ToListAsync();
        return response.WithStatusCode(HttpStatusCode.OK).WithData(counts);
    }

    public async Task<ApiResponse<OrderPageDto>> GetOrderPage(OrderPageQuery input)
    {
        var response = new ApiResponse<OrderPageDto>();
        if (input.Status.HasValue && !Enum.IsDefined(input.Status.Value)
            || input.Scope is not ("all" or "mine" or "active" or "review"))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Неизвестный фильтр заявок.");
        // Keep items and counts consistent within this response, including concurrent mutations.
        await using var transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var visible = VisibleOrders().AsNoTracking();
        var counts = await visible.GroupBy(x => x.Status).Select(x => new OrderQueueCountDto(x.Key, x.Count())).ToListAsync();
        var id = claimsPrincipal.ReqUserId;
        var reviews = visible.Where(x => (x.Status == EOrderStatus.WorkFinished || x.Status == EOrderStatus.Approved)
            && (IsStaff || x.InitiatorId == id));
        int Count(EOrderStatus status) => counts.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        var stats = new OrderListStats(counts.Sum(x => x.Count), Count(EOrderStatus.WorkerOnTheWay) + Count(EOrderStatus.InProgress)
            + Count(EOrderStatus.Rework), await reviews.CountAsync(), Count(EOrderStatus.Completed));
        var query = input.Quality ? reviews : visible;
        query = input.Scope switch
        {
            "mine" => query.Where(x => x.InitiatorId == id || x.ExecutorId == id),
            "active" => query.Where(x => x.Status == EOrderStatus.WorkerOnTheWay || x.Status == EOrderStatus.InProgress || x.Status == EOrderStatus.Rework),
            "review" => query.Where(x => (x.Status == EOrderStatus.WorkFinished || x.Status == EOrderStatus.Approved) && (IsStaff || x.InitiatorId == id)),
            _ => query
        };
        if (input.Status.HasValue) query = query.Where(x => x.Status == input.Status.Value);
        var search = input.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.Title, pattern, "\\") || EF.Functions.ILike(x.AddressText, pattern, "\\")
                || EF.Functions.ILike(x.Initiator.Name + " " + x.Initiator.Surname, pattern, "\\")
                || EF.Functions.ILike(x.Id.ToString(), pattern, "\\"));
        }
        var total = await query.CountAsync();
        var page = Math.Min(input.Page, Math.Max(1, (int)Math.Ceiling(total / (double)input.PageSize)));
        var items = await WithDetails(query).OrderByDescending(x => x.Created).ThenBy(x => x.Id)
            .Skip((page - 1) * input.PageSize).Take(input.PageSize).ToListAsync();
        await transaction.CommitAsync();
        return response.WithStatusCode(HttpStatusCode.OK).WithData(new(items.Select(MapToDto).ToList(), page, input.PageSize, total, stats, counts));
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
            await AddHistory(order.Id, EOrderHistoryEventType.ReportSubmitted, order.Status,
                EOrderStatus.WorkFinished, comment: order.ReportSummary);
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
                await photoCleanup.DeleteOrDeferAsync(photo.FileName);
        }
        return response.WithStatusCode(HttpStatusCode.OK);
    }

    public async Task<ApiResponse> ReviewReport(Guid orderId, ReviewReportDto dto)
    {
        var response = new ApiResponse();
        var order = await VisibleOrders().FirstOrDefaultAsync(x => x.Id == orderId);
        if (order == null)
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена.");
        if (order.InitiatorId != claimsPrincipal.ReqUserId)
            return response.WithStatusCode(HttpStatusCode.Forbidden).WithErrorMsg("Работу принимает только клиент, создавший заявку.");
        if (!dto.Approved.HasValue || !dto.ExpectedUpdated.HasValue)
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Передайте решение и версию заявки.");
        if (order.Updated != dto.ExpectedUpdated.Value || order.Status is not (EOrderStatus.WorkFinished or EOrderStatus.Approved)
            || order.ReportSubmittedAt == null)
            return response.WithStatusCode(HttpStatusCode.Conflict).WithErrorMsg("Заявка или отчёт изменились. Обновите карточку перед приёмкой.");
        if (!dto.Approved.Value && string.IsNullOrWhiteSpace(dto.Feedback))
            return response.WithStatusCode(HttpStatusCode.BadRequest).WithErrorMsg("Укажите, что нужно исправить.");
        var newStatus = dto.Approved.Value ? EOrderStatus.Completed : EOrderStatus.Rework;
        await AddHistory(order.Id, dto.Approved.Value ? EOrderHistoryEventType.WorkAcceptedByClient : EOrderHistoryEventType.WorkReturnedByClient,
            order.Status, newStatus, comment: dto.Feedback?.Trim(), priceAmount: order.PriceAmount);
        order.Status = newStatus;
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

    public async Task<ApiResponse<List<OrderHistoryEventDto>>> GetHistory(Guid orderId)
    {
        var response = new ApiResponse<List<OrderHistoryEventDto>>();
        var visible = VisibleOrders().Where(x => x.Id == orderId);
        if (!await visible.AnyAsync())
            return response.WithStatusCode(HttpStatusCode.NotFound).WithErrorMsg("Заявка не найдена или недоступна.");
        var events = await dbContext.OrderHistoryEvents.AsNoTracking()
            .Where(x => x.OrderId == orderId && visible.Any(o => o.Id == x.OrderId))
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id)
            .Select(x => new OrderHistoryEventDto
            {
                Id = x.Id, OccurredAt = x.OccurredAt, EventType = x.EventType,
                ActorUserId = x.ActorUserId, ActorNameSnapshot = x.ActorNameSnapshot,
                PreviousStatus = x.PreviousStatus, NewStatus = x.NewStatus,
                ExecutorId = x.ExecutorId, ExecutorNameSnapshot = x.ExecutorNameSnapshot, Comment = x.Comment,
                PreviousExecutorId = x.PreviousExecutorId, PreviousExecutorNameSnapshot = x.PreviousExecutorNameSnapshot,
                PriceAmount = x.PriceAmount
            }).ToListAsync();
        return response.WithStatusCode(HttpStatusCode.OK).WithData(events);
    }

    private async Task AddHistory(Guid orderId, EOrderHistoryEventType type,
        EOrderStatus? previousStatus, EOrderStatus newStatus, Guid? executorId = null, string? comment = null,
        Guid? previousExecutorId = null, string? previousExecutorName = null, decimal? priceAmount = null)
    {
        var actor = await dbContext.Users.FirstAsync(x => x.Id == claimsPrincipal.ReqUserId);
        var executorName = executorId.HasValue
            ? await dbContext.Users.Where(x => x.Id == executorId).Select(x => x.Name + " " + x.Surname).FirstAsync()
            : null;
        dbContext.OrderHistoryEvents.Add(new OrderHistoryEvent
        {
            OrderId = orderId, EventType = type, ActorUserId = actor.Id,
            ActorNameSnapshot = $"{actor.Name} {actor.Surname}".Trim(),
            PreviousStatus = previousStatus, NewStatus = newStatus,
            ExecutorId = executorId, ExecutorNameSnapshot = executorName, Comment = comment,
            PreviousExecutorId = previousExecutorId, PreviousExecutorNameSnapshot = previousExecutorName,
            PriceAmount = priceAmount
        });
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
        PriceAmount = order.PriceAmount, PriceScope = order.PriceScope, PriceStatus = order.PriceStatus,
        PriceExecutorId = order.PriceExecutorId, PriceAcceptedAt = order.PriceAcceptedAt,
        Photos = order.Photos.Select(x => new OrderPhotoDto { Id = x.Id, Name = x.OriginalName, Url = $"/api/order/{order.Id}/photos/{x.Id}" }).ToList()
    };
}
