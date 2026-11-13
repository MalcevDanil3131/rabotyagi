import type { OrderListQuery, OrderPage } from './api';

export interface OrderListState {
  data: OrderPage | null;
  loading: boolean;
  error: unknown;
}

// Only the latest request can publish state, including errors and completion.
// A failed refresh retains the last successful page; a different query clears it.
export function createOrderListLoader(fetchPage: (query: OrderListQuery) => Promise<OrderPage>) {
  let state: OrderListState = { data: null, loading: false, error: null };
  let key = '';
  let generation = 0;
  const listeners = new Set<() => void>();
  const publish = (next: OrderListState) => { state = next; for (const listener of listeners) listener(); };
  return {
    subscribe: (listener: () => void) => { listeners.add(listener); return () => { listeners.delete(listener); }; },
    getSnapshot: () => state,
    cancel: () => { generation++; key = ''; },
    load: async (query: OrderListQuery, background = false): Promise<boolean> => {
      if (background && state.loading) return false;
      const nextKey = JSON.stringify(query);
      const data = key === nextKey ? state.data : null;
      key = nextKey;
      const version = ++generation;
      publish({ data, loading: true, error: null });
      try {
        const page = await fetchPage(query);
        if (version !== generation) return false;
        // Remember the actual page if the server corrected a now empty last page.
        key = JSON.stringify({ ...query, page: page.page });
        publish({ data: page, loading: false, error: null });
        return true;
      } catch (error) {
        if (version !== generation) return false;
        publish({ data, loading: false, error });
        return false;
      }
    },
  };
}
