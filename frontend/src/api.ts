import type { OrderType } from './domain';

export interface Address {
  id?: string | null;
  country: string | null;
  city: string | null;
  street: string | null;
  houseNumber: string | null;
  apartmentNumber?: string | null;
  floorNumber?: number | null;
  intercomNumber?: string | null;
}
export interface Account {
  id: string;
  email: string;
  name: string;
  surname: string;
  phoneNumber: string;
  address: Address | null;
  roles: string[];
  roleName: string;
  availableUntil: string | null;
}
export type ApiStatus =
  | 'created'
  | 'published'
  | 'workerOnTheWay'
  | 'inProgress'
  | 'workFinished'
  | 'rework'
  | 'approved'
  | 'completed'
  | 'cancelled';
export interface ApiPhoto {
  id: string;
  name: string;
  url: string;
}
export interface ApiOrder {
  priceAmount: number | null;
  priceScope: string | null;
  priceStatus: 'none' | 'proposed' | 'accepted' | 'rejected';
  priceExecutorId: string | null;
  priceAcceptedAt: string | null;
  id: string;
  initiatorId: string;
  executorId: string | null;
  title: string;
  addressText: string;
  scheduledAt: string | null;
  initiatorName: string;
  initiatorPhone: string | null;
  executorName: string | null;
  executorPhone: string | null;
  orderType: OrderType;
  status: ApiStatus;
  comment: string;
  created: string;
  updated: string;
  reportSummary: string | null;
  workDone: boolean;
  workplaceClean: boolean;
  clientInformed: boolean;
  reportSubmittedAt: string | null;
  reviewFeedback: string | null;
  reviewedAt: string | null;
  photos: ApiPhoto[];
}
export interface ApiWorker {
  id: string;
  name: string;
  phoneNumber: string | null;
  availableUntil: string | null;
  activeOrders: number;
}
export interface OrderListQuery {
  page: number;
  pageSize: number;
  scope: 'all' | 'mine' | 'active' | 'review';
  status?: ApiStatus;
  search: string;
  quality: boolean;
}
export interface OrderPage {
  items: ApiOrder[];
  page: number;
  pageSize: number;
  totalCount: number;
  stats: { total: number; active: number; review: number; completed: number };
  statusCounts: { status: ApiStatus; count: number }[];
}
export interface ApiUser {
  id: string;
  name: string;
  email: string;
  phoneNumber: string | null;
  roles: string[];
  blocked: boolean;
}
export interface ApiMessage {
  id: string;
  senderId: string;
  senderName: string;
  text: string;
  created: string;
}
export interface ApiHistoryEvent {
  id: string;
  occurredAt: string;
  eventType: 'created' | 'statusChanged' | 'executorAssigned' | 'executorChanged' | 'reportSubmitted' | 'reportApproved' | 'reportReturned' | 'priceProposed' | 'priceAccepted' | 'priceRejected' | 'workAcceptedByClient' | 'workReturnedByClient';
  priceAmount?: number | null;
  actorUserId: string;
  actorNameSnapshot: string;
  previousStatus: ApiStatus | null;
  newStatus: ApiStatus;
  executorId: string | null;
  executorNameSnapshot: string | null;
  previousExecutorId: string | null;
  previousExecutorNameSnapshot: string | null;
  comment: string | null;
}
export const apiBase = () => (import.meta.env?.VITE_API_BASE_URL || '').replace(/\/$/, '');
export const photoUrl = (url: string) => `${apiBase()}${url}`;
export class ApiError extends Error {
  status: number;
  retryAfterSeconds?: number;
  constructor(message: string, status: number, retryAfterSeconds?: number) {
    super(message);
    this.status = status;
    this.retryAfterSeconds = retryAfterSeconds;
  }
}
export function parseRetryAfter(value: string | null, now = Date.now()): number | undefined {
  if (!value) return undefined;
  const seconds = /^\d+$/.test(value) ? Number(value) : Math.ceil((Date.parse(value) - now) / 1000);
  return Number.isFinite(seconds) ? Math.min(86400, Math.max(1, seconds)) : undefined;
}

