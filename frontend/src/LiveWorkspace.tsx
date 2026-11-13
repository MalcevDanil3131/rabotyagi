import { useEffect, useState } from 'react';
import type { FormEvent } from 'react';
import { ArrowRight, UserRound } from 'lucide-react';
import { api, ApiError } from './api';
import type { Account, Address, DemoAccount } from './api';
import { DemoSwitcher } from './DemoSwitcher';
import { Dialog } from './components';
import ConnectedDashboard from './ConnectedDashboard';
import { useRequestCooldown } from './useRequestCooldown';

export default function LiveWorkspace() {
  const [account, setAccount] = useState<Account | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [auth, setAuth] = useState<'login' | 'register'>('login');
  const [busy, setBusy] = useState(false);
  const [demoAccounts, setDemoAccounts] = useState<DemoAccount[]>([]);
  const [switching, setSwitching] = useState(false);
  const [demoError, setDemoError] = useState('');
  const loginCooldown = useRequestCooldown();
  const demoCooldown = useRequestCooldown();
  useEffect(() => {
    let active = true;
    void api.demoAccounts().then((items) => { if (active) setDemoAccounts(items); }).catch(() => {});
    return () => { active = false; };
  }, []);
  async function switchDemo(key: string) {
    if (demoCooldown.seconds > 0) return;
    setSwitching(true);
    setDemoError('');
    setAccount(null); // Unmount all previous account state before changing the cookie.
    try { await api.demoLogin(key); setAccount(await api.account()); }
    catch (err) {
      demoCooldown.hold(err);
      setDemoError(err instanceof Error ? err.message : 'Не удалось сменить аккаунт.');
      try { setAccount(await api.account()); } catch { setAccount(null); }
    } finally { setSwitching(false); }
  }
  useEffect(() => {
    let active = true;
    api
      .account()
      .then((user) => {
        if (active) setAccount(user);
      })
      .catch((err) => {
        if (active && (!(err instanceof ApiError) || err.status !== 401)) setError(err.message);
      })
      .finally(() => {
        if (active) setLoading(false);
      });
    return () => {
      active = false;
    };
  }, []);
  async function authenticate(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (loginCooldown.seconds > 0 || busy) return;
    const form = new FormData(e.currentTarget);
    setBusy(true);
    setError('');
    try {
      const credentials = {
        email: String(form.get('email')).trim(),
        password: String(form.get('password')),
        isPersistent: form.get('persistent') === 'on',
      };
      if (auth === 'register')
        await api.register({
          ...credentials,
          name: String(form.get('name')).trim(),
          surname: String(form.get('surname')).trim(),
          phoneNumber: String(form.get('phone')).trim(),
        });
      else await api.login(credentials.email, credentials.password, credentials.isPersistent);
      setAccount(await api.account());
    } catch (err) {
      loginCooldown.hold(err);
      setError(err instanceof Error ? err.message : 'Не удалось войти.');
    } finally {
      setBusy(false);
    }
  }
  async function logout() {
    await api.logout();
    setAccount(null);
  }
  if (switching) return <main className="loading" role="status">Переключаем демо-аккаунт…</main>;
  const demoPanel = demoAccounts.length ? <DemoSwitcher accounts={demoAccounts} account={account} busy={busy || loading || demoCooldown.seconds > 0}
    error={demoCooldown.seconds > 0 ? `Переключение будет доступно через ${demoCooldown.seconds} сек.` : demoError} onSwitch={switchDemo} /> : undefined;
  if (account)
    return (
      <ConnectedDashboard
        key={account.id}
        demoPanel={demoPanel}
        account={account}
        onAccount={(updated) => setAccount((current) => current?.id === updated.id ? updated : current)}
        onLogout={logout}
        onExpired={() => setAccount((current) => current?.id === account.id ? null : current)}
      />
    );
  return (
    <div className="live-shell">
      <header className="live-header">
        <span className="brand">
          <span className="brand-mark">р</span>работяги<span className="brand-period">.</span>
        </span>
      </header>
      <main className="auth-layout">
        <div className="auth-intro">
          <span className="eyebrow">Ваше рабочее пространство</span>
          <h1>
            Хорошая работа
            <br />
            начинается здесь<span className="yellow">.</span>
          </h1>
          <p>
            Заявки, специалисты и контроль качества.
            <br />
            Всё, что нужно для работы, — в одном месте.
          </p>
          <div className="auth-detail">
            <span className="square-icon">
              <UserRound size={22} />
            </span>
            <span>
              Уже есть аккаунт?
              <br />
              <strong>Заявки и рабочие задачи в одном месте</strong>
            </span>
          </div>
        </div>
        <section className="auth-card">
          {demoPanel}
          <h2>{auth === 'login' ? 'С возвращением' : 'Создать аккаунт'}</h2>
          <p className="muted">
            {auth === 'login'
              ? 'Войдите, чтобы продолжить работу'
              : 'Регистрация клиентского аккаунта'}
          </p>
          <form className="form" onSubmit={authenticate}>
            {auth === 'register' && (
              <>
                <div className="form-grid">
                  <label>
                    Имя
                    <input name="name" autoComplete="given-name" required />
                  </label>
                  <label>
                    Фамилия
                    <input name="surname" autoComplete="family-name" required />
                  </label>
                </div>
                <label>
                  Телефон
                  <input name="phone" type="tel" autoComplete="tel" required />
                </label>
              </>
            )}
            <label>
              Почта
              <input
                name="email"
                type="email"
                autoComplete="email"
                placeholder="you@example.ru"
                required
              />
            </label>
            <label>
              Пароль
              <input
                name="password"
                type="password"
                autoComplete={auth === 'login' ? 'current-password' : 'new-password'}
                minLength={auth === 'register' ? 10 : undefined}
                maxLength={auth === 'register' ? 128 : 1024}
                required
              />
            </label>
            {auth === 'register' && (
              <small className="muted">
                От 10 до 128 символов: заглавная и строчная буквы, цифра и спецсимвол.
              </small>
            )}
            <label className="checkbox-label">
              <input name="persistent" type="checkbox" />
              Запомнить меня
            </label>
            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}
            <button className="button primary full" disabled={busy || loading || loginCooldown.seconds > 0}>
              {loginCooldown.seconds > 0 ? `Повторить через ${loginCooldown.seconds} сек.` : busy || loading
                ? 'Подключаемся…'
                : auth === 'login'
                  ? 'Войти'
                  : 'Зарегистрироваться'}
              <ArrowRight size={17} />
            </button>
          </form>
          <button
            className="text-button auth-toggle"
            disabled={busy}
            onClick={() => {
              setAuth(auth === 'login' ? 'register' : 'login');
              setError('');
            }}
          >
            {auth === 'login' ? 'Нет аккаунта? Зарегистрироваться' : 'Уже есть аккаунт? Войти'}
          </button>
        </section>
      </main>
    </div>
  );
}

