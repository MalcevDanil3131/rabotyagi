import { useState } from 'react';
import {
  AlertTriangle,
  Camera,
  Check,
  CheckCircle2,
  ChevronRight,
  CircleHelp,
  Info,
  Lightbulb,
  ShieldCheck,
  UsersRound,
} from 'lucide-react';
import type { ApiStatus } from './api';
import type { OrderType } from './domain';
import { categories } from './domain';
import { Dialog } from './components';
import {
  acceptanceChecklist,
  acceptanceNote,
  captureRules,
  orderTypePhotoTips,
  photoPhases,
  roleResponsibilities,
  workflowStageIndex,
  workflowStages,
} from './workflowHints';
import type { HintTone, OrderHint, PhotoPhase } from './workflowHints';

const toneIcons: Record<HintTone, typeof Info> = {
  info: Info,
  action: Lightbulb,
  warning: AlertTriangle,
  success: CheckCircle2,
};

export function WorkflowProgress({ status }: { status: ApiStatus }) {
  const index = workflowStageIndex(status);
  const stage = workflowStages[index];
  if (status === 'cancelled' || !stage) {
    return (
      <p className="workflow-cancelled">
        <Info size={15} aria-hidden="true" />
        Заявка отменена — этапы работы не применяются.
      </p>
    );
  }
  return (
    <section className="workflow-progress" aria-label={`Этап заявки: ${stage.title}`}>
      <ol className="workflow-track">
        {workflowStages.map((item, i) => {
          const done = i < index || (status === 'completed' && i === index);
          const current = i === index && status !== 'completed';
          return (
            <li
              key={item.id}
              className={`workflow-step ${done ? 'done' : current ? 'current' : 'upcoming'}`}
              aria-current={current ? 'step' : undefined}
            >
              <span className="workflow-dot" aria-hidden="true">
                {done ? <Check size={10} /> : i + 1}
              </span>
              <span className="workflow-label">{item.label}</span>
            </li>
          );
        })}
      </ol>
      <p className="workflow-caption">
        {status === 'completed' ? (
          <>
            <strong>Заявка закрыта.</strong> Фотоотчёт и история сохранены в карточке.
          </>
        ) : (
          <>
            <strong>
              Этап {index + 1} из {workflowStages.length}: {stage.title}.
            </strong>{' '}
            {stage.description}
          </>
        )}
      </p>
    </section>
  );
}

function HintCard({ hint }: { hint: OrderHint }) {
  const Icon = toneIcons[hint.tone];
  return (
    <article className={`hint-card ${hint.tone}`}>
      <span className="hint-icon" aria-hidden="true">
        <Icon size={16} />
      </span>
      <div>
        <h4>{hint.title}</h4>
        <p>{hint.summary}</p>
        {hint.bullets && hint.bullets.length > 0 && (
          <ul>
            {hint.bullets.map((item) => (
              <li key={item}>{item}</li>
            ))}
          </ul>
        )}
        {hint.source && <span className="hint-source">Регламент · {hint.source}</span>}
      </div>
    </article>
  );
}

export function OrderHints({
  hints,
  onOpenGuide,
}: {
  hints: OrderHint[];
  onOpenGuide: () => void;
}) {
  if (!hints.length) return null;
  return (
    <section className="order-hints" aria-label="Подсказки по текущему этапу">
      <header className="order-hints-head">
        <span>
          <Lightbulb size={15} aria-hidden="true" />
          Подсказка по этапу
        </span>
        <button type="button" className="text-button" onClick={onOpenGuide}>
          Памятка
          <ChevronRight size={14} aria-hidden="true" />
        </button>
      </header>
      <div className="order-hints-list">
        {hints.map((hint) => (
          <HintCard key={hint.id} hint={hint} />
        ))}
      </div>
    </section>
  );
}

