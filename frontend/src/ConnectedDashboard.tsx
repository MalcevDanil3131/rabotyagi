import { useCallback, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { createOrderListLoader } from './orderListLoader';
import { refreshOrderView } from './refreshOrderView';
import type { ReactNode } from 'react';
import { adminQueues } from './adminQueues';
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
import type { Account, Address, ApiOrder, ApiWorker, ApiUser, ApiMessage, ApiStatus, OrderListQuery } from './api';
import { Avatar, Badge, Dialog, Empty, OrdersTable, SearchField } from './components';
import { CreateOrder, ReportForm } from './OrderDialogs';
import { OrderHistory } from './OrderHistory';
import { ReassignExecutorForm } from './ReassignExecutorForm';
import { OrderPrice } from './OrderPrice';
import { canReviewWork } from './reportAcceptance';
import { priceAgreed, priceSummary } from './pricePresentation';
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
    priceSummary: priceSummary(o),
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
  demoPanel,
}: {
  account: Account;
  onAccount: (a: Account) => void;
  onLogout: () => Promise<void>;
  onExpired: () => void;
  demoPanel?: ReactNode;
}) {
  const [view, setView] = useState<View>('orders');
  const [workers, setWorkers] = useState<ApiWorker[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [filter, setFilter] = useState('all');
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
  const [queue, setQueue] = useState<ApiStatus | undefined>();
  const [debouncedSearch, setDebouncedSearch] = useState('');
  const [detail, setDetail] = useState<ApiOrder | null>(null);
  const [detailLoading, setDetailLoading] = useState(false);
  const [detailError, setDetailError] = useState('');
  const detailVersion = useRef(0);
  const auxiliaryVersion = useRef(0);
  const mutationInFlight = useRef(false);
  const latestRefresh = useRef<(background?: boolean, afterMutation?: boolean) => Promise<void>>(async () => {});
  const [workersError, setWorkersError] = useState('');
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
  const loader = useMemo(() => createOrderListLoader(api.orderPage), [account.id, account.roleName, roles.join(',')]);
  const list = useSyncExternalStore(loader.subscribe, loader.getSnapshot);
  const orders = list.data?.items ?? [];
  const loading = list.loading;
  const queueCounts = list.data?.statusCounts ?? [];
  const stats = list.data?.stats;
  const query: OrderListQuery = useMemo(() => ({ page, pageSize: 20,
    scope: (['mine', 'active', 'review'].includes(filter) ? filter : 'all') as OrderListQuery['scope'],
    status: queue ?? (filter in statusMap ? filter as ApiStatus : undefined),
    search: debouncedSearch.trim(), quality: view === 'quality',
  }), [page, filter, queue, debouncedSearch, view]);
  useEffect(() => {
    if (search === debouncedSearch) return;
    const timer = window.setTimeout(() => { setDebouncedSearch(search); setPage(1); }, 300);
    return () => clearTimeout(timer);
  }, [search]);
  useEffect(() => {
    let active = true;
    void loader.load(query).then((ok) => {
      const current = loader.getSnapshot().data;
      if (active && ok && current && current.page !== query.page) setPage(current.page);
    });
    return () => { active = false; };
  }, [loader, query]);
  useEffect(() => () => loader.cancel(), [loader]);
  useEffect(() => {
    if (list.error instanceof ApiError && list.error.status === 401) onExpired();
  }, [list.error, onExpired]);
  useEffect(() => { setPage(1); }, [filter, queue, view]);
  const loadWorkers = useCallback(async () => {
    const version = ++auxiliaryVersion.current;
    if (!worker && !staff) { setWorkers([]); return; }
    try {
      const rows = await api.workers();
      if (version === auxiliaryVersion.current) { setWorkers(rows); setWorkersError(''); }
    } catch (err) {
      if (version !== auxiliaryVersion.current) return;
      if (err instanceof ApiError && err.status === 401) onExpired();
      setWorkersError('Не удалось обновить специалистов. Список заявок доступен отдельно.');
    }
  }, [worker, staff, onExpired]);
  useEffect(() => { void loadWorkers(); return () => { auxiliaryVersion.current++; }; }, [account.id, worker, staff]);
  const loadDetail = useCallback(async () => {
    const version = ++detailVersion.current;
    if (!selected) { setDetail(null); setDetailError(''); return; }
    setDetailLoading(true);
    try {
      const row = await api.order(selected);
      if (version === detailVersion.current) { setDetail(row); setDetailError(''); }
    } catch (err) {
      if (version !== detailVersion.current) return;
      if (err instanceof ApiError && err.status === 401) onExpired();
      if (err instanceof ApiError && (err.status === 404 || err.status === 403)) setDetail(null);
      setDetailError(err instanceof Error ? err.message : 'Не удалось обновить карточку.');
    } finally { if (version === detailVersion.current) setDetailLoading(false); }
  }, [selected, onExpired]);
  useEffect(() => {
    setDetail(null); setDetailError(''); void loadDetail();
    return () => { detailVersion.current++; };
  }, [selected]);
  async function refresh(background = false, afterMutation = false) {
    await refreshOrderView({ loadList: () => loader.load(query, background), loadWorkers, loadDetail, selected, reportId, afterMutation });
    const current = loader.getSnapshot().data;
    if (current && current.page !== query.page && !loader.getSnapshot().error) setPage(current.page);
  }
  latestRefresh.current = refresh;
  useEffect(() => {
    const id = window.setInterval(() => {
      setClock((x) => x + 1);
      if (!document.hidden && !loading && !busy && !modal && !reportId) void refresh(true);
    }, 15000);
    return () => clearInterval(id);
  }, [loader, query, loading, busy, modal, reportId, selected, loadDetail, loadWorkers]);
  useEffect(() => {
    const handleFocus = () => { if (!document.hidden && !busy && !modal && !reportId) void refresh(true); };
    window.addEventListener('focus', handleFocus);
    return () => window.removeEventListener('focus', handleFocus);
  }, [loader, query, busy, modal, reportId, selected, loadDetail, loadWorkers]);
  useEffect(() => {
    if (!notice) return;
    const id = setTimeout(() => setNotice(''), 5000);
    return () => clearTimeout(id);
  }, [notice]);
  async function run(action: () => Promise<unknown>, message = 'Сохранено.') {
    if (mutationInFlight.current) throw new Error('Дождитесь завершения текущего действия.');
    mutationInFlight.current = true;
    setBusy(true); setError('');
    try {
      await action();
      setNotice(message);
      // A refresh failure must not pretend that a committed action failed.
      await latestRefresh.current(false, true);
    } catch (err) {
      fail(err);
      if (err instanceof ApiError && (err.status === 409 || err.status === 404 || err.status === 403)) await latestRefresh.current(false, true);
      throw err;
    } finally { mutationInFlight.current = false; setBusy(false); }
  }
  const reviews = stats?.review ?? 0;
  const shown = orders;
  const chosen = detail?.id === selected ? detail : undefined;
  const reporting = detail?.id === reportId ? detail : undefined;
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
    await run(() => api.report(reporting.id, form), 'Отчёт отправлен клиенту.');
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
                  setQueue(undefined);
                  setFilter('all');
                  setSearch('');
                }}
              >
                <Icon size={19} />
                <span>{worker && key === 'team' ? 'Мой слот' : label}</span>
                {key === 'quality' && reviews > 0 && (
                  <span className="nav-count">{reviews}</span>
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
        {demoPanel && <div className="demo-banner">{demoPanel}</div>}
        <main className="main-content">
          {superAdmin && (view === 'orders' || view === 'quality') && (
            <section className="admin-queues" aria-label="Центр управления заявками">
              <div className="admin-queues-heading"><div><span className="eyebrow">Центр управления</span><h2>Что требует внимания</h2></div>
                <button className="text-button" onClick={() => { setQueue(undefined); setView('orders'); setFilter('all'); setSearch(''); }}>Все заявки</button>
              </div>
              <div className="admin-queue-grid">{adminQueues.map((item) => (
                <button key={item.status} className={`admin-queue ${queue === item.status ? 'selected' : ''}`}
                  aria-pressed={queue === item.status} onClick={() => {

                    setQueue(item.status); setView('orders'); setFilter('all'); setSearch(''); setSelected(null);
                  }}>
                  <span>{item.title}</span><strong>{stats ? queueCounts.find((x) => x.status === item.status)?.count ?? 0 : '—'}</strong>
                  <small>{item.description}</small>
                </button>
              ))}</div>
              <p className="muted">Счётчики учитывают все заявки. Откройте карточку из очереди, чтобы выполнить действие.</p>
              <button className="text-button" onClick={() => { setView('users'); setQueue(undefined); }}>Управление пользователями →</button>
            </section>
          )}
          <div className="page-heading">
            <div>
              <span className="greeting">Здравствуйте, {account.name}</span>
              <h1>{worker && view === 'team' ? 'Мой рабочий слот' : views[view][0]}</h1>
              <p>
                {view === 'orders'
                  ? 'Заявки, которые доступны вам по роли.'
                  : view === 'quality'
                    ? 'Работы, ожидающие приёмки клиентом.'
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
          {workersError && <p className="error" role="alert">{workersError} <button className="text-button" onClick={() => void loadWorkers()}>Повторить загрузку специалистов</button></p>}
          {notice && (
            <p className="success" role="status">
              {notice}
            </p>
          )}
          {(view === 'orders' || view === 'quality') && (
            <>
              <div className="metrics live-metrics">
                {[
                  ['Всего доступно', stats?.total ?? '—', 'all'],
                  ['В работе', stats?.active ?? '—', 'active'],
                  ['Приёмка', stats?.review ?? '—', 'review'],
                  ['Закрыто', stats?.completed ?? '—', 'completed'],
                ].map(([label, count, f]) => (
                  <button className="metric" key={label} onClick={() => { setQueue(undefined); setFilter(String(f)); }}>
                    <span className="metric-top">{label}</span>
                    <strong className="metric-value">{count}</strong>
                  </button>
                ))}
              </div>
              <section className="panel">
                <header className="panel-heading">
                  <h2>{queue ? adminQueues.find((x) => x.status === queue)?.title : view === 'quality' ? 'Приёмка работ' : 'Очередь заявок'}</h2>
                  <span className="count">{list.data?.totalCount ?? '—'}</span>
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
                        onChange={(e) => { setQueue(undefined); setFilter(e.target.value || 'all'); }}
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
                {list.error != null && (
                  <div className="error" role="alert">
                    {list.error instanceof Error ? list.error.message : 'Не удалось загрузить заявки.'}
                    {list.data && <p>Показаны ранее загруженные данные. Они могут быть устаревшими.</p>}
                    <button className="button secondary" disabled={loading} onClick={() => void refresh()}>Повторить загрузку</button>
                  </div>
                )}
                {loading && <p className="loading" role="status">{list.data ? 'Обновляем список…' : 'Загружаем заявки…'}</p>}
                {shown.length ? (
                  <OrdersTable
                    orders={shown.map(toOrder)}
                    workers={avatars}
                    onSelect={setSelected}
                  />
                ) : list.data && !list.error ? (
                  <Empty
                    title="Заявок пока нет"
                    text="Здесь появятся ваши заявки. Попробуйте изменить фильтр."
                  />
                ) : null}
                {list.data && (
                  <nav className="pagination" aria-label="Страницы заявок">
                    <button className="button secondary" disabled={loading || busy || list.data.page <= 1}
                      onClick={() => setPage(list.data!.page - 1)}>Назад</button>
                    <span role="status">Страница {list.data.page} из {Math.max(1, Math.ceil(list.data.totalCount / list.data.pageSize))} · Найдено: {list.data.totalCount}</span>
                    <button className="button secondary" disabled={loading || busy || list.data.page * list.data.pageSize >= list.data.totalCount}
                      onClick={() => setPage(list.data!.page + 1)}>Далее</button>
                  </nav>
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
              {!workers.length && !workersError && (
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
                setQueue(undefined);
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
            let createdId: string | undefined;
            await run(
              async () => {
                const created = await api.createOrder({
                  title: input.title,
                  orderType: input.type,
                  comment: input.comment,
                  addressText: input.address,
                  scheduledAt: input.scheduledAt ? new Date(input.scheduledAt).toISOString() : null,
                });
                createdId = created.id;
              },
              'Заявка отправлена на модерацию.',
            );
            if (createdId) setSelected(createdId);
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
          busy={busy || detailLoading || !!detailError}
          refreshError={detailError}
          refreshing={detailLoading}
          onRefresh={() => void loadDetail()}
          run={run}
          onCancelled={() => setReportId(null)}
          onClose={() => setSelected(null)}
          onReport={() => setReportId(chosen.id)}
        />
      )}
      {selected && !chosen && !reporting && (
        <Dialog title="Карточка заявки" onClose={() => setSelected(null)} drawer>
          <div className="dialog-body">
            {detailLoading ? <p role="status">Загружаем заявку…</p> : <p className="error" role="alert">{detailError || 'Заявка недоступна.'}</p>}
            <button className="button secondary" disabled={detailLoading} onClick={() => void loadDetail()}>Повторить загрузку карточки</button>
          </div>
        </Dialog>
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
  onCancelled,
  refreshError,
  refreshing,
  onRefresh,
}: {
  order: ApiOrder;
  account: Account;
  workers: ApiWorker[];
  busy: boolean;
  run: (action: () => Promise<unknown>, message?: string) => Promise<void>;
  onClose: () => void;
  onReport: () => void;
  onCancelled: () => void;
  refreshError: string;
  refreshing: boolean;
  onRefresh: () => void;
}) {
  const [messages, setMessages] = useState<ApiMessage[]>([]);
  const [historyRevision, setHistoryRevision] = useState(0);
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
  const canAccept = canReviewWork(order, account.id);
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
      setHistoryRevision((value) => value + 1);
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
        {refreshError && <p className="error" role="alert">{refreshError} Действия временно недоступны до обновления карточки.</p>}
        <button className="button secondary" disabled={refreshing} onClick={onRefresh}>{refreshing ? 'Обновляем…' : 'Обновить карточку'}</button>
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
          {dispatcher && order.executorId && ['workerOnTheWay', 'inProgress', 'rework'].includes(order.status) && (
            <ReassignExecutorForm key={`${order.id}:${order.updated}`} order={order} workers={workers} busy={busy} act={act} />
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
        <OrderPrice key={`${order.id}:${order.updated}`} order={order} account={account} busy={busy} act={act} />
        {['workFinished', 'approved'].includes(order.status) && (
          <p className="notice">
            {canAccept
              ? 'Проверьте отчёт и фотографии. Принять работу — завершить заявку. Если есть замечания, верните её на доработку с описанием.'
              : 'Отчёт ожидает решения клиента. Только владелец заявки может принять работу или вернуть её на доработку.'}
          </p>
        )}
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
                Замечание к отчёту: {order.reviewFeedback}
              </p>
            )}
          </section>
        )}
        {canAccept && rework && (
          <form
            className="form"
            onSubmit={async (e) => {
              e.preventDefault();
              await act(async () => {
                await api.review(order.id, false, order.updated, feedback.trim());
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
            <button className="button secondary" disabled={busy || !feedback.trim()}>
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
        <OrderHistory key={order.id} orderId={order.id} revision={`${order.updated}:${historyRevision}`} />
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
      </div>
      <footer className="drawer-footer">
        {order.initiatorId === account.id &&
          ['created', 'published', 'workerOnTheWay'].includes(order.status) && (
            <button
              className="button secondary"
              disabled={busy}
              onClick={() => {
                if (!window.confirm('Отменить вашу заявку? Это действие нельзя отменить.')) return;
                void act(async () => {
                  await api.status(order.id, 'cancelled');
                  onCancelled();
                  setEdit(false);
                }, 'Заявка отменена.');
              }}
            >
              Отменить заявку
            </button>
          )}
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
            disabled={busy || !priceAgreed(order)}
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
        {canAccept && (
          <>
            <button
              className="button primary"
              disabled={busy}
              onClick={() => void act(() => api.review(order.id, true, order.updated), 'Работа принята. Заявка завершена.')}
            >
              Принять работу
            </button>
            <button className="button secondary" disabled={busy} onClick={() => setRework(true)}>
              На доработку
            </button>
          </>
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
  const [more, setMore] = useState(false);
  const [search, setSearch] = useState('');
  const [page, setPage] = useState(1);
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
