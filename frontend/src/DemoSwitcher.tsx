import { useState } from 'react';
import type { Account, DemoAccount } from './api';
import { Dialog } from './components';

export function DemoSwitcher({ accounts, account, busy, error, onSwitch }: {
  accounts: DemoAccount[]; account: Account | null; busy: boolean; error: string;
  onSwitch: (key: string) => Promise<void>;
}) {
  const [opened, setOpened] = useState(false);
  const current = accounts.find((x) => x.id === account?.id);
  const choices = <div className="demo-choices">
    <p className="muted">Учебная база. Выберите, от чьего имени показать работу сервиса.</p>
    <div className="demo-account-grid">
      {accounts.map((item) => <button key={item.key} type="button"
        className={`button ${current?.key === item.key ? 'primary' : 'secondary'}`}
        disabled={busy || current?.key === item.key} onClick={() => void onSwitch(item.key)}>
        {item.label}{current?.key === item.key ? ' · сейчас' : ''}
      </button>)}
    </div>
    <small className="muted">Вкладки одного профиля браузера используют общий аккаунт. Для двух одновременных ролей откройте разные профили браузера.</small>
    {error && <p className="error" role="alert">{error}</p>}
  </div>;
  if (!account) return <section className="demo-login"><h3>Быстрый вход для показа</h3>{choices}</section>;
  return <div className="demo-toolbar">
    <span className="demo-label">ДЕМО · {current?.label || `${account.name} ${account.surname}`}</span>
    <button className="button secondary" disabled={busy} onClick={() => setOpened(true)}>Сменить аккаунт</button>
    {current?.key !== 'superadmin' && <button className="text-button" disabled={busy} onClick={() => void onSwitch('superadmin')}>К суперадмину</button>}
    {error && !opened && <span className="error" role="alert">{error}</span>}
    {opened && <Dialog title="Демонстрационные аккаунты" onClose={() => setOpened(false)}>
      <div className="dialog-body">{choices}</div>
    </Dialog>}
  </div>;
}
