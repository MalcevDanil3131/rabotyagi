import test from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError } from '../src/api.ts';
import { adminQueues } from '../src/adminQueues.ts';

test('демовход передаёт явный заголовок, cookie и ключ, но не пароль или роль', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async (url, options) => {
      assert.equal(url, '/api/demo/login/worker1');
      assert.equal(options?.method, 'POST');
      assert.equal(options?.credentials, 'include');
      assert.deepEqual(options?.headers, { 'X-Rabotyagi-Demo': '1' });
      assert.equal(options?.body, undefined);
      return new Response(null, { status: 200 });
    };
    await api.demoLogin('worker1');
    globalThis.fetch = async () => Response.json({ error: 'Недоступно' }, { status: 409 });
    await assert.rejects(api.demoLogin('worker2'), (err) => err instanceof ApiError && err.status === 409);
    globalThis.fetch = async () => new Response(null, { status: 404 });
    await assert.rejects(api.demoAccounts(), (err) => err instanceof ApiError && err.status === 404);
  } finally { globalThis.fetch = original; }
});

test('очереди используют серверный фильтр на каждой странице и отдельные счётчики', async () => {
  assert.deepEqual(adminQueues.map((q) => q.status), ['created', 'published', 'workFinished', 'approved']);
  const original = globalThis.fetch;
  const paths: string[] = [];
  try {
    globalThis.fetch = async (url) => { paths.push(String(url)); return Response.json([]); };
    await api.orders(1, 'created');
    await api.orders(2, 'created');
    await api.orderQueues();
    await api.orders();
    assert.deepEqual(paths, ['/api/order?page=1&pageSize=100&status=created', '/api/order?page=2&pageSize=100&status=created',
      '/api/order/queues', '/api/order?page=1&pageSize=100']);
  } finally { globalThis.fetch = original; }
});
