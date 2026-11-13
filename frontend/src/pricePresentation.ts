import type { ApiOrder } from './api';

export function formatPrice(amount: number | null | undefined): string {
  return amount == null ? 'Не указана' : new Intl.NumberFormat('ru-RU', { style: 'currency', currency: 'RUB' }).format(amount);
}

export function priceSummary(order: Pick<ApiOrder, 'priceAmount' | 'priceStatus' | 'status'>): string {
  if (order.priceAmount == null) {
    if (order.status === 'created' || order.status === 'published') return 'Стоимость после назначения';
    if (order.status === 'workerOnTheWay') return 'Ожидает предложения исполнителя';
    return 'Стоимость не зафиксирована';
  }
  const labels = { none: 'не согласована', proposed: 'ожидает согласования', accepted: 'согласована', rejected: 'отклонена клиентом' };
  return `${formatPrice(order.priceAmount)} · ${labels[order.priceStatus]}`;
}

export function priceAgreed(order: Pick<ApiOrder, 'priceStatus' | 'priceAmount' | 'priceExecutorId' | 'executorId'>): boolean {
  return order.priceStatus === 'accepted' && (order.priceAmount ?? 0) > 0
    && !!order.executorId && order.priceExecutorId === order.executorId;
}

export function parsePrice(value: string): number | null {
  const text = value.trim().replace(',', '.');
  if (!/^\d+(\.\d{1,2})?$/.test(text)) return null;
  const amount = Number(text);
  return amount > 0 && amount <= 10000000 ? amount : null;
}
