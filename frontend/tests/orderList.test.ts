import test from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError, request } from '../src/api.ts';
import type { OrderListQuery, OrderPage } from '../src/api.ts';
import { createOrderListLoader } from '../src/orderListLoader.ts';

const query: OrderListQuery = { page: 2, pageSize: 20, scope: 'all', search: '', quality: false };
const result = (page = 2, totalCount = 42): OrderPage => ({ items: [], page, pageSize: 20, totalCount,
  stats: { total: totalCount, active: 10, review: 1, completed: 3 }, statusCounts: [] });
function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (error: unknown) => void;
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
test('страница, поиск и фильтры передаются серверу вместе, включая специальные символы', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async (url, options) => {
      const params = new URL(String(url), 'http://localhost').searchParams;
      assert.equal(params.get('page'), '2'); assert.equal(params.get('pageSize'), '20');
      assert.equal(params.get('scope'), 'mine'); assert.equal(params.get('status'), 'workFinished');
      assert.equal(params.get('search'), '100% & _'); assert.equal(params.get('quality'), 'true');
      assert.equal(options?.credentials, 'include');
      return Response.json(result());
    };
    assert.deepEqual(await api.orderPage({ ...query, scope: 'mine', status: 'workFinished', search: '100% & _', quality: true }), result());
  } finally { globalThis.fetch = original; }
});
test('поздний ответ старого фильтра не заменяет текущую страницу; его ошибка тоже игнорируется', async () => {
  const first = deferred<OrderPage>(), second = deferred<OrderPage>();
  let calls = 0;
  const loader = createOrderListLoader(() => ++calls === 1 ? first.promise : second.promise);
  const old = loader.load(query);
  const current = loader.load({ ...query, page: 1, search: 'новое' });
  second.resolve(result(1)); await current;
  first.reject(new ApiError('Старая сессия', 401)); await old;
  assert.equal(loader.getSnapshot().data?.page, 1);
  assert.equal(loader.getSnapshot().error, null);
  assert.equal(loader.getSnapshot().loading, false);
});
test('смена запроса скрывает строки и счётчики прежней выдачи до ответа', async () => {
  const pending = deferred<OrderPage>(); let calls = 0;
  const loader = createOrderListLoader(() => ++calls === 1 ? Promise.resolve(result()) : pending.promise);
  await loader.load(query);
  const next = loader.load({ ...query, scope: 'active', page: 1 });
  assert.equal(loader.getSnapshot().data, null); assert.equal(loader.getSnapshot().loading, true);
  pending.resolve(result(1)); await next;
});
test('обновление сохраняет страницу, ошибка оставляет прежние данные, повтор восстанавливает загрузку', async () => {
  let calls = 0;
  const seen: number[] = [];
  const loader = createOrderListLoader(async (q) => {
    seen.push(q.page); if (++calls === 2) throw new ApiError('Нет сети', 0);
    return result();
  });
  await loader.load(query); const initial = loader.getSnapshot().data;
  assert.equal(await loader.load(query), false);
  assert.equal(loader.getSnapshot().data, initial); assert.ok(loader.getSnapshot().error);
  assert.equal(await loader.load(query), true); assert.equal(loader.getSnapshot().error, null);
  assert.deepEqual(seen, [2, 2, 2]);
});
test('фоновый опрос не прерывает запрос страницы, размонтирование игнорирует поздние ответы', async () => {
  const pending = deferred<OrderPage>(); let calls = 0;
  const loader = createOrderListLoader(() => { calls++; return pending.promise; });
  const first = loader.load(query);
  assert.equal(await loader.load(query, true), false); assert.equal(calls, 1);
  loader.cancel(); pending.resolve(result()); await first;
  assert.equal(loader.getSnapshot().data, null);
});
test('после сокращения выдачи серверная последняя страница становится текущей', async () => {
  const loader = createOrderListLoader(async () => result(1, 20));
  await loader.load(query);
  assert.equal(loader.getSnapshot().data?.page, 1);
  await loader.load({ ...query, page: 1 });
  assert.equal(loader.getSnapshot().data?.totalCount, 20);
});
test('HTML ошибки не раскрываются; 401 сохраняет статус, обрыв чтения ответа обрабатывается', async () => {
  const original = globalThis.fetch;
  try {
    for (const status of [401, 403, 404, 409, 502]) {
      globalThis.fetch = async () => new Response('<html>internal proxy error</html>', { status });
      await assert.rejects(request('/order/page'), (err) => err instanceof ApiError && err.status === status && !err.message.includes('<html>'));
    }
    globalThis.fetch = async () => new Response('bad JSON', { status: 200 });
    await assert.rejects(request('/order/page'), /неожиданный ответ/);
    globalThis.fetch = async () => ({ text: async () => { throw new Error('stream closed'); } }) as Response;
    await assert.rejects(request('/order/page'), (err) => err instanceof ApiError && err.status === 0);
  } finally { globalThis.fetch = original; }
});
test('таймаут останавливает ожидание и не повторяет запрос изменения автоматически', async (t) => {
  const original = globalThis.fetch;
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let calls = 0;
  try {
    globalThis.fetch = (_url, options) => new Promise<Response>((_resolve, reject) => {
      calls++;
      options?.signal?.addEventListener('abort', () => reject(new Error('aborted')));
    });
    const pending = request('/order/id/review', 'POST', { approved: true });
    const rejected = assert.rejects(pending, (err) => err instanceof ApiError && err.status === 0 && err.message.includes('проверить результат'));
    t.mock.timers.tick(20000);
    await rejected;
    assert.equal(calls, 1);
  } finally { globalThis.fetch = original; t.mock.timers.reset(); }
});
