import test from 'node:test';
import assert from 'node:assert/strict';
import { describeHistoryEvent, historyDate } from '../src/historyPresentation.ts';
import type { ApiHistoryEvent } from '../src/api.ts';
import { api, ApiError } from '../src/api.ts';

const event: ApiHistoryEvent = {
  id: 'event', occurredAt: '2026-10-17T12:30:00Z', eventType: 'created',
  actorUserId: 'actor', actorNameSnapshot: 'Иван Петров', previousStatus: null,
  newStatus: 'created', executorId: null, executorNameSnapshot: null, comment: null,
};

test('история описывает все действия и старый/новый статус по-русски', () => {
  const descriptions = {
    created: 'Создана заявка', statusChanged: 'Изменён статус: На модерации → Доступна',
    executorAssigned: 'Назначен исполнитель: Иван', reportSubmitted: 'Отправлен фотоотчёт',
    reportApproved: 'Фотоотчёт принят диспетчером', reportReturned: 'Фотоотчёт возвращён на доработку',
  };
  for (const [eventType, expected] of Object.entries(descriptions)) {
    assert.equal(describeHistoryEvent({ ...event, eventType: eventType as ApiHistoryEvent['eventType'],
      previousStatus: 'created', newStatus: 'published', executorNameSnapshot: 'Иван' }), expected);
  }
  assert.equal(describeHistoryEvent({ ...event, eventType: 'future' as ApiHistoryEvent['eventType'] }), 'Действие с заявкой');
  assert.match(describeHistoryEvent({ ...event, eventType: 'executorAssigned' }), /Пользователь удалён/);
  assert.match(historyDate(event.occurredAt), /2026/);
  assert.match(historyDate(event.occurredAt), /\d{2}:\d{2}:\d{2}/);
});

test('история запрашивается для конкретной заявки с cookie; пустой результат и ошибки сохраняются', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async (url, options) => {
      assert.equal(url, '/api/order/order-id/history');
      assert.equal(options?.credentials, 'include');
      return Response.json([event]);
    };
    assert.deepEqual(await api.history('order-id'), [event]);
    globalThis.fetch = async () => Response.json([]);
    assert.deepEqual(await api.history('old-order'), []);
    for (const status of [401, 404, 500]) {
      globalThis.fetch = async () => new Response(null, { status });
      await assert.rejects(api.history('order-id'), (err) => err instanceof ApiError && err.status === status);
    }
  } finally { globalThis.fetch = original; }
});
