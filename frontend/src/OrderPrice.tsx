import { useState } from 'react';
import { api } from './api';
import type { Account, ApiOrder } from './api';
import { formatPrice, parsePrice, priceAgreed } from './pricePresentation';

export function OrderPrice({ order, account, busy, act }: {
  order: ApiOrder; account: Account; busy: boolean;
  act: (action: () => Promise<unknown>, message: string) => Promise<void>;
}) {
  const [amount, setAmount] = useState(order.priceAmount?.toString() ?? '');
  const [scope, setScope] = useState(order.priceScope ?? '');
  const [reason, setReason] = useState('');
  const [error, setError] = useState('');
  const worker = (account.roles || [account.roleName]).includes('Worker') && order.executorId === account.id;
  const owner = order.initiatorId === account.id;
  const beforeWork = order.status === 'workerOnTheWay';
  const agreed = priceAgreed(order);
  const labels = { none: 'Стоимость пока не предложена', proposed: 'Ожидает решения клиента',
    accepted: 'Согласована клиентом', rejected: 'Клиент отклонил предложение' };
  return <section className="detail-section order-price" aria-label="Стоимость заказа">
    <h3>Стоимость заказа</h3>
    <strong>{formatPrice(order.priceAmount)}</strong>
    <p>{labels[order.priceStatus] || labels.none}</p>
    {order.priceScope && <p className="preserve-lines">{order.priceScope}</p>}
    {order.priceAcceptedAt && <small>Согласовано {new Date(order.priceAcceptedAt).toLocaleString('ru-RU')}</small>}
    {beforeWork && !agreed && <p className="notice">До согласования стоимости начать работу нельзя.</p>}
    {worker && beforeWork && order.priceStatus !== 'accepted' && <form className="form" onSubmit={async (e) => {
      e.preventDefault(); setError('');
      const price = parsePrice(amount);
      if (price == null || !scope.trim()) { setError('Укажите сумму от 0,01 до 10 000 000 ₽ (до двух знаков после запятой) и состав работ.'); return; }
      await act(() => api.proposePrice(order.id, price, scope.trim(), order.updated), 'Стоимость отправлена клиенту.');
    }}>
      <label>Полная стоимость, ₽<input inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} required /></label>
      <label>Что входит в стоимость<textarea value={scope} onChange={(e) => setScope(e.target.value)} required maxLength={2000} /></label>
      <small className="muted">Укажите итоговую сумму с материалами и всеми расходами. После согласования менять её нельзя.</small>
      {error && <p className="error" role="alert">{error}</p>}
      <button className="button primary" disabled={busy}>{order.priceStatus === 'proposed' ? 'Обновить предложение' : 'Предложить стоимость'}</button>
    </form>}
    {owner && beforeWork && order.priceStatus === 'proposed' && <div className="form">
      <button className="button primary" disabled={busy} onClick={() => void act(
        () => api.decidePrice(order.id, true, '', order.updated), 'Стоимость согласована.')}>Согласовать {formatPrice(order.priceAmount)}</button>
      <label>Причина отказа<textarea value={reason} onChange={(e) => setReason(e.target.value)} maxLength={2000} /></label>
      <button className="button secondary" disabled={busy || !reason.trim()} onClick={() => void act(
        () => api.decidePrice(order.id, false, reason.trim(), order.updated), 'Предложение отклонено.')}>Отклонить стоимость</button>
    </div>}
  </section>;
}
