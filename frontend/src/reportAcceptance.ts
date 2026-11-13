import type { ApiOrder } from './api';

export function canReviewWork(order: Pick<ApiOrder, 'initiatorId' | 'status' | 'reportSubmittedAt'>, userId: string): boolean {
  return order.initiatorId === userId && !!order.reportSubmittedAt
    && (order.status === 'workFinished' || order.status === 'approved');
}
