import { useCallback, useEffect, useState } from 'react';
import {
  ClipboardList,
  UsersRound,
  ShieldCheck,
  Settings2,
  Plus,
  Radio,
  RefreshCw,
  LogOut,
  Phone,
  Send,
  MapPin,
  Clock3,
  ArrowRight,
  UserRound,
} from 'lucide-react';
import { api, ApiError, photoUrl } from './api';
import type { Account, Address, ApiOrder, ApiWorker, ApiUser, ApiMessage, ApiStatus } from './api';
import { Avatar, Badge, Dialog, Empty, OrdersTable, SearchField } from './components';
import { CreateOrder, ReportForm } from './OrderDialogs';
import { AddressForm, PasswordForm } from './LiveWorkspace';
import { categories, formatDate, formatTime, statuses } from './domain';
import type { Order, Worker, Status, Report, Photo } from './domain';

const roleLabels: Record<string, string> = {
  User: 'Клиент',
  Worker: 'Работяга',
  Dispatcher: 'Диспетчер',
  Admin: 'Администратор',
  SuperAdmin: 'Суперадминистратор',
};
const statusMap: Record<ApiStatus, Status> = {
  created: 'new',
  published: 'published',
  workerOnTheWay: 'onTheWay',
  inProgress: 'inProgress',
  workFinished: 'report',
  rework: 'rework',
  approved: 'approved',
  completed: 'completed',
  cancelled: 'cancelled',
};
function toOrder(o: ApiOrder): Order {
  return {
    id: o.id,
    number: o.id.slice(0, 8),
    title: o.title || categories[o.orderType],
    type: o.orderType,
    status: statusMap[o.status],
    clientId: o.initiatorId,
    clientName: o.initiatorName,
    address: o.addressText,
    comment: o.comment,
    scheduledAt: o.scheduledAt,
    createdAt: o.created,
    workerId: o.executorId || undefined,
    events: [],
    report: o.reportSubmittedAt
      ? {
          summary: o.reportSummary || '',
          checks: [o.workDone, o.clientInformed, o.workplaceClean],
          photos: o.photos.map((p) => ({ ...p, url: photoUrl(p.url) })),
          submittedAt: o.reportSubmittedAt,
          feedback: o.reviewFeedback || undefined,
        }
      : undefined,
  };
}
function toWorker(w: ApiWorker): Worker {
  return {
    id: w.id,
    name: w.name,
    phone: w.phoneNumber || '',
    specialty: 'Выездной специалист',
    initials: w.name
      .split(' ')
      .map((x) => x[0])
      .join('')
      .slice(0, 2),
    color: 'sage',
    slotUntil: w.availableUntil || undefined,
  };
}
function addressText(a: Address | null) {
  return a?.street && a.houseNumber
    ? [a.city, `${a.street}, ${a.houseNumber}`, a.apartmentNumber ? `кв. ${a.apartmentNumber}` : '']
        .filter(Boolean)
        .join(', ')
    : '';
}
function available(until: string | null) {
  return !!until && Date.parse(until) > Date.now();
}
type View = 'orders' | 'team' | 'quality' | 'users' | 'settings';
const views = {
  orders: ['Заявки', ClipboardList],
  team: ['Специалисты', UsersRound],
  quality: ['Контроль качества', ShieldCheck],
  users: ['Пользователи', UserRound],
  settings: ['Настройки', Settings2],
} as const;

