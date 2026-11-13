import type { ApiHistoryEvent, ApiStatus } from './api';
import { formatPrice } from './pricePresentation.ts';

const labels: Record<ApiStatus, string> = {
  created: 'На модерации', published: 'Доступна', workerOnTheWay: 'В пути',
  inProgress: 'В работе', workFinished: 'Проверка отчёта', rework: 'На доработке',
  approved: 'Ожидает закрытия', completed: 'Закрыта', cancelled: 'Отменена',
};

export function describeHistoryEvent(event: ApiHistoryEvent): string {
  switch (event.eventType) {
    case 'created': return 'Создана заявка';
    case 'statusChanged': return `Изменён статус: ${event.previousStatus ? labels[event.previousStatus] || event.previousStatus : '—'} → ${labels[event.newStatus] || event.newStatus}`;
    case 'executorAssigned': return `Назначен исполнитель: ${event.executorNameSnapshot || 'Пользователь удалён'}`;
    case 'executorChanged': return `Заменён исполнитель: ${event.previousExecutorNameSnapshot || 'Пользователь удалён'} → ${event.executorNameSnapshot || 'Пользователь удалён'}. Заявка возвращена в статус «В пути»`;
    case 'priceProposed': return `Исполнитель предложил стоимость: ${formatPrice(event.priceAmount)}`;
    case 'priceAccepted': return `Клиент согласовал стоимость: ${formatPrice(event.priceAmount)}`;
    case 'priceRejected': return `Клиент отклонил стоимость: ${formatPrice(event.priceAmount)}`;
    case 'reportSubmitted': return 'Отправлен фотоотчёт';
    case 'reportApproved': return 'Фотоотчёт принят диспетчером';
    case 'workAcceptedByClient': return 'Клиент принял работу. Заявка завершена';
    case 'workReturnedByClient': return 'Клиент вернул работу на доработку';
    case 'reportReturned': return 'Фотоотчёт возвращён на доработку';
    default: return 'Действие с заявкой';
  }
}

export function historyDate(value: string): string {
  return new Date(value).toLocaleString('ru-RU', {
    year: 'numeric', month: '2-digit', day: '2-digit',
    hour: '2-digit', minute: '2-digit', second: '2-digit',
  });
}
