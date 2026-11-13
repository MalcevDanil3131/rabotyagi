import { useEffect, useState } from 'react';
import { ApiError } from './api';

export function useRequestCooldown() {
  const [until, setUntil] = useState(0);
  const [now, setNow] = useState(Date.now());
  const seconds = Math.max(0, Math.ceil((until - now) / 1000));
  useEffect(() => {
    if (until <= Date.now()) return;
    const timer = window.setInterval(() => {
      const time = Date.now();
      setNow(time);
      if (time >= until) clearInterval(timer);
    }, 250);
    return () => clearInterval(timer);
  }, [until]);
  function hold(error: unknown) {
    if (error instanceof ApiError && error.status === 429) {
      const time = Date.now(); setNow(time); setUntil(time + (error.retryAfterSeconds ?? 60) * 1000);
    }
  }
  return { seconds, hold };
}
