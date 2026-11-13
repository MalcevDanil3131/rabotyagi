import { useState } from 'react';
import { api } from './api';
import type { ApiOrder, ApiWorker } from './api';

export function ReassignExecutorForm({ order, workers, busy, act }: {
  order: ApiOrder;
  workers: ApiWorker[];
  busy: boolean;
  act: (action: () => Promise<unknown>, message: string) => Promise<void>;
}) {
  const [opened, setOpened] = useState(false);
  const [executor, setExecutor] = useState('');
  const [reason, setReason] = useState('');
  const available = workers.filter((w) => w.id !== order.executorId && w.availableUntil && Date.parse(w.availableUntil) > Date.now());
  if (!opened) return <button className="button secondary" disabled={busy} onClick={() => setOpened(true)}>Заменить исполнителя</button>;
  return (
    <form className="form" onSubmit={async (event) => {
      event.preventDefault();
      if (!order.executorId || !reason.trim()) return;
      await act(async () => {
        await api.reassign(order.id, executor, order.executorId!, order.updated, reason.trim());
        setOpened(false);
      }, 'Исполнитель заменён. Заявка возвращена в статус «В пути».');
    }}>
      <p className="muted">Новый исполнитель начнёт работу заново и согласует стоимость с клиентом. Прежнее согласование будет сброшено. Последний отчёт останется доступен для просмотра; потребуется новая отправка отчёта.</p>
      <label>Новый исполнитель
        <select required value={executor} disabled={busy} onChange={(event) => setExecutor(event.target.value)}>
          <option value="">Выберите специалиста с открытым слотом</option>
          {available.map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
        </select>
      </label>
      {!available.length && <p className="muted">Нет других специалистов с открытым слотом.</p>}
      <label>Причина замены
        <textarea required maxLength={2000} value={reason} disabled={busy} onChange={(event) => setReason(event.target.value)} />
      </label>
      <div className="inline-actions">
        <button className="button primary" disabled={busy || !reason.trim() || !available.some((w) => w.id === executor)}>Подтвердить замену</button>
        <button type="button" className="button secondary" disabled={busy} onClick={() => setOpened(false)}>Отмена</button>
      </div>
    </form>
  );
}
