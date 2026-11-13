import test from 'node:test';
import assert from 'node:assert/strict';
import { describeHistoryEvent, historyDate } from '../src/historyPresentation.ts';
import type { ApiHistoryEvent } from '../src/api.ts';
import { api, ApiError } from '../src/api.ts';

const event: ApiHistoryEvent = {
  id: 'event', occurredAt: '2026-10-17T12:30:00Z', eventType: 'created',
  actorUserId: 'actor', actorNameSnapshot: 'Иван Петров', previousStatus: null,
  newStatus: 'created', executorId: null, executorNameSnapshot: null, comment: null,
  previousExecutorId: null, previousExecutorNameSnapshot: null,
};

test('история описывает все действия и старый/новый статус по-русски', () => {
  const descriptions = {
    created: 'Создана заявка', statusChanged: 'Изменён статус: На модерации → Доступна',
    executorAssigned: 'Назначен исполнитель: Иван', reportSubmitted: 'Отправлен фотоотчёт',
    executorChanged: 'Заменён исполнитель: Пётр → Иван. Заявка возвращена в статус «В пути»',
    reportApproved: 'Фотоотчёт принят диспетчером', reportReturned: 'Фотоотчёт возвращён на доработку',
  };
  for (const [eventType, expected] of Object.entries(descriptions)) {
    assert.equal(describeHistoryEvent({ ...event, eventType: eventType as ApiHistoryEvent['eventType'],
      previousStatus: 'created', newStatus: 'published', executorNameSnapshot: 'Иван', previousExecutorNameSnapshot: 'Пётр' }), expected);
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

test('замена передаёт причину и исходную версию заявки, конфликт не скрывается', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async (url, options) => {
      assert.equal(url, '/api/order/order-id/executor');
      assert.equal(options?.method, 'PATCH');
      assert.equal(options?.credentials, 'include');
      assert.deepEqual(JSON.parse(String(options?.body)), {
        executorId: 'new', expectedExecutorId: 'old', expectedUpdated: '2026-10-24T10:00:00.123456Z', reason: 'Недоступен',
      });
      return new Response(null, { status: 200 });
    };
    await api.reassign('order-id', 'new', 'old', '2026-10-24T10:00:00.123456Z', 'Недоступен');
    for (const status of [400, 403, 409]) {
      globalThis.fetch = async () => Response.json({ error: 'Замена недоступна' }, { status });
      await assert.rejects(api.reassign('order-id', 'new', 'old', 'version', 'reason'),
        (err) => err instanceof ApiError && err.status === status && err.message === 'Замена недоступна');
    }
  } finally { globalThis.fetch = original; }
});
