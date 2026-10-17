import type { ApiHistoryEvent, ApiStatus } from './api';

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
    case 'reportSubmitted': return 'Отправлен фотоотчёт';
    case 'reportApproved': return 'Фотоотчёт принят диспетчером';
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
