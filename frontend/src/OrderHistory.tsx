import { useEffect, useState } from 'react';
import { api } from './api';
import type { ApiHistoryEvent } from './api';
import { describeHistoryEvent, historyDate } from './historyPresentation';

export function OrderHistory({ orderId, revision }: { orderId: string; revision: string }) {
  const [events, setEvents] = useState<ApiHistoryEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [retry, setRetry] = useState(0);
  useEffect(() => {
    let active = true;
    let pending = false;
    setEvents([]);
    setLoading(true);
    setError('');
    async function load() {
      if (pending) return;
      pending = true;
      try {
        const result = await api.history(orderId);
        if (active) { setEvents(result); setError(''); }
      } catch (err) {
        if (active) {
          setEvents([]);
          setError(err instanceof Error ? err.message : 'Не удалось загрузить историю.');
        }
      } finally {
        pending = false;
        if (active) setLoading(false);
      }
    }
    void load();
    const timer = window.setInterval(() => { if (!document.hidden) void load(); }, 10000);
    return () => { active = false; clearInterval(timer); };
  }, [orderId, revision, retry]);

  return (
    <section className="detail-section" aria-label="История действий" aria-busy={loading}>
      <h3>История действий</h3>
      {loading ? <p className="muted" role="status">Загружаем историю…</p> : error ? (
        <div role="alert">
          <p className="error">{error}</p>
          <button className="text-button" onClick={() => setRetry((x) => x + 1)}>Повторить загрузку</button>
        </div>
      ) : events.length === 0 ? (
        <p className="muted">Сохранённых событий пока нет. Действия до включения журнала не записывались.</p>
      ) : (
        <ol className="timeline">
          {events.map((event) => (
            <li key={event.id}>
              <span className="timeline-dot" aria-hidden="true" />
              <div>
                <strong>{describeHistoryEvent(event)}</strong>
                <small><time dateTime={event.occurredAt}>{historyDate(event.occurredAt)}</time> · {event.actorNameSnapshot || 'Пользователь удалён'}</small>
                {event.comment && <p className="preserve-lines">{event.comment}</p>}
              </div>
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}
