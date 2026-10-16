import { useEffect, useId, useRef } from 'react';
import type { ReactNode } from 'react';
import {
  X,
  Wrench,
  Zap,
  Sofa,
  Paintbrush,
  Truck,
  Shapes,
  ArrowUpRight,
  Search,
} from 'lucide-react';
import { categories, statuses, formatDate, formatTime } from './domain';
import type { Order, OrderType, Status, Worker } from './domain';

export function CategoryIcon({ type, size = 19 }: { type: OrderType; size?: number }) {
  const Icon =
    {
      sanitary: Wrench,
      electric: Zap,
      furniture: Sofa,
      cosmetic: Paintbrush,
      movement: Truck,
      other: Shapes,
    }[type] || Shapes;
  return <Icon size={size} strokeWidth={1.7} aria-hidden="true" />;
}
export function Badge({ status }: { status: Status }) {
  return (
    <span className={`badge status-${status}`}>
      <span className="status-dot" />
      {statuses[status]}
    </span>
  );
}
export function Avatar({ worker, small = false }: { worker: Worker; small?: boolean }) {
  return (
    <span className={`avatar ${worker.color} ${small ? 'small' : ''}`} aria-hidden="true">
      {worker.initials}
    </span>
  );
}
export function Empty({ title, text }: { title: string; text: string }) {
  return (
    <div className="empty">
      <span className="empty-mark">✓</span>
      <h3>{title}</h3>
      <p>{text}</p>
    </div>
  );
}
export function Dialog({
  title,
  eyebrow,
  children,
  onClose,
  wide = false,
  drawer = false,
}: {
  title: string;
  eyebrow?: string;
  children: ReactNode;
  onClose: () => void;
  wide?: boolean;
  drawer?: boolean;
}) {
  const ref = useRef<HTMLDialogElement>(null);
  const titleId = useId();
  useEffect(() => {
    const dialog = ref.current;
    dialog?.showModal();
    return () => dialog?.close();
  }, []);
  return (
    <dialog
      ref={ref}
      className={`dialog ${wide ? 'wide' : ''} ${drawer ? 'drawer' : ''}`}
      aria-labelledby={titleId}
      onCancel={(event) => {
        event.stopPropagation();
        onClose();
      }}
      onClick={(e) => {
        if (e.target === e.currentTarget) {
          const box = e.currentTarget.getBoundingClientRect();
          if (
            e.clientX < box.left ||
            e.clientX > box.right ||
            e.clientY < box.top ||
            e.clientY > box.bottom
          )
            onClose();
        }
      }}
    >
      <header className="dialog-header">
        <div>
          {eyebrow && <span className="eyebrow">{eyebrow}</span>}
          <h2 id={titleId}>{title}</h2>
        </div>
        <button className="icon-button" type="button" onClick={onClose} aria-label="Закрыть">
          <X size={21} />
        </button>
      </header>
      {children}
    </dialog>
  );
}
export function OrdersTable({
  orders,
  workers,
  onSelect,
}: {
  orders: Order[];
  workers: Worker[];
  onSelect: (id: string) => void;
}) {
  return (
    <div className="orders-list" role="list" aria-label="Заявки">
      <div className="table-head" aria-hidden="true">
        <span>Заявка</span>
        <span>Специалист</span>
        <span>Время</span>
        <span>Статус</span>
        <span />
      </div>
      {orders.map((order) => {
        const worker = workers.find((w) => w.id === order.workerId);
        return (
          <div role="listitem" key={order.id}>
            <button
              className="order-row"
              onClick={() => onSelect(order.id)}
              aria-label={`Открыть заявку ${order.number}: ${order.title}`}
            >
              <span className="order-primary">
                <span className={`category-icon type-${order.type}`}>
                  <CategoryIcon type={order.type} />
                </span>
                <span className="order-copy">
                  <span className="order-number">
                    #{order.number} <span>· {categories[order.type]}</span>
                  </span>
                  <strong>{order.title}</strong>
                  <span className="muted address-line">{order.address}</span>
                </span>
              </span>
              <span className="order-worker">
                {worker ? (
                  <>
                    <Avatar worker={worker} small />
                    <span>
                      {worker.name.split(' ')[0]}
                      <span className="muted block">
                        {worker.name.split(' ').slice(1).join(' ')}
                      </span>
                    </span>
                  </>
                ) : (
                  <span className="muted">Не назначен</span>
                )}
              </span>
              <span className="order-time">
                {order.scheduledAt ? formatTime(order.scheduledAt) : 'Согласуется'}
                {order.scheduledAt && (
                  <span className="muted block">{formatDate(order.scheduledAt)}</span>
                )}
              </span>
              <Badge status={order.status} />
              <ArrowUpRight className="row-arrow" size={17} />
            </button>
          </div>
        );
      })}
    </div>
  );
}
export function SearchField({
  value,
  onChange,
}: {
  value: string;
  onChange: (value: string) => void;
}) {
  return (
    <label className="search">
      <Search size={18} />
      <input
        aria-label="Поиск по заявкам"
        placeholder="Номер, адрес или услуга"
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      <kbd>/</kbd>
    </label>
  );
}
