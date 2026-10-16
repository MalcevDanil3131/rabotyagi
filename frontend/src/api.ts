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
export const apiBase = () => (import.meta.env?.VITE_API_BASE_URL || '').replace(/\/$/, '');
export const photoUrl = (url: string) => `${apiBase()}${url}`;
export class ApiError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

export async function request<T>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const base = apiBase();
  const multipart = body instanceof FormData;
  let response: Response;
  try {
    response = await fetch(`${base}/api${path}`, {
      method,
      credentials: 'include',
      headers: body === undefined || multipart ? {} : { 'Content-Type': 'application/json' },
      body: multipart ? body : body === undefined ? undefined : JSON.stringify(body),
    });
  } catch {
    throw new ApiError('Не удалось связаться с сервером. Проверьте, запущен ли API.', 0);
  }
  const text = await response.text();
  let data: unknown;
  try {
    data = text ? JSON.parse(text) : undefined;
  } catch {
    throw new ApiError('Сервер вернул неожиданный ответ. Проверьте адрес API.', response.status);
  }
  if (!response.ok) {
    const error = data as
      { error?: string; title?: string; errors?: Record<string, string[]> } | undefined;
    const validation = error?.errors ? Object.values(error.errors).flat().join(' ') : undefined;
    throw new ApiError(
      error?.error ||
        validation ||
        error?.title ||
        (response.status === 401
          ? 'Войдите в аккаунт, чтобы продолжить.'
          : response.status >= 500
            ? 'Сервер API недоступен или вернул ошибку. Проверьте, запущен ли бэкенд.'
            : 'Не удалось выполнить запрос.'),
      response.status,
    );
  }
  return data as T;
}

export const api = {
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
  orders: (page = 1) => request<ApiOrder[]>(`/order?page=${page}&pageSize=100`),
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
  report: (orderId: string, form: FormData) =>
    request<void>(`/order/${orderId}/report`, 'POST', form),
  review: (orderId: string, approved: boolean, feedback?: string) =>
    request<void>(`/order/${orderId}/review`, 'POST', { approved, feedback }),
  messages: (id: string) => request<ApiMessage[]>(`/order/${id}/messages`),
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
