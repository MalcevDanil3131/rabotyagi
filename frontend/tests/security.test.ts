import test from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError, parseRetryAfter } from '../src/api.ts';

test('429 сохраняет Retry-After и не повторяет вход или смену пароля автоматически', async () => {
  const original = globalThis.fetch;
  let calls = 0;
  try {
    globalThis.fetch = async () => { calls++; return Response.json({ error: 'Подождите', retryAfterSeconds: 12 }, { status: 429, headers: { 'Retry-After': '30' } }); };
    for (const action of [() => api.login('demo@example.invalid', 'placeholder', false),
      () => api.changePassword('placeholder', 'placeholder'), () => api.demoLogin('worker1')]) {
      await assert.rejects(action(), (err) => err instanceof ApiError && err.status === 429 && err.retryAfterSeconds === 30 && err.message === 'Подождите');
    }
    assert.equal(calls, 3);
    globalThis.fetch = async () => new Response('proxy error', { status: 429, headers: { 'Retry-After': '5' } });
    await assert.rejects(api.login('demo@example.invalid', 'placeholder', false), (err) => err instanceof ApiError && err.retryAfterSeconds === 5 && err.message.includes('5 сек.'));
    globalThis.fetch = async () => Response.json({ retryAfterSeconds: 7 }, { status: 429 });
    await assert.rejects(api.demoLogin('client'), (err) => err instanceof ApiError && err.retryAfterSeconds === 7);
  } finally { globalThis.fetch = original; }
});
test('Retry-After допускает секунды и HTTP-дату, ограничивает некорректное ожидание', () => {
  const now = Date.parse('2026-11-07T10:00:00Z');
  assert.equal(parseRetryAfter('15', now), 15);
  assert.equal(parseRetryAfter('0', now), 1);
  assert.equal(parseRetryAfter('999999999', now), 86400);
  assert.equal(parseRetryAfter('Sat, 07 Nov 2026 10:00:05 GMT', now), 5);
  assert.equal(parseRetryAfter('invalid', now), undefined);
  assert.equal(parseRetryAfter(null, now), undefined);
});