export default function ConnectedDashboard({
  account,
  onAccount,
  onLogout,
  onExpired,
}: {
  account: Account;
  onAccount: (a: Account) => void;
  onLogout: () => Promise<void>;
  onExpired: () => void;
}) {
  const [view, setView] = useState<View>('orders');
  const [orders, setOrders] = useState<ApiOrder[]>([]);
  const [workers, setWorkers] = useState<ApiWorker[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [more, setMore] = useState(false);
  const [selected, setSelected] = useState<string | null>(null);
  const [reportId, setReportId] = useState<string | null>(null);
  const [modal, setModal] = useState<'create' | 'address' | 'password' | 'slot' | null>(null);
  const [slotHours, setSlotHours] = useState(4);
  const [, setClock] = useState(0);
  const roles = account.roles || [account.roleName];
  const superAdmin = roles.includes('SuperAdmin');
  const admin = superAdmin || roles.includes('Admin');
  const dispatcher = superAdmin || roles.includes('Dispatcher');
  const worker = roles.includes('Worker');
  const staff = admin || dispatcher;
  const nav: View[] = [
    'orders',
    ...(worker || staff ? ['team' as const] : []),
    ...(staff ? ['quality' as const] : []),
    ...(superAdmin ? ['users' as const] : []),
    'settings',
  ];
  const fail = useCallback(
    (err: unknown) => {
      if (err instanceof ApiError && err.status === 401) onExpired();
      setError(err instanceof Error ? err.message : 'Не удалось выполнить запрос.');
    },
    [onExpired],
  );
  const refresh = useCallback(
    async (silent = false) => {
      if (!silent) setLoading(true);
      try {
        const [o, w] = await Promise.all([
          api.orders(),
          worker || staff ? api.workers() : Promise.resolve([]),
        ]);
        setOrders(o);
        setWorkers(w);
        setPage(1);
        setMore(o.length === 100);
        if (!silent) setError('');
      } catch (err) {
        fail(err);
      } finally {
        if (!silent) setLoading(false);
      }
    },
    [worker, staff, fail],
  );
  useEffect(() => {
    void refresh();
  }, [account.id, worker, staff]);
  useEffect(() => {
    const id = window.setInterval(() => {
      setClock((x) => x + 1);
      if (!document.hidden && !busy && !modal && !reportId && page === 1) void refresh(true);
    }, 15000);
    return () => clearInterval(id);
  }, [account.id, worker, staff, busy, modal, reportId, page]);
  useEffect(() => {
    if (!notice) return;
    const id = setTimeout(() => setNotice(''), 5000);
    return () => clearTimeout(id);
  }, [notice]);
  async function run(action: () => Promise<unknown>, message = 'Сохранено.') {
    setBusy(true);
    setError('');
    try {
      await action();
      setNotice(message);
      await refresh(true);
    } catch (err) {
      fail(err);
      throw err;
    } finally {
      setBusy(false);
    }
  }
  async function loadMore() {
    setLoading(true);
    try {
      const data = await api.orders(page + 1);
      setOrders((old) => [...old, ...data.filter((o) => !old.some((x) => x.id === o.id))]);
      setPage((p) => p + 1);
      setMore(data.length === 100);
    } catch (err) {
      fail(err);
    } finally {
      setLoading(false);
    }
  }
  const active = orders.filter((o) =>
    ['workerOnTheWay', 'inProgress', 'rework'].includes(o.status),
  );
  const reviews = orders.filter(
    (o) => (dispatcher && o.status === 'workFinished') || (admin && o.status === 'approved'),
  );
  const shown = orders
    .filter((o) => view !== 'quality' || reviews.includes(o))
    .filter(
      (o) =>
        filter === 'all' ||
        (filter === 'mine' && (o.initiatorId === account.id || o.executorId === account.id)) ||
        (filter === 'active' && active.includes(o)) ||
        (filter === 'review' && reviews.includes(o)) ||
        o.status === filter,
    )
    .filter((o) =>
      `${o.title} ${o.id} ${o.addressText} ${o.initiatorName}`
        .toLowerCase()
        .includes(search.toLowerCase()),
    );
  const chosen = orders.find((o) => o.id === selected);
  const reporting = orders.find((o) => o.id === reportId);
  const avatars = workers.map(toWorker);
  for (const o of orders)
    if (o.executorId && !avatars.some((w) => w.id === o.executorId))
      avatars.push(
        toWorker({
          id: o.executorId,
          name: o.executorName || 'Специалист',
          phoneNumber: o.executorPhone,
          availableUntil: null,
          activeOrders: 0,
        }),
      );
  async function submitReport(report: Report) {
    if (!reporting) return;
    const form = new FormData();
    form.append('Summary', report.summary);
    form.append('WorkDone', String(report.checks[0]));
    form.append('ClientInformed', String(report.checks[1]));
    form.append('WorkplaceClean', String(report.checks[2]));
    for (const photo of report.photos) {
      if (reporting.photos.some((p) => p.id === photo.id)) form.append('KeepPhotoIds', photo.id);
      else {
        const blob = await (await fetch(photo.url)).blob();
        form.append('Photos', blob, 'report.jpg');
      }
    }
    await run(() => api.report(reporting.id, form), 'Отчёт отправлен диспетчеру.');
    setReportId(null);
  }
  return (
    <div className="app-shell connected">
      <aside className="sidebar">
        <span className="brand">
          <span className="brand-mark">р</span>работяги<span className="brand-period">.</span>
        </span>
        <div className="workspace-label">
          <span className="workspace-icon">
            <ClipboardList size={18} />
          </span>
          <div>
            Рабочее пространство<small>{roles.map((r) => roleLabels[r] || r).join(', ')}</small>
          </div>
        </div>
        <nav aria-label="Основная навигация">
          {nav.map((key) => {
            const [label, Icon] = views[key];
            return (
              <button
                key={key}
                className={`nav-item ${view === key ? 'selected' : ''}`}
                aria-current={view === key ? 'page' : undefined}
                onClick={() => {
                  setView(key);
                  setFilter('all');
                  setSearch('');
                }}
              >
                <Icon size={19} />
                <span>{worker && key === 'team' ? 'Мой слот' : label}</span>
                {key === 'quality' && reviews.length > 0 && (
                  <span className="nav-count">{reviews.length}</span>
                )}
              </button>
            );
          })}
        </nav>
        <div className="sidebar-bottom">
          <button className="profile-button" onClick={() => setView('settings')}>
            <span className="avatar profile-avatar">
              {account.name[0]}
              {account.surname[0]}
            </span>
            <span>
              <strong>
                {account.name} {account.surname}
              </strong>
              <small>{roleLabels[account.roleName] || 'Аккаунт'}</small>
            </span>
          </button>
        </div>
      </aside>
      <div className="main-shell">
        <header className="topbar">
          <div className="breadcrumb">
            <span>Рабочее пространство</span>
            <span>/</span>
            <strong>{views[view][0]}</strong>
          </div>
          <div className="topbar-actions">
            <button
              className="icon-button"
              aria-label="Обновить данные"
              disabled={busy || loading}
              onClick={() => void refresh()}
            >
              <RefreshCw size={18} />
            </button>
            <button
              className="icon-button"
              aria-label="Выйти"
              disabled={busy}
              onClick={() => void run(onLogout).catch(() => {})}
            >
              <LogOut size={18} />
            </button>
          </div>
        </header>
        <main className="main-content">
          <div className="page-heading">
            <div>
              <span className="greeting">Здравствуйте, {account.name}</span>
              <h1>{worker && view === 'team' ? 'Мой рабочий слот' : views[view][0]}</h1>
              <p>
                {view === 'orders'
                  ? 'Заявки, которые доступны вам по роли.'
                  : view === 'quality'
                    ? 'Проверьте отчёты и подтвердите результат.'
                    : 'Вся нужная информация — здесь.'}
              </p>
            </div>
            {view === 'orders' && (
              <button
                className="button primary"
                onClick={() => setModal(addressText(account.address) ? 'create' : 'address')}
              >
                <Plus size={18} />
                Создать заявку
              </button>
            )}
            {worker && view === 'team' && (
              <button className="button primary" onClick={() => setModal('slot')}>
                <Radio size={18} />
                {available(account.availableUntil) ? 'Управлять слотом' : 'Открыть слот'}
              </button>
            )}
          </div>
          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}
          {notice && (
            <p className="success" role="status">
              {notice}
            </p>
          )}
          {(view === 'orders' || view === 'quality') && (
            <>
              <div className="metrics live-metrics">
                {[
                  ['Всего загружено', orders.length, 'all'],
                  ['В работе', active.length, 'active'],
                  ['На проверке', reviews.length, 'review'],
                  ['Закрыто', orders.filter((o) => o.status === 'completed').length, 'completed'],
                ].map(([label, count, f]) => (
                  <button className="metric" key={label} onClick={() => setFilter(String(f))}>
                    <span className="metric-top">{label}</span>
                    <strong className="metric-value">{count}</strong>
                  </button>
                ))}
              </div>
              <section className="panel">
                <header className="panel-heading">
                  <h2>{view === 'quality' ? 'Приёмка работ' : 'Очередь заявок'}</h2>
                  <span className="count">{shown.length}</span>
                </header>
                <div className="orders-toolbar">
                  <div className="tabs">
                    <button
                      className={filter === 'all' ? 'active' : ''}
                      onClick={() => setFilter('all')}
                    >
                      Все
                    </button>
                    <button
                      className={filter === 'mine' ? 'active' : ''}
                      onClick={() => setFilter('mine')}
                    >
                      Мои
                    </button>
                    <button
                      className={filter === 'active' ? 'active' : ''}
                      onClick={() => setFilter('active')}
                    >
                      В работе
                    </button>
                    <label className="live-filter">
                      <span className="sr-only">Статус заявки</span>
                      <select
                        aria-label="Статус заявки"
                        value={['all', 'mine', 'active', 'review'].includes(filter) ? '' : filter}
                        onChange={(e) => setFilter(e.target.value || 'all')}
                      >
                        <option value="">Все статусы</option>
                        {Object.entries(statusMap).map(([key, s]) => (
                          <option key={key} value={key}>
                            {statuses[s]}
                          </option>
                        ))}
                      </select>
                    </label>
                  </div>
                  <SearchField value={search} onChange={setSearch} />
                </div>
                {loading && !orders.length ? (
                  <p className="loading" role="status">
                    Загружаем заявки…
                  </p>
                ) : shown.length ? (
                  <OrdersTable
                    orders={shown.map(toOrder)}
                    workers={avatars}
                    onSelect={setSelected}
                  />
                ) : (
                  <Empty
                    title="Заявок пока нет"
                    text="Здесь появятся ваши заявки. Попробуйте изменить фильтр."
                  />
                )}
                {more && (
                  <div className="panel-heading">
                    <button
                      className="button secondary"
                      disabled={loading}
                      onClick={() => void loadMore()}
                    >
                      Загрузить ещё
                    </button>
                  </div>
                )}
              </section>
            </>
          )}
          {view === 'team' && (
            <>
              {worker && (
                <section className="panel slot-panel">
                  <h2>
                    {available(account.availableUntil)
                      ? `Принимаю заявки до ${formatTime(account.availableUntil!)}`
                      : 'Слот закрыт'}
                  </h2>
                  <p className="muted">
                    Откройте слот, чтобы брать опубликованные заявки. Закрытие слота не снимает
                    текущие заявки.
                  </p>
                  <button className="button primary" onClick={() => setModal('slot')}>
                    Настроить слот
                  </button>
                </section>
              )}
              <div className="team-grid">
                {workers.map((w) => (
                  <article className="panel worker-card" key={w.id}>
                    <div className="worker-card-header">
                      <Avatar worker={toWorker(w)} />
                      <span
                        className={`availability ${available(w.availableUntil) ? 'online' : ''}`}
                      >
                        <span />
                        {available(w.availableUntil) ? 'Принимает заявки' : 'Слот закрыт'}
                      </span>
                    </div>
                    <h2>{w.name}</h2>
                    <div className="worker-card-facts">
                      <span>
                        Активных заявок<strong>{w.activeOrders}</strong>
                      </span>
                      <span>
                        Слот до
                        <strong>
                          {available(w.availableUntil) ? formatTime(w.availableUntil!) : '—'}
                        </strong>
                      </span>
                    </div>
                    {orders
                      .filter(
                        (o) =>
                          o.executorId === w.id && !['completed', 'cancelled'].includes(o.status),
                      )
                      .map((o) => (
                        <button
                          className="text-button block"
                          key={o.id}
                          onClick={() => setSelected(o.id)}
                        >
                          {o.title || categories[o.orderType]} · {statuses[statusMap[o.status]]}
                        </button>
                      ))}
                    {w.phoneNumber && (
                      <a className="button secondary" href={`tel:${w.phoneNumber}`}>
                        <Phone size={16} />
                        Позвонить
                      </a>
                    )}
                  </article>
                ))}
              </div>
              {!workers.length && (
                <Empty
                  title="Специалистов пока нет"
                  text="Суперадминистратор может назначить роль работяги зарегистрированному пользователю."
                />
              )}
            </>
          )}
          {view === 'users' && superAdmin && <UsersPanel currentId={account.id} run={run} />}
          {view === 'settings' && (
            <section className="panel profile-panel">
              <h2>
                {account.name} {account.surname}
              </h2>
              <p>
                {account.email} · {account.phoneNumber}
              </p>
              <p className="muted">{roles.map((r) => roleLabels[r] || r).join(', ')}</p>
              <div className="profile-strip">
                <div>
                  <MapPin size={18} />
                  {addressText(account.address) || 'Адрес пока не заполнен'}
                </div>
              </div>
              <div className="inline-actions">
                <button className="button secondary" onClick={() => setModal('address')}>
                  Изменить адрес
                </button>
                <button className="button secondary" onClick={() => setModal('password')}>
                  Сменить пароль
                </button>
              </div>
            </section>
          )}
        </main>
      </div>
      <nav className="mobile-nav connected-mobile" aria-label="Мобильная навигация">
        {nav.map((key) => {
          const [label, Icon] = views[key];
          return (
            <button
              key={key}
              className={view === key ? 'active' : ''}
              aria-current={view === key ? 'page' : undefined}
              onClick={() => {
                setView(key);
                setFilter('all');
                setSearch('');
              }}
            >
              <Icon size={20} />
              <span>
                {key === 'quality' ? 'Качество' : worker && key === 'team' ? 'Слот' : label}
              </span>
            </button>
          );
        })}
      </nav>
      {modal === 'create' && (
        <CreateOrder
          defaultAddress={addressText(account.address)}
          onClose={() => setModal(null)}
          onSave={async (input) => {
            await run(
              () =>
                api.createOrder({
                  title: input.title,
                  orderType: input.type,
                  comment: input.comment,
                  addressText: input.address,
                  scheduledAt: input.scheduledAt ? new Date(input.scheduledAt).toISOString() : null,
                }),
              'Заявка отправлена на модерацию.',
            );
          }}
        />
      )}
      {modal === 'address' && (
        <AddressForm
          address={account.address}
          onClose={() => setModal(null)}
          onSave={async (a) => {
            await run(async () => {
              await api.saveAddress(a);
              onAccount(await api.account());
            }, 'Адрес сохранён.');
            setModal(null);
          }}
        />
      )}
      {modal === 'password' && (
        <PasswordForm
          onClose={() => setModal(null)}
          onSave={async (oldPassword, newPassword) => {
            await run(() => api.changePassword(oldPassword, newPassword), 'Пароль изменён.');
            setModal(null);
          }}
        />
      )}
      {modal === 'slot' && (
        <Dialog title="Мой рабочий слот" onClose={() => setModal(null)}>
          <div className="dialog-body form">
            <p className="muted">Выберите, на сколько часов вы готовы принимать новые заявки.</p>
            <div className="slot-options">
              {[2, 4, 6, 8].map((h) => (
                <button
                  key={h}
                  className={`button ${h === slotHours ? 'primary' : 'secondary'}`}
                  onClick={() => setSlotHours(h)}
                >
                  {h} ч
                </button>
              ))}
            </div>
            <div className="form-footer">
              <button
                className="button secondary"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    await api.availability(0);
                    onAccount(await api.account());
                    setModal(null);
                  }, 'Слот закрыт.').catch(() => {})
                }
              >
                Закрыть слот
              </button>
              <button
                className="button primary"
                disabled={busy}
                onClick={() =>
                  void run(async () => {
                    await api.availability(slotHours);
                    onAccount(await api.account());
                    setModal(null);
                  }, 'Слот открыт.').catch(() => {})
                }
              >
                Открыть на {slotHours} ч
              </button>
            </div>
          </div>
        </Dialog>
      )}
      {chosen && !reporting && (
        <LiveDetails
          key={chosen.id}
          order={chosen}
          account={account}
          workers={workers}
          busy={busy}
          run={run}
          onClose={() => setSelected(null)}
          onReport={() => setReportId(chosen.id)}
        />
      )}
      {reporting && (
        <ReportForm
          order={toOrder(reporting)}
          onClose={() => setReportId(null)}
          onSave={submitReport}
        />
      )}
    </div>
  );
}