export async function request<T>(path: string, method = 'GET', body?: unknown, headers: Record<string, string> = {}): Promise<T> {
  const base = apiBase();
  const multipart = body instanceof FormData;
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 20000);
  try {
    const response = await fetch(`${base}/api${path}`, {
      method, credentials: 'include', signal: controller.signal,
      headers: { ...(body === undefined || multipart ? {} : { 'Content-Type': 'application/json' }), ...headers },
      body: multipart ? body : body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await response.text();
    let data: unknown;
    try { data = text ? JSON.parse(text) : undefined; }
    catch {
      if (response.ok) throw new ApiError('Сервер вернул неожиданный ответ. Проверьте адрес API.', response.status);
    }
    if (!response.ok) {
      const error = data as { error?: string; title?: string; errors?: Record<string, string[]>; retryAfterSeconds?: number } | undefined;
      const retryAfter = response.status === 429 ? parseRetryAfter(response.headers.get('Retry-After'))
        ?? (Number.isFinite(error?.retryAfterSeconds) ? Math.min(86400, Math.max(1, error!.retryAfterSeconds!)) : 60) : undefined;
      const validation = error?.errors ? Object.values(error.errors).flat().join(' ') : undefined;
      throw new ApiError(error?.error || validation || error?.title || (
        response.status === 429 ? `Слишком много запросов. Повторите через ${retryAfter} сек.`
          : response.status === 401 ? 'Войдите в аккаунт, чтобы продолжить.'
          : response.status === 403 ? 'У вас нет прав на это действие.'
          : response.status === 404 ? 'Заявка или запрошенные данные недоступны.'
          : response.status === 409 ? 'Данные изменились. Обновите карточку и повторите действие.'
          : response.status >= 500 ? 'Сервер API недоступен или вернул ошибку. Попробуйте обновить данные.'
          : 'Не удалось выполнить запрос.'), response.status, retryAfter);
    }
    return data as T;
  } catch (err) {
    if (err instanceof ApiError) throw err;
    throw new ApiError(controller.signal.aborted
      ? 'Сервер не ответил вовремя. Обновите данные, чтобы проверить результат действия перед повтором.'
      : 'Не удалось связаться с сервером. Проверьте подключение и повторите загрузку.', 0);
  } finally { clearTimeout(timer); }
}
export const api = {
  proposePrice: (orderId: string, amount: number, scope: string, expectedUpdated: string) =>
    request<void>(`/order/${orderId}/price`, 'POST', { amount, scope, expectedUpdated }),
  decidePrice: (orderId: string, accepted: boolean, reason: string, expectedUpdated: string) =>
    request<void>(`/order/${orderId}/price/decision`, 'POST', { accepted, reason, expectedUpdated }),
  demoAccounts: () => request<DemoAccount[]>('/demo/accounts', 'GET', undefined, { 'X-Rabotyagi-Demo': '1' }),
  demoLogin: (key: string) => request<void>(`/demo/login/${encodeURIComponent(key)}`, 'POST', undefined, { 'X-Rabotyagi-Demo': '1' }),
  account: () => request<Account>('/account'),
  login: (email: string, password: string, isPersistent: boolean) =>
    request<void>('/account/login', 'POST', { email, password, isPersistent }),
  register: (data: {
    name: string;
    surname: string;
    email: string;
    phoneNumber: string;
    password: string;
    isPersistent: boolean;
  }) => request<void>('/account/register', 'POST', data),
  logout: () => request<void>('/account/logout', 'POST'),
  changePassword: (oldPassword: string, newPassword: string) =>
    request<void>('/account/password', 'PATCH', { oldPassword, newPassword }),
  orders: (page = 1, status?: ApiStatus) => request<ApiOrder[]>(`/order?page=${page}&pageSize=100${status ? `&status=${encodeURIComponent(status)}` : ''}`),
  orderPage: (query: OrderListQuery) => {
    const params = new URLSearchParams({ page: String(query.page), pageSize: String(query.pageSize), scope: query.scope,
      search: query.search, quality: String(query.quality) });
    if (query.status) params.set('status', query.status);
    return request<OrderPage>(`/order/page?${params}`);
  },
  orderQueues: () => request<{ status: ApiStatus; count: number }[]>('/order/queues'),
  order: (id: string) => request<ApiOrder>(`/order/${id}`),
  createOrder: (data: {
    title: string;
    orderType: OrderType;
    comment: string;
    addressText: string;
    scheduledAt: string | null;
  }) => request<ApiOrder>('/order', 'POST', data),
  editOrder: (orderId: string, orderType: OrderType, comment: string) =>
    request<void>('/order', 'PUT', { orderId, orderType, comment }),
  status: (orderId: string, status: ApiStatus) =>
    request<void>('/order/status', 'PATCH', { orderId, status }),
  assign: (orderId: string, executorId: string) =>
    request<void>('/order/executor', 'PATCH', { orderId, executorId }),
  reassign: (orderId: string, executorId: string, expectedExecutorId: string, expectedUpdated: string, reason: string) =>
    request<void>(`/order/${orderId}/executor`, 'PATCH', { executorId, expectedExecutorId, expectedUpdated, reason }),
  report: (orderId: string, form: FormData) =>
    request<void>(`/order/${orderId}/report`, 'POST', form),
  review: (orderId: string, approved: boolean, expectedUpdated: string, feedback?: string) =>
    request<void>(`/order/${orderId}/review`, 'POST', { approved, feedback, expectedUpdated }),
  messages: (id: string) => request<ApiMessage[]>(`/order/${id}/messages`),
  history: (id: string) => request<ApiHistoryEvent[]>(`/order/${id}/history`),
  sendMessage: (id: string, text: string) =>
    request<void>(`/order/${id}/messages`, 'POST', { text }),
  workers: () => request<ApiWorker[]>('/worker'),
  availability: (hours: number) => request<void>('/worker/availability', 'PUT', { hours }),
  users: (page = 1, search = '') =>
    request<ApiUser[]>(`/user?page=${page}&search=${encodeURIComponent(search)}`),
  setRole: (id: string, role: string) => request<void>(`/user/${id}/role`, 'PUT', { role }),
  setBlocked: (id: string, blocked: boolean) =>
    request<void>(`/user/${id}/blocked`, 'PUT', { blocked }),
  saveAddress: (address: Address) => request<void>('/address', 'POST', address),
};

export interface DemoAccount { key: string; id: string; label: string; role: string }
