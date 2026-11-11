import { useCallback, useEffect, useRef, useState } from 'react';

// One request at a time; abort obsolete requests on navigation, identity changes, or refresh.
export function usePolling<T>(load: (signal: AbortSignal) => Promise<T>, interval: number, enabled = true) {
  const [revision, setRevision] = useState(0);
  const generation = useRef(0);
  const [state, setState] = useState<{ load: typeof load; data: T | null; error: string }>({ load, data: null, error: '' });
  useEffect(() => {
    if (!enabled) return;
    const controller = new AbortController();
    const version = ++generation.current;
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const data = await load(controller.signal);
        if (!controller.signal.aborted && generation.current === version) setState({ load, data, error: '' });
      } catch (error) {
        if (!controller.signal.aborted && generation.current === version) setState(previous => ({ load,
          data: previous.load === load ? previous.data : null,
          error: error instanceof Error ? error.message : 'Something went wrong. Please try again.' }));
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(poll, interval);
      }
    }
    void poll();
    return () => { controller.abort(); clearTimeout(timer); };
  }, [load, interval, enabled, revision]);
  const refresh = useCallback(() => { generation.current++; setRevision(value => value + 1); }, []);
  function replaceData(data: T) { setState({ load, data, error: '' }); refresh(); }
  const current = enabled && state.load === load;
  return { data: current ? state.data : null, error: current ? state.error : '',
    loading: enabled && (!current || (!state.data && !state.error)), refresh, replaceData };
}