function LiveDetails({
  order,
  account,
  workers,
  busy,
  run,
  onClose,
  onReport,
}: {
  order: ApiOrder;
  account: Account;
  workers: ApiWorker[];
  busy: boolean;
  run: (action: () => Promise<unknown>, message?: string) => Promise<void>;
  onClose: () => void;
  onReport: () => void;
}) {
  const [messages, setMessages] = useState<ApiMessage[]>([]);
  const [text, setText] = useState('');
  const [feedback, setFeedback] = useState('');
  const [rework, setRework] = useState(false);
  const [error, setError] = useState('');
  const [zoom, setZoom] = useState<Photo | null>(null);
  const [executor, setExecutor] = useState('');
  const [edit, setEdit] = useState(false);
  const [comment, setComment] = useState(order.comment);
  const [type, setType] = useState(order.orderType);
  const roles = account.roles || [account.roleName];
  const admin = roles.includes('Admin') || roles.includes('SuperAdmin');
  const dispatcher = roles.includes('Dispatcher') || roles.includes('SuperAdmin');
  const worker = roles.includes('Worker');
  const mine = order.executorId === account.id;
  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const m = await api.messages(order.id);
        if (active) setMessages(m);
      } catch (err) {
        if (active)
          setError(err instanceof Error ? err.message : 'Не удалось загрузить обсуждение.');
      }
    }
    void load();
    const timer = setInterval(() => {
      if (!document.hidden) void load();
    }, 10000);
    return () => {
      active = false;
      clearInterval(timer);
    };
  }, [order.id]);
  async function act(fn: () => Promise<unknown>, message: string) {
    setError('');
    try {
      await run(fn, message);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось сохранить.');
    }
  }
  const open = !['completed', 'cancelled'].includes(order.status);
  const canDiscuss = open && (admin || dispatcher || mine || order.initiatorId === account.id);
  return (
    <Dialog
      title={order.title || categories[order.orderType]}
      eyebrow={`Заявка #${order.id.slice(0, 8)}`}
      onClose={onClose}
      drawer
    >
      <div className="dialog-body details">
        <Badge status={statusMap[order.status]} />
        <div className="detail-facts">
          <div>
            <MapPin size={19} />
            <span>{order.addressText}</span>
          </div>
          <div>
            <Clock3 size={19} />
            <span>
              {order.scheduledAt
                ? `${formatDate(order.scheduledAt)}, ${formatTime(order.scheduledAt)}`
                : 'Время согласуется с клиентом'}
            </span>
          </div>
          <div>
            <UserRound size={19} />
            <span>
              {order.initiatorName}
              {order.initiatorPhone && (
                <a className="muted block" href={`tel:${order.initiatorPhone}`}>
                  {order.initiatorPhone}
                </a>
              )}
            </span>
          </div>
        </div>
        <section className="detail-section">
          <h3>Детали заявки</h3>
          <p className="preserve-lines">{order.comment}</p>
          {order.status === 'created' && (admin || order.initiatorId === account.id) && (
            <button className="text-button" onClick={() => setEdit(!edit)}>
              Редактировать описание
            </button>
          )}
          {edit && (
            <form
              className="form"
              onSubmit={async (e) => {
                e.preventDefault();
                await act(async () => {
                  await api.editOrder(order.id, type, comment);
                  setEdit(false);
                }, 'Описание изменено.');
              }}
            >
              <label>
                Тип заявки
                <select value={type} onChange={(e) => setType(e.target.value as typeof type)}>
                  {Object.entries(categories).map(([k, v]) => (
                    <option key={k} value={k}>
                      {v}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                Описание
                <textarea
                  value={comment}
                  onChange={(e) => setComment(e.target.value)}
                  required
                  minLength={10}
                  maxLength={2000}
                />
              </label>
              <button className="button primary" disabled={busy}>
                Сохранить
              </button>
            </form>
          )}
        </section>
        <section className="detail-section">
          <h3>Специалист</h3>
          <p>{order.executorName || 'Пока не назначен'}</p>
          {order.executorPhone && (
            <a className="button secondary" href={`tel:${order.executorPhone}`}>
              <Phone size={16} />
              Позвонить
            </a>
          )}
          {dispatcher && order.status === 'published' && (
            <form
              className="form"
              onSubmit={async (e) => {
                e.preventDefault();
                await act(() => api.assign(order.id, executor), 'Специалист назначен.');
              }}
            >
              <label>
                Назначить специалиста с открытым слотом
                <select value={executor} onChange={(e) => setExecutor(e.target.value)} required>
                  <option value="">Выберите специалиста</option>
                  {workers
                    .filter((w) => available(w.availableUntil))
                    .map((w) => (
                      <option value={w.id} key={w.id}>
                        {w.name}
                      </option>
                    ))}
                </select>
              </label>
              <button className="button secondary" disabled={busy || !executor}>
                Назначить
              </button>
            </form>
          )}
        </section>
        {order.reportSubmittedAt && (
          <section className="detail-section">
            <h3>Отчёт о работе</h3>
            <p className="preserve-lines">{order.reportSummary}</p>
            <div className="report-checks">
              <span>✓ Работа выполнена</span>
              <span>✓ Результат проверен с клиентом</span>
              <span>✓ Рабочее место убрано</span>
            </div>
            <div className="photo-grid">
              {order.photos.map((p) => (
                <button
                  className="photo-button"
                  key={p.id}
                  aria-label={`Открыть фото ${p.name}`}
                  onClick={() => setZoom({ ...p, url: photoUrl(p.url) })}
                >
                  <img src={photoUrl(p.url)} alt={p.name} />
                </button>
              ))}
            </div>
            <small className="muted">
              Отправлен {new Date(order.reportSubmittedAt).toLocaleString('ru-RU')}
            </small>
            {order.reviewFeedback && (
              <p className="notice preserve-lines">
                Комментарий диспетчера: {order.reviewFeedback}
              </p>
            )}
          </section>
        )}
        {dispatcher && order.status === 'workFinished' && rework && (
          <form
            className="form"
            onSubmit={async (e) => {
              e.preventDefault();
              await act(async () => {
                await api.review(order.id, false, feedback);
                setRework(false);
              }, 'Отчёт возвращён на доработку.');
            }}
          >
            <label>
              Что нужно исправить?
              <textarea
                value={feedback}
                onChange={(e) => setFeedback(e.target.value)}
                required
                maxLength={2000}
              />
            </label>
            <button className="button secondary" disabled={busy}>
              Вернуть на доработку
            </button>
          </form>
        )}
        <section className="detail-section">
          <h3>Обсуждение заявки</h3>
          <div className="messages" aria-live="polite">
            {messages.length ? (
              messages.map((m) => (
                <article key={m.id} className={m.senderId === account.id ? 'own-message' : ''}>
                  <strong>{m.senderName}</strong>
                  <small>{formatTime(m.created)}</small>
                  <p className="preserve-lines">{m.text}</p>
                </article>
              ))
            ) : (
              <p className="muted">Сообщений пока нет.</p>
            )}
          </div>
          {canDiscuss && (
            <form
              className="form"
              onSubmit={async (e) => {
                e.preventDefault();
                await act(async () => {
                  await api.sendMessage(order.id, text);
                  setText('');
                  setMessages(await api.messages(order.id));
                }, 'Сообщение отправлено.');
              }}
            >
              <label>
                Сообщение
                <textarea
                  value={text}
                  onChange={(e) => setText(e.target.value)}
                  required
                  maxLength={2000}
                  rows={2}
                />
              </label>
              <button className="button secondary" disabled={busy || !text.trim()}>
                <Send size={16} />
                Отправить
              </button>
            </form>
          )}
        </section>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
      <footer className="drawer-footer">
        {admin && order.status === 'created' && (
          <>
            <button
              className="button primary"
              disabled={busy}
              onClick={() =>
                void act(() => api.status(order.id, 'published'), 'Заявка опубликована.')
              }
            >
              Опубликовать
              <ArrowRight size={16} />
            </button>
            <button
              className="button secondary"
              disabled={busy}
              onClick={() => void act(() => api.status(order.id, 'cancelled'), 'Заявка отклонена.')}
            >
              Отклонить
            </button>
          </>
        )}
        {worker && order.status === 'published' && (
          <button
            className="button primary"
            disabled={busy || !available(account.availableUntil)}
            onClick={() =>
              void act(() => api.assign(order.id, account.id), 'Заявка закреплена за вами.')
            }
          >
            {available(account.availableUntil) ? 'Взять заявку' : 'Сначала откройте слот'}
          </button>
        )}
        {worker && mine && order.status === 'workerOnTheWay' && (
          <button
            className="button primary"
            disabled={busy}
            onClick={() => void act(() => api.status(order.id, 'inProgress'), 'Работа начата.')}
          >
            Я на месте · начать работу
          </button>
        )}
        {worker && mine && ['inProgress', 'rework'].includes(order.status) && (
          <button className="button primary" disabled={busy} onClick={onReport}>
            Заполнить отчёт
          </button>
        )}
        {dispatcher && order.status === 'workFinished' && (
          <>
            <button
              className="button primary"
              disabled={busy}
              onClick={() => void act(() => api.review(order.id, true), 'Выполнение подтверждено.')}
            >
              Подтвердить выполнение
            </button>
            <button className="button secondary" disabled={busy} onClick={() => setRework(true)}>
              На доработку
            </button>
          </>
        )}
        {admin && order.status === 'approved' && (
          <button
            className="button primary"
            disabled={busy}
            onClick={() => void act(() => api.status(order.id, 'completed'), 'Заявка закрыта.')}
          >
            Закрыть заявку
          </button>
        )}
      </footer>
      {zoom && (
        <Dialog title={zoom.name} onClose={() => setZoom(null)} wide>
          <div className="photo-viewer">
            <img src={zoom.url} alt={zoom.name} />
          </div>
        </Dialog>
      )}
    </Dialog>
  );
}

function UsersPanel({
  currentId,
  run,
}: {
  currentId: string;
  run: (action: () => Promise<unknown>, message?: string) => Promise<void>;
}) {
  const [users, setUsers] = useState<ApiUser[]>([]);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [more, setMore] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function load(p = 1, append = false) {
    setBusy(true);
    setError('');
    try {
      const data = await api.users(p, search);
      setUsers((old) => (append ? [...old, ...data] : data));
      setPage(p);
      setMore(data.length === 50);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить пользователей.');
    } finally {
      setBusy(false);
    }
  }
  useEffect(() => {
    void load();
  }, []);
  async function change(action: () => Promise<void>) {
    setBusy(true);
    try {
      await run(action, 'Пользователь обновлён. Ему потребуется войти снова.');
      await load();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось изменить пользователя.');
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="panel">
      <header className="panel-heading">
        <h2>Пользователи</h2>
        <form
          className="user-search"
          onSubmit={(e) => {
            e.preventDefault();
            void load();
          }}
        >
          <input
            aria-label="Поиск пользователей"
            placeholder="Имя или почта"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
          <button className="button secondary" disabled={busy}>
            Найти
          </button>
        </form>
      </header>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
      <div className="users-list">
        {users.map((u) => (
          <article key={u.id}>
            <div>
              <strong>{u.name}</strong>
              <span className="muted block">{u.email}</span>
              <span className="muted block">{u.phoneNumber}</span>
            </div>
            <label>
              <span className="sr-only">Роль {u.name}</span>
              <select
                aria-label={`Роль ${u.name}`}
                value={u.roles[0] || 'User'}
                disabled={busy || u.id === currentId}
                onChange={(e) => void change(() => api.setRole(u.id, e.target.value))}
              >
                {Object.entries(roleLabels).map(([r, label]) => (
                  <option key={r} value={r}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
            <button
              className="button secondary"
              disabled={busy || u.id === currentId}
              onClick={() => void change(() => api.setBlocked(u.id, !u.blocked))}
            >
              {u.blocked ? 'Разблокировать' : 'Заблокировать'}
            </button>
          </article>
        ))}
      </div>
      {!users.length && (
        <Empty title="Пользователи не найдены" text="Попробуйте другой поисковый запрос." />
      )}
      {more && (
        <div className="panel-heading">
          <button
            className="button secondary"
            disabled={busy}
            onClick={() => void load(page + 1, true)}
          >
            Загрузить ещё
          </button>
        </div>
      )}
    </section>
  );
}
