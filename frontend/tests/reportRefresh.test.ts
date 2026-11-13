import test from 'node:test';
import assert from 'node:assert/strict';
import { refreshOrderView } from '../src/refreshOrderView.ts';

test('после сохранения отчёта карточка обновляется до закрытия формы, без фонового таймера', async () => {
  const calls: string[] = [];
  let card = 'inProgress';
  let submitted = false;
  await Promise.resolve().then(() => { submitted = true; });
  await refreshOrderView({ selected: 'order', reportId: 'order', afterMutation: true,
    loadList: async () => { assert.equal(submitted, true); calls.push('list'); },
    loadWorkers: async () => { calls.push('workers'); },
    loadDetail: async () => { card = 'workFinished'; calls.push('detail'); },
  });
  assert.equal(card, 'workFinished'); assert.deepEqual(calls, ['list', 'workers', 'detail']);
});
test('фоновое обновление не перезаписывает открытую форму отчёта', async () => {
  let details = 0;
  const input = { selected: 'order', reportId: 'order', afterMutation: false,
    loadList: async () => {}, loadWorkers: async () => {}, loadDetail: async () => { details++; } };
  await refreshOrderView(input); assert.equal(details, 0);
  await refreshOrderView({ ...input, reportId: null }); assert.equal(details, 1);
  await refreshOrderView({ ...input, selected: null, afterMutation: true }); assert.equal(details, 1);
});
