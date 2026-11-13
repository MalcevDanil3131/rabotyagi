import test from 'node:test';
import assert from 'node:assert/strict';
import { api, ApiError } from '../src/api.ts';
import { parsePrice, priceAgreed, formatPrice, priceSummary } from '../src/pricePresentation.ts';
import { describeHistoryEvent } from '../src/historyPresentation.ts';
import type { ApiHistoryEvent } from '../src/api.ts';

test('стоимость допускает копейки и запятую, не округляет неверный ввод', () => {
  assert.equal(parsePrice(' 2500,75 '), 2500.75);
  assert.equal(parsePrice('0.01'), 0.01);
  for (const input of ['', '0', '-10', '1.001', '1e3', 'NaN', '10000000.01']) assert.equal(parsePrice(input), null);
  assert.match(formatPrice(2500.75), /2\s500,75\s₽/);
});
test('список показывает актуальную сумму и решение клиента', () => {
  for (const [priceStatus, label] of [['proposed', 'ожидает согласования'], ['accepted', 'согласована'], ['rejected', 'отклонена клиентом']] as const) {
    const text = priceSummary({ priceStatus, priceAmount: 3200, status: 'workerOnTheWay' });
    assert.match(text, /3\s200,00\s₽/);
    assert.ok(text.endsWith(label));
  }
});
test('новым и старым заявкам без суммы не приписывается цена', () => {
  const order = { priceStatus: 'none' as const, priceAmount: null };
  assert.equal(priceSummary({ ...order, status: 'created' }), 'Стоимость после назначения');
  assert.equal(priceSummary({ ...order, status: 'published' }), 'Стоимость после назначения');
  assert.equal(priceSummary({ ...order, status: 'workerOnTheWay' }), 'Ожидает предложения исполнителя');
  assert.equal(priceSummary({ ...order, status: 'completed' }), 'Стоимость не зафиксирована');
});
test('начало работы требует согласования именно с текущим исполнителем', () => {
  const order = { priceStatus: 'accepted' as const, priceAmount: 100, priceExecutorId: 'worker', executorId: 'worker' };
  assert.equal(priceAgreed(order), true);
  for (const priceStatus of ['none', 'proposed', 'rejected'] as const) assert.equal(priceAgreed({ ...order, priceStatus }), false);
  assert.equal(priceAgreed({ ...order, executorId: 'new-worker' }), false);
  assert.equal(priceAgreed({ ...order, priceAmount: null }), false);
});
test('предложение и решение передают точную версию; конфликт не скрывается', async () => {
  const original = globalThis.fetch;
  const seen: { url: string; body: unknown }[] = [];
  try {
    globalThis.fetch = async (url, options) => {
      assert.equal(options?.method, 'POST'); assert.equal(options?.credentials, 'include');
      seen.push({ url: String(url), body: JSON.parse(String(options?.body)) });
      return new Response(null, { status: 200 });
    };
    await api.proposePrice('id', 2500.75, 'Работа и материалы', 'exact-version');
    await api.decidePrice('id', false, 'Дорого', 'exact-version');
    assert.deepEqual(seen, [
      { url: '/api/order/id/price', body: { amount: 2500.75, scope: 'Работа и материалы', expectedUpdated: 'exact-version' } },
      { url: '/api/order/id/price/decision', body: { accepted: false, reason: 'Дорого', expectedUpdated: 'exact-version' } },
    ]);
    globalThis.fetch = async () => Response.json({ error: 'Предложение изменилось' }, { status: 409 });
    await assert.rejects(api.decidePrice('id', true, '', 'old'), (e) => e instanceof ApiError && e.status === 409);
  } finally { globalThis.fetch = original; }
});
test('история цены содержит русское действие и денежный снимок', () => {
  for (const [eventType, text] of [['priceProposed', 'предложил'], ['priceAccepted', 'согласовал'], ['priceRejected', 'отклонил']] as const) {
    const result = describeHistoryEvent({ eventType, priceAmount: 2500.75 } as ApiHistoryEvent);
    assert.ok(result.includes(text)); assert.match(result, /2\s500,75\s₽/);
  }
});