export function PhotoGuide({
  orderType,
  compact = false,
}: {
  orderType: OrderType;
  compact?: boolean;
}) {
  const [phaseId, setPhaseId] = useState<PhotoPhase['id']>('before');
  const phase = photoPhases.find((item) => item.id === phaseId) ?? photoPhases[0];
  return (
    <section className={`photo-guide ${compact ? 'compact' : ''}`} aria-label="Чек-лист фото">
      <header className="photo-guide-head">
        <Camera size={18} aria-hidden="true" />
        <div>
          <h3>Фотофиксация по регламенту</h3>
          <p>Снимайте на месте и прикладывайте свежие кадры — до, в процессе и после работ.</p>
        </div>
      </header>
      <div className="photo-guide-tabs" role="tablist" aria-label="Этапы фотофиксации">
        {photoPhases.map((item) => (
          <button
            key={item.id}
            type="button"
            role="tab"
            aria-selected={item.id === phaseId}
            className={item.id === phaseId ? 'active' : ''}
            onClick={() => setPhaseId(item.id)}
          >
            {item.label}
            <span>{item.frames.length}</span>
          </button>
        ))}
      </div>
      <div className="photo-guide-panel" role="tabpanel">
        <strong>{phase.title}</strong>
        <p className="muted">{phase.note}</p>
        <ol className="photo-frame-list">
          {phase.frames.map((frame, index) => (
            <li key={frame.title}>
              <span className="frame-num" aria-hidden="true">
                {index + 1}
              </span>
              <div>
                <strong>{frame.title}</strong>
                <p>{frame.description}</p>
              </div>
            </li>
          ))}
        </ol>
      </div>
      <div className="photo-type-tip">
        {compact ? (
          <details className="photo-type-details">
            <summary>Советы для «{categories[orderType]}»</summary>
            <ul>
              {orderTypePhotoTips[orderType].map((tip) => (
                <li key={tip}>{tip}</li>
              ))}
            </ul>
          </details>
        ) : (
          <>
            <strong>Дополнительно для «{categories[orderType]}»</strong>
            <ul>
              {orderTypePhotoTips[orderType].map((tip) => (
                <li key={tip}>{tip}</li>
              ))}
            </ul>
          </>
        )}
      </div>
      {compact && (
        <p className="photo-guide-note">
          В отчёт можно приложить до 4 ключевых кадров. Полный набор снимите на объекте; если
          обязательный кадр снять нельзя — напишите причину в обсуждении.
        </p>
      )}
      <details className="capture-rules">
        <summary>Правила съёмки и ограничения</summary>
        <ul>
          {captureRules.map((rule) => (
            <li key={rule}>{rule}</li>
          ))}
        </ul>
      </details>
    </section>
  );
}

export function RegulationGuide({
  orderType,
  onClose,
}: {
  orderType: OrderType;
  onClose: () => void;
}) {
  const [tab, setTab] = useState<'steps' | 'photo' | 'acceptance'>('steps');
  const [selectedType, setSelectedType] = useState<OrderType>(orderType);
  return (
    <Dialog
      title="Памятка по заявке"
      eyebrow="Регламент фотофиксации и сдачи работ"
      onClose={onClose}
      wide
    >
      <div className="dialog-body regulation-guide">
        <div className="hint-tabs" role="tablist" aria-label="Разделы памятки">
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'steps'}
            className={tab === 'steps' ? 'active' : ''}
            onClick={() => setTab('steps')}
          >
            <CircleHelp size={15} aria-hidden="true" />
            Этапы
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'photo'}
            className={tab === 'photo' ? 'active' : ''}
            onClick={() => setTab('photo')}
          >
            <Camera size={15} aria-hidden="true" />
            Фото
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={tab === 'acceptance'}
            className={tab === 'acceptance' ? 'active' : ''}
            onClick={() => setTab('acceptance')}
          >
            <ShieldCheck size={15} aria-hidden="true" />
            Приёмка
          </button>
        </div>
        {tab === 'steps' && (
          <>
            <p className="muted">
              Порядок соответствует маршруту заявки в платформе: создание → назначение → выезд →
              фотофиксация → отчёт → приёмка → закрытие.
            </p>
            <ol className="guide-list">
              {workflowStages.map((stage, index) => (
                <li key={stage.id}>
                  <span className="guide-number" aria-hidden="true">
                    {index + 1}
                  </span>
                  <div>
                    <strong>{stage.title}</strong>
                    <p>{stage.description}</p>
                  </div>
                </li>
              ))}
            </ol>
          </>
        )}
        {tab === 'photo' && (
          <>
            <label className="guide-type">
              Вид работ
              <select
                value={selectedType}
                onChange={(event) => setSelectedType(event.target.value as OrderType)}
              >
                {Object.entries(categories).map(([key, label]) => (
                  <option key={key} value={key}>
                    {label}
                  </option>
                ))}
              </select>
            </label>
            <PhotoGuide orderType={selectedType} />
          </>
        )}
        {tab === 'acceptance' && (
          <>
            <section className="guide-section">
              <h3>Что проверить в фотоотчёте</h3>
              <ul>
                {acceptanceChecklist.map((item) => (
                  <li key={item}>{item}</li>
                ))}
              </ul>
            </section>
            <section className="guide-section">
              <h3>Срок приёмки</h3>
              <p>{acceptanceNote}</p>
            </section>
            <section className="guide-section">
              <h3>Если кадр снять невозможно</h3>
              <p>
                Напишите причину в обсуждении заявки: запрет на съёмку, отказ заказчика или
                технические причины. Диспетчер увидит пометку и решит, можно ли принять работу без
                кадра. Без причины неполный фотоотчёт возвращается на доработку.
              </p>
            </section>
            <section className="guide-section">
              <h3>Роли и ответственность</h3>
              <ul className="role-list">
                {roleResponsibilities.map((item) => (
                  <li key={item.role}>
                    <UsersRound size={15} aria-hidden="true" />
                    <span>
                      <strong>{item.role}.</strong> {item.text}
                    </span>
                  </li>
                ))}
              </ul>
            </section>
            <p className="guide-footer">
              Регламент «Фотофиксация и сдача работ», версия 1.0 (проект). Подсказки не заменяют
              договор и не отменяют проверку диспетчера.
            </p>
          </>
        )}
      </div>
    </Dialog>
  );
}
