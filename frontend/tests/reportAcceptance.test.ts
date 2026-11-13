import test from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError } from '../src/api.ts';
import { canReviewWork } from '../src/reportAcceptance.ts';
import { describeHistoryEvent } from '../src/historyPresentation.ts';
import type { ApiHistoryEvent, ApiStatus } from '../src/api.ts';

test('приёмка доступна владельцу текущего отчёта и прежних принятых диспетчером работ', () => {
  for (const status of ['workFinished', 'approved'] as const) {
    const row = { initiatorId: 'client', status, reportSubmittedAt: '2026-11-07T00:00:00Z' };
    assert.equal(canReviewWork(row, 'client'), true);
    for (const actor of ['worker', 'dispatcher', 'admin', 'outsider']) assert.equal(canReviewWork(row, actor), false);
    assert.equal(canReviewWork({ ...row, reportSubmittedAt: null }, 'client'), false);
  }
  for (const status of ['created', 'published', 'workerOnTheWay', 'inProgress', 'rework', 'completed', 'cancelled'] as ApiStatus[])
    assert.equal(canReviewWork({ initiatorId: 'client', status, reportSubmittedAt: 'date' }, 'client'), false);
});
test('приёмка передаёт точную версию, замечание и реальную сессию; ошибка 409 видна вызывающему', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async (url, options) => {
      assert.equal(url, '/api/order/id/review'); assert.equal(options?.method, 'POST');
      assert.equal(options?.credentials, 'include');
      assert.deepEqual(JSON.parse(String(options?.body)), { approved: false, expectedUpdated: 'exact-version', feedback: 'Поправьте' });
      return new Response(null, { status: 200 });
    };
    await api.review('id', false, 'exact-version', 'Поправьте');
    globalThis.fetch = async () => Response.json({ error: 'Отчёт изменился' }, { status: 409 });
    await assert.rejects(api.review('id', true, 'stale-version'), (err) => err instanceof ApiError && err.status === 409);
  } finally { globalThis.fetch = original; }
});
test('история отличает решение клиента от прежней приёмки диспетчера', () => {
  assert.equal(describeHistoryEvent({ eventType: 'workAcceptedByClient' } as ApiHistoryEvent), 'Клиент принял работу. Заявка завершена');
  assert.equal(describeHistoryEvent({ eventType: 'workReturnedByClient' } as ApiHistoryEvent), 'Клиент вернул работу на доработку');
  assert.equal(describeHistoryEvent({ eventType: 'reportApproved' } as ApiHistoryEvent), 'Фотоотчёт принят диспетчером');
});