export function AddressForm({
  address,
  onClose,
  onSave,
}: {
  address: Address | null;
  onClose: () => void;
  onSave: (address: Address) => Promise<void>;
}) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  return (
    <Dialog title="Адрес для выезда" onClose={onClose}>
      <form
        className="dialog-body form"
        onSubmit={async (e) => {
          e.preventDefault();
          const data = new FormData(e.currentTarget);
          setBusy(true);
          setError('');
          try {
            await onSave({
              country: String(data.get('country')).trim(),
              city: String(data.get('city')).trim(),
              street: String(data.get('street')).trim(),
              houseNumber: String(data.get('house')).trim(),
              apartmentNumber: String(data.get('apartment')).trim() || null,
              floorNumber: data.get('floor') ? Number(data.get('floor')) : null,
              intercomNumber: String(data.get('intercom')).trim() || null,
            });
          } catch (err) {
            setError(err instanceof Error ? err.message : 'Не удалось сохранить адрес.');
          } finally {
            setBusy(false);
          }
        }}
      >
        <div className="form-grid">
          <label>
            Страна
            <input name="country" defaultValue={address?.country || 'Россия'} required />
          </label>
          <label>
            Город
            <input name="city" defaultValue={address?.city || ''} required />
          </label>
        </div>
        <label>
          Улица
          <input name="street" defaultValue={address?.street || ''} required />
        </label>
        <div className="form-grid">
          <label>
            Дом
            <input name="house" defaultValue={address?.houseNumber || ''} required />
          </label>
          <label>
            Квартира / офис
            <input name="apartment" defaultValue={address?.apartmentNumber || ''} />
          </label>
          <label>
            Этаж
            <input
              name="floor"
              type="number"
              min={-5}
              max={200}
              defaultValue={address?.floorNumber ?? ''}
            />
          </label>
          <label>
            Домофон
            <input name="intercom" defaultValue={address?.intercomNumber || ''} />
          </label>
        </div>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <button className="button primary" disabled={busy}>
          {busy ? 'Сохраняем…' : 'Сохранить адрес'}
        </button>
      </form>
    </Dialog>
  );
}
export function PasswordForm({
  onClose,
  onSave,
}: {
  onClose: () => void;
  onSave: (oldPassword: string, newPassword: string) => Promise<void>;
}) {
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const cooldown = useRequestCooldown();
  return (
    <Dialog title="Изменить пароль" onClose={onClose}>
      <form
        className="dialog-body form"
        onSubmit={async (e) => {
          e.preventDefault();
          if (cooldown.seconds > 0 || busy) return;
          const data = new FormData(e.currentTarget);
          setBusy(true);
          setError('');
          try {
            if (data.get('new') !== data.get('confirm'))
              throw new Error('Новые пароли не совпадают.');
            await onSave(String(data.get('old')), String(data.get('new')));
          } catch (err) {
            cooldown.hold(err);
            setError(err instanceof Error ? err.message : 'Не удалось изменить пароль.');
          } finally {
            setBusy(false);
          }
        }}
      >
        <label>
          Текущий пароль
          <input name="old" type="password" autoComplete="current-password" required />
        </label>
        <label>
          Новый пароль
          <input name="new" type="password" autoComplete="new-password" required minLength={10} maxLength={128} />
        </label>
        <label>
          Повторите новый пароль
          <input
            name="confirm"
            type="password"
            autoComplete="new-password"
            required
            minLength={10}
            maxLength={128}
          />
        </label>
        {error && (
          <p className="error" role="alert">
            {error}
          </p>
        )}
        <small className="muted">От 10 до 128 символов: заглавная и строчная буквы, цифра и спецсимвол.</small>
        <button className="button primary" disabled={busy || cooldown.seconds > 0}>
          {cooldown.seconds > 0 ? `Повторить через ${cooldown.seconds} сек.` : busy ? 'Сохраняем…' : 'Изменить пароль'}
        </button>
      </form>
    </Dialog>
  );
}
