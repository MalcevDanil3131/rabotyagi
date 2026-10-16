# Контракт API для фронтенда

Все маршруты требуют cookie Identity, кроме регистрации и входа. Ошибки сервиса: `{ error: string }`; ошибки привязки DTO — стандартный ответ ASP.NET. Сервер проверяет роль, исполнителя и допустимый этап заявки. UI не назначает себе права.

## Аккаунт и адрес

| Метод | Маршрут | Данные |
| --- | --- | --- |
| POST | `/api/account/register` | имя, фамилия, email, телефон, пароль, isPersistent; создаётся клиент |
| POST | `/api/account/login` | email, password, isPersistent |
| POST | `/api/account/logout` | без тела |
| GET | `/api/account` | профиль, nullable address, roles, roleName, availableUntil |
| PATCH | `/api/account/password` | oldPassword, newPassword |
| GET / POST | `/api/address` | адрес текущего аккаунта / сохранение JSON адреса |

Роли Identity: `User`, `Worker`, `Dispatcher`, `Admin`, `SuperAdmin`. Основная роль сохраняется и в `RoleId`; полномочия проверяются по Identity. Смена роли или блокировка обновляет security stamp и отзывает прежнюю сессию.

## Заявки

| Метод | Маршрут | Поведение |
| --- | --- | --- |
| GET | `/api/order?page=1&pageSize=100` | очередь по роли, максимум 100 за страницу; необязательный userId фильтрует инициатора |
| GET | `/api/order/{id}` | доступная текущему аккаунту заявка |
| POST | `/api/order` | title, orderType, comment, addressText, scheduledAt; инициатор берётся из cookie; возвращает DTO с ID и 201 |
| PUT | `/api/order` | orderId, orderType, comment; описание до публикации меняет инициатор или администратор |
| PATCH | `/api/order/status` | orderId, status; публикация, отклонение, начало и закрытие |
| PATCH | `/api/order/executor` | orderId, executorId; работяга берёт себе, диспетчер назначает; требуется активный слот |
| POST | `/api/order/{id}/report` | multipart: Summary, WorkDone, ClientInformed, WorkplaceClean, Photos, KeepPhotoIds |
| POST | `/api/order/{id}/review` | approved, feedback; возврат требует комментарий |
| GET | `/api/order/{id}/photos/{photoId}` | авторизованная выдача файла |
| GET / POST | `/api/order/{id}/messages` | последние 100 сообщений / `{ text }`; закрытые заявки доступны только для чтения |

DTO заявки содержит ID, участников и телефоны, тип, статус, описание, заголовок, адрес, время, дату создания/обновления, отчёт, фото и комментарий проверки. У новых заявок `addressText` — снимок адреса, `scheduledAt` — UTC. Старые заявки без снимка используют существующий адрес из БД.

Клиент видит свои заявки. Работяга видит свои назначенные и опубликованные. Администратор и диспетчер видят общую очередь. Одновременное взятие защищено условным UPDATE; второй запрос получает 409. Другие изменения заявки используют optimistic concurrency по Updated.

| Из | В | Кто |
| --- | --- | --- |
| created | published / cancelled | Admin / SuperAdmin |
| published | workerOnTheWay | Worker со слотом / назначение Dispatcher |
| workerOnTheWay | inProgress | назначенный Worker |
| inProgress / rework | workFinished | назначенный Worker отправляет полный отчёт |
| workFinished | approved / rework | Dispatcher / SuperAdmin |
| approved | completed | Admin / SuperAdmin |

## Специалисты и пользователи

| Метод | Маршрут | Поведение |
| --- | --- | --- |
| GET | `/api/worker` | специалист видит себя; сотрудники — команду с доступностью и числом активных заявок |
| PUT | `/api/worker/availability` | `{ hours: 0..12 }`; 0 закрывает слот, остальные значения открывают/продлевают от текущего времени |
| GET | `/api/user?page=1&search=` | SuperAdmin, 50 пользователей за страницу |
| PUT | `/api/user/{id}/role` | SuperAdmin, `{ role }`, одна основная роль; собственную менять нельзя |
| PUT | `/api/user/{id}/blocked` | SuperAdmin, `{ blocked }`; собственный аккаунт блокировать нельзя |

Без дополнительных уровней архитектуры: контроллер → конкретный сервис → AppDbContext / UserManager. Worker и Users расположены в собственных папках Features с DTO. Фото лежат вне wwwroot в volume; отдельное публичное хранилище не используется.
