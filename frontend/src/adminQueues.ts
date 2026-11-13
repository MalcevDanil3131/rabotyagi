import type { ApiStatus } from './api';

export const adminQueues: { status: ApiStatus; title: string; description: string }[] = [
  { status: 'created', title: 'Ждут публикации', description: 'Проверить новую заявку' },
  { status: 'published', title: 'Нужен исполнитель', description: 'Назначить доступного специалиста' },
  { status: 'workFinished', title: 'Ждут приёмки клиентом', description: 'Отчёты отправлены, решение принимает клиент' },
  { status: 'approved', title: 'Прежние приёмки', description: 'Ранее проверены диспетчером; завершает клиент' },
];
