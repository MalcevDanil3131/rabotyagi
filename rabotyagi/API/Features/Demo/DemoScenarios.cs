using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using rabotyagi.DataAccess;
using rabotyagi.DataAccess.Entities;
using rabotyagi.DataAccess.Enums;

namespace rabotyagi.API.Features.Demo;

// Explicitly fictional fixtures, called only by the guarded DemoSeeder.
public static class DemoScenarios
{
    public const string MarkerType = "rabotyagi:demo-scenarios";
    public const string Version = "client-acceptance-v2";

    public static async Task AddMissingAsync(AppDbContext db, IConfiguration config, List<string> files)
    {
        var client = DemoAccounts.All[0];
        if (await db.UserClaims.AnyAsync(x => x.UserId == client.Id && x.ClaimType == MarkerType && x.ClaimValue == Version)) return;
        var ids = DemoAccounts.All.Select(x => x.Id).ToArray();
        var names = await db.Users.Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => (x.Name + " " + x.Surname).Trim());
        var dispatcher = DemoAccounts.All[3];
        var admin = DemoAccounts.All[4];
        var address = await db.Addresses.FirstOrDefaultAsync(x => x.UserId == client.Id);
        if (address == null)
        {
            address = new DataAccess.Entities.Address { UserId = client.Id, Country = "Россия", City = "Екатеринбург",
                Street = "Демонстрационная", HouseNumber = "1" };
            db.Addresses.Add(address);
        }
        string[] titles = ["Новая заявка на модерации", "Взять заявку и предложить цену", "Исполнитель ещё не предложил цену",
            "Цена ждёт решения клиента", "Цена отклонена: предложить другую", "Цена согласована: можно начинать",
            "Работа по согласованной цене", "Клиенту: принять работу", "Исправить замечания клиента",
            "Клиенту: вернуть работу на доработку", "Работа принята клиентом", "Клиент отменил заявку"];
        decimal[] amounts = [0, 0, 0, 3200, 2800, 2500, 4500, 1800, 3600, 5400, 4000, 0];
        for (var i = 0; i < titles.Length; i++)
        {
            var worker = DemoAccounts.All[1 + i % 2];
            var at = DateTimeOffset.UtcNow.AddHours(-1).AddMinutes(i * 2);
            var order = new DataAccess.Entities.Order { InitiatorId = client.Id, Address = address,
                Title = "ДЕМО · " + titles[i], Comment = "Учебный сценарий прямой приёмки клиентом (v2). Все данные и события вымышленные.",
                AddressText = "Екатеринбург, Демонстрационная, 1 (демо)", OrderType = EOrderType.Other,
                ScheduledAt = DateTimeOffset.UtcNow.AddHours(2), Created = at, Updated = at };
            db.Orders.Add(order);
            void Event(EOrderHistoryEventType type, DemoAccount actor, EOrderStatus next, string? comment = null, decimal? price = null)
            {
                at = at.AddMinutes(1);
                db.OrderHistoryEvents.Add(new OrderHistoryEvent { Order = order, EventType = type, OccurredAt = at,
                    ActorUserId = actor.Id, ActorNameSnapshot = names[actor.Id],
                    PreviousStatus = type == EOrderHistoryEventType.Created ? null : order.Status, NewStatus = next,
                    ExecutorId = type == EOrderHistoryEventType.ExecutorAssigned ? worker.Id : null,
                    ExecutorNameSnapshot = type == EOrderHistoryEventType.ExecutorAssigned ? names[worker.Id] : null,
                    Comment = comment ?? "Учебное событие", PriceAmount = price });
                order.Status = next;
                order.Updated = at;
            }
            Event(EOrderHistoryEventType.Created, client, EOrderStatus.Created);
            if (i == 11)
            {
                Event(EOrderHistoryEventType.StatusChanged, client, EOrderStatus.Cancelled, "Учебная отмена: работа больше не нужна.");
                continue;
            }
            if (i >= 1) Event(EOrderHistoryEventType.StatusChanged, admin, EOrderStatus.Published);
            if (i >= 2)
            {
                order.ExecutorId = worker.Id;
                Event(EOrderHistoryEventType.ExecutorAssigned, dispatcher, EOrderStatus.WorkerOnTheWay);
            }
            if (i >= 3)
            {
                order.PriceAmount = amounts[i];
                order.PriceScope = "Работа и необходимые расходные материалы включены. Дополнительных платежей нет.";
                order.PriceStatus = EOrderPriceStatus.Proposed;
                order.PriceExecutorId = worker.Id;
                Event(EOrderHistoryEventType.PriceProposed, worker, order.Status, order.PriceScope, order.PriceAmount);
            }
            if (i == 4)
            {
                order.PriceStatus = EOrderPriceStatus.Rejected;
                Event(EOrderHistoryEventType.PriceRejected, client, order.Status, "Бюджет до 2 000 ₽. Предложите другой вариант.", order.PriceAmount);
            }
            if (i >= 5)
            {
                order.PriceStatus = EOrderPriceStatus.Accepted;
                Event(EOrderHistoryEventType.PriceAccepted, client, order.Status, order.PriceScope, order.PriceAmount);
                order.PriceAcceptedAt = at;
            }
            if (i >= 6) Event(EOrderHistoryEventType.StatusChanged, worker, EOrderStatus.InProgress);
            if (i >= 7)
            {
                Event(EOrderHistoryEventType.ReportSubmitted, worker, EOrderStatus.WorkFinished);
                order.ReportSubmittedAt = at;
                order.ReportSummary = "Демонстрационный отчёт: работа выполнена, результат проверен. Изображение — учебный образец.";
                order.WorkDone = order.WorkplaceClean = order.ClientInformed = true;
                var photo = new OrderPhoto { Order = order, OriginalName = "Учебный образец.png", ContentType = "image/png" };
                photo.FileName = photo.Id + ".png";
                var dir = config["Uploads:Path"]!;
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, photo.FileName);
                files.Add(path);
                await File.WriteAllBytesAsync(path, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jX1sAAAAASUVORK5CYII="));
                db.OrderPhotos.Add(photo);
            }
            if (i == 8 || i == 10)
            {
                order.ReviewFeedback = i == 8 ? "Добавьте понятное фото результата." : "Результат принят.";
                Event(i == 8 ? EOrderHistoryEventType.WorkReturnedByClient : EOrderHistoryEventType.WorkAcceptedByClient,
                    client, i == 8 ? EOrderStatus.Rework : EOrderStatus.Completed, order.ReviewFeedback, order.PriceAmount);
                order.ReviewedAt = at;
                order.ReviewerId = client.Id;
            }
        }
        // Saved atomically with the fixtures. Existing orders and account settings are never reset.
        db.UserClaims.Add(new IdentityUserClaim<Guid> { UserId = client.Id, ClaimType = MarkerType, ClaimValue = Version });
    }
}
