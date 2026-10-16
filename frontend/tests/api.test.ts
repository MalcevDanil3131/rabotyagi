import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, api, request } from '../src/api.ts';

test('POST без тела ответа считается успехом; cookie передаются в API', async () => {
  const original = globalThis.fetch;
  let options: RequestInit | undefined;
  globalThis.fetch = async (_url, init) => {
    options = init;
    return new Response(null, { status: 200 });
  };
  try {
    assert.equal(
      await api.saveAddress({
        country: 'Россия',
        city: 'Екатеринбург',
        street: 'Тестовая',
        houseNumber: '1',
      }),
      undefined,
    );
    assert.equal(options?.credentials, 'include');
    assert.equal(options?.method, 'POST');
  } finally {
    globalThis.fetch = original;
  }
});

test('ошибки API и ASP.NET validation передаются форме', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async () =>
      new Response(JSON.stringify({ error: 'Адрес не заполнен' }), { status: 400 });
    await assert.rejects(
      api.createOrder({
        title: 'Розетка',
        orderType: 'electric',
        comment: 'Заменить розетку',
        addressText: 'Тестовая, 1',
        scheduledAt: null,
      }),
      /Адрес не заполнен/,
    );
    globalThis.fetch = async () =>
      new Response(JSON.stringify({ errors: { Email: ['Неверный адрес почты'] } }), {
        status: 400,
      });
    await assert.rejects(request('/account'), /Неверный адрес почты/);
    globalThis.fetch = async () => new Response(null, { status: 401 });
    await assert.rejects(
      request('/account'),
      (error) => error instanceof ApiError && error.status === 401,
    );
  } finally {
    globalThis.fetch = original;
  }
});

test('недоступный API возвращает ошибку', async () => {
  const original = globalThis.fetch;
  try {
    globalThis.fetch = async () => new Response(null, { status: 502 });
    await assert.rejects(request('/account'), /Сервер API недоступен/);
    globalThis.fetch = async () => {
      throw new TypeError('fetch failed');
    };
    await assert.rejects(
      request('/account'),
      (error) => error instanceof ApiError && error.status === 0,
    );
  } finally {
    globalThis.fetch = original;
  }
});

test('новая заявка отправляет адрес и время, возвращает настоящий ID', async () => {
  const original = globalThis.fetch;
  const input = {
    title: 'Розетка',
    orderType: 'electric' as const,
    comment: 'Заменить розетку',
    addressText: 'Тестовая, 1',
    scheduledAt: '2026-10-11T10:00:00Z',
  };
  let sent: unknown;
  globalThis.fetch = async (_url, options) => {
    sent = JSON.parse(String(options?.body));
    return new Response(JSON.stringify({ id: 'order-from-server', ...input }), { status: 201 });
  };
  try {
    const order = await api.createOrder(input);
    assert.equal(order.id, 'order-from-server');
    assert.deepEqual(sent, input);
  } finally {
    globalThis.fetch = original;
  }
});

test('фотоотчёт передаётся как multipart без ручного Content-Type', async () => {
  const original = globalThis.fetch;
  const form = new FormData();
  form.append('Summary', 'Работа завершена');
  form.append('Photos', new Blob(['photo'], { type: 'image/jpeg' }), 'report.jpg');
  let options: RequestInit | undefined;
  globalThis.fetch = async (_url, init) => {
    options = init;
    return new Response(null, { status: 200 });
  };
  try {
    await api.report('order-id', form);
    assert.equal(options?.body, form);
    assert.deepEqual(options?.headers, {});
    assert.equal(options?.credentials, 'include');
    globalThis.fetch = async () =>
      new Response(JSON.stringify({ error: 'Заявка уже занята' }), { status: 409 });
    await assert.rejects(
      api.assign('order-id', 'worker-id'),
      (err) => err instanceof ApiError && err.status === 409,
    );
  } finally {
    globalThis.fetch = original;
  }
});
