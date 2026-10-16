import { useState } from 'react';
import type { FormEvent } from 'react';
import { ArrowRight, ImagePlus, Trash2, Send } from 'lucide-react';
import { CategoryIcon, Dialog } from './components';
import { categories } from './domain';
import type { Order, OrderType, Report, Photo } from './domain';

export interface OrderInput {
  title: string;
  type: OrderType;
  comment: string;
  address: string;
  scheduledAt: string;
}
export function CreateOrder({
  onClose,
  onSave,
  defaultAddress = '',
}: {
  onClose: () => void;
  onSave: (input: OrderInput) => Promise<void>;
  defaultAddress?: string;
}) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [type, setType] = useState<OrderType>('sanitary');
  async function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    const data = new FormData(e.currentTarget);
    setBusy(true);
    setError('');
    try {
      await onSave({
        title: String(data.get('title') || categories[type]).trim(),
        type,
        comment: String(data.get('comment')).trim(),
        address: String(data.get('address')).trim(),
        scheduledAt: String(data.get('date')),
      });
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось создать заявку');
    } finally {
      setBusy(false);
    }
  }
  const localDate = new Date(Date.now() - new Date().getTimezoneOffset() * 60000)
    .toISOString()
    .slice(0, 16);
  return (
    <Dialog title="Новая заявка" eyebrow="Расскажите, чем помочь" onClose={onClose} wide>
      <form className="dialog-body form" onSubmit={submit}>
        <fieldset className="category-field">
          <legend>Что нужно сделать?</legend>
          <div className="category-grid">
            {Object.entries(categories).map(([key, label]) => (
              <button
                type="button"
                className={`category-option ${type === key ? 'selected' : ''}`}
                aria-pressed={type === key}
                onClick={() => setType(key as OrderType)}
                key={key}
              >
                <CategoryIcon type={key as OrderType} />
                <span>{label}</span>
              </button>
            ))}
          </div>
        </fieldset>
        <label>
          Короткое название
          <input name="title" placeholder="Например, заменить смеситель" required maxLength={100} />
        </label>
        <label>
          Описание
          <textarea
            name="comment"
            placeholder="Что случилось, что уже куплено и на что обратить внимание"
            required
            minLength={10}
            maxLength={2000}
            rows={4}
          />
        </label>
        <div className="form-grid">
          <label>
            Адрес
            <input
              name="address"
              placeholder="Улица, дом, квартира"
              defaultValue={defaultAddress}
              required
              maxLength={200}
            />
          </label>
          <label>
            Удобное время
            <input
              name="date"
              type="datetime-local"
              min={localDate}
              defaultValue={localDate}
              required
            />
          </label>
        </div>
        <p className="form-note">
          После создания администратор проверит заявку и передаст её специалистам.
        </p>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <footer className="form-footer">
          <button type="button" className="button secondary" onClick={onClose} disabled={busy}>
            Отмена
          </button>
          <button className="button primary" disabled={busy}>
            {busy ? 'Отправляем…' : 'Создать заявку'}
            <ArrowRight size={17} />
          </button>
        </footer>
      </form>
    </Dialog>
  );
}

async function preparePhoto(file: File): Promise<Photo> {
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type))
    throw new Error('Выберите фотографию в формате JPG, PNG или WebP.');
  if (file.size > 10 * 1024 * 1024) throw new Error('Каждая фотография должна быть меньше 10 МБ.');
  const image = await createImageBitmap(file);
  const scale = Math.min(1, 1100 / Math.max(image.width, image.height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(image.width * scale);
  canvas.height = Math.round(image.height * scale);
  const context = canvas.getContext('2d');
  if (!context) {
    image.close();
    throw new Error('Не удалось обработать фотографию.');
  }
  context.drawImage(image, 0, 0, canvas.width, canvas.height);
  image.close();
  return { id: crypto.randomUUID(), name: file.name, url: canvas.toDataURL('image/jpeg', 0.65) };
}

export function ReportForm({
  order,
  onClose,
  onSave,
}: {
  order: Order;
  onClose: () => void;
  onSave: (report: Report) => void | Promise<void>;
}) {
  const [summary, setSummary] = useState(order.report?.summary || '');
  const [checks, setChecks] = useState<[boolean, boolean, boolean]>([false, false, false]);
  const [photos, setPhotos] = useState<Photo[]>(order.report?.photos || []);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  async function upload(files: FileList | null) {
    if (!files) return;
    if (photos.length + files.length > 4) {
      setError('Можно добавить до четырёх фотографий.');
      return;
    }
    setBusy(true);
    setError('');
    try {
      const added = await Promise.all(Array.from(files).map(preparePhoto));
      setPhotos((current) => [...current, ...added]);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Не удалось загрузить фото.');
    } finally {
      setBusy(false);
    }
  }
  return (
    <Dialog title="Отчёт о работе" eyebrow={`Заявка #${order.number}`} onClose={onClose} wide>
      <form
        className="dialog-body form"
        onSubmit={async (e) => {
          e.preventDefault();
          if (!photos.length) {
            setError('Добавьте хотя бы одну фотографию результата.');
            return;
          }
          setBusy(true);
          try {
            await onSave({
              summary: summary.trim(),
              checks,
              photos,
              submittedAt: new Date().toISOString(),
            });
          } catch (err) {
            setError(err instanceof Error ? err.message : 'Не удалось сохранить отчёт.');
          } finally {
            setBusy(false);
          }
        }}
      >
        <label>
          Что было сделано?
          <textarea
            placeholder="Опишите выполненную работу и результат проверки"
            value={summary}
            onChange={(e) => setSummary(e.target.value)}
            required
            minLength={10}
            maxLength={2000}
            rows={4}
          />
        </label>
        <fieldset className="checklist">
          <legend>Проверьте перед отправкой</legend>
          {[
            'Работа выполнена в полном объёме',
            'Результат проверен вместе с клиентом',
            'Рабочее место убрано',
          ].map((label, i) => (
            <label key={label}>
              <input
                type="checkbox"
                required
                checked={checks[i]}
                onChange={(e) =>
                  setChecks(
                    (current) =>
                      current.map((value, index) =>
                        index === i ? e.target.checked : value,
                      ) as typeof checks,
                  )
                }
              />
              <span>{label}</span>
            </label>
          ))}
        </fieldset>
        <div>
          <span className="field-label">Фото результата</span>
          <p className="muted">До 4 фотографий · JPG, PNG, WebP · до 10 МБ каждая</p>
          <div className="photo-grid editable">
            {photos.map((photo) => (
              <div key={photo.id}>
                <img src={photo.url} alt={photo.name} />
                <button
                  className="icon-button photo-remove"
                  type="button"
                  aria-label={`Удалить ${photo.name}`}
                  disabled={busy}
                  onClick={() => setPhotos((current) => current.filter((p) => p.id !== photo.id))}
                >
                  <Trash2 size={16} />
                </button>
              </div>
            ))}
          </div>
          {photos.length < 4 && (
            <label className="upload">
              <ImagePlus size={23} />
              <span>{busy ? 'Обрабатываем фотографии…' : 'Добавить фотографии'}</span>
              <input
                type="file"
                accept="image/jpeg,image/png,image/webp"
                multiple
                disabled={busy}
                onChange={(e) => {
                  void upload(e.target.files);
                  e.target.value = '';
                }}
              />
            </label>
          )}
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <footer className="form-footer">
          <button type="button" className="button secondary" onClick={onClose}>
            Отмена
          </button>
          <button className="button primary" disabled={busy}>
            <Send size={17} />
            Отправить диспетчеру
          </button>
        </footer>
      </form>
    </Dialog>
  );
}
