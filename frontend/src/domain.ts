export type OrderType = 'sanitary' | 'electric' | 'furniture' | 'cosmetic' | 'movement' | 'other';
export type Status =
  | 'new'
  | 'published'
  | 'onTheWay'
  | 'inProgress'
  | 'report'
  | 'rework'
  | 'approved'
  | 'completed'
  | 'cancelled';

export const categories: Record<OrderType, string> = {
  sanitary: 'Сантехника',
  electric: 'Электрика',
  furniture: 'Сборка мебели',
  cosmetic: 'Мелкий ремонт',
  movement: 'Переезд',
  other: 'Другое',
};
export const statuses: Record<Status, string> = {
  new: 'На модерации',
  published: 'Доступна',
  onTheWay: 'В пути',
  inProgress: 'В работе',
  report: 'Проверка отчёта',
  rework: 'На доработке',
  approved: 'Ожидает закрытия',
  completed: 'Закрыта',
  cancelled: 'Отменена',
};
export interface Photo {
  id: string;
  name: string;
  url: string;
}
export interface Report {
  summary: string;
  checks: [boolean, boolean, boolean];
  photos: Photo[];
  submittedAt: string;
  feedback?: string;
}
export interface Event {
  id: string;
  text: string;
  time: string;
}
export interface Order {
  id: string;
  number: number | string;
  title: string;
  type: OrderType;
  status: Status;
  clientId: string;
  clientName: string;
  address: string;
  comment: string;
  scheduledAt: string | null;
  createdAt: string;
  workerId?: string;
  report?: Report;
  events: Event[];
}
export interface Worker {
  id: string;
  name: string;
  specialty: string;
  initials: string;
  color: string;
  phone: string;
  slotUntil?: string;
}
export function formatTime(value: string) {
  return new Date(value).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}
export function formatDate(value: string) {
  return new Date(value).toLocaleDateString('ru-RU', { day: 'numeric', month: 'short' });
}
