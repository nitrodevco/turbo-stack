import { useQuery } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, errorMessage, type JournalEntry, type ServiceUnit } from '../api';
import { Button, ErrorNote, Input, PageHeader, Select } from '../components/ui';
import { useStickToBottom } from '../components/useStickToBottom';

const priorities = [
    { value: '', label: 'All levels' },
    { value: '3', label: 'Errors and worse' },
    { value: '4', label: 'Warnings and worse' },
    { value: '6', label: 'Info and worse' },
];

const maxLiveEntries = 2000;

export function LogsPage() {
    const [params, setParams] = useSearchParams();
    const unit = params.get('unit') ?? '';
    const priority = params.get('priority') ?? '';
    const [grep, setGrep] = useState(params.get('grep') ?? '');
    const [live, setLive] = useState(false);
    const [liveEntries, setLiveEntries] = useState<JournalEntry[]>([]);
    const [liveError, setLiveError] = useState<string | null>(null);

    const services = useQuery({ queryKey: ['services'], queryFn: () => api.get<ServiceUnit[]>('/api/services') });
    const query = new URLSearchParams({ ...(unit && { unit }), ...(priority && { priority }), ...(params.get('grep') && { grep: params.get('grep')! }) }).toString();
    const entries = useQuery({
        queryKey: ['logs', query],
        queryFn: () => api.get<JournalEntry[]>(`/api/logs?${query}&limit=500`),
        enabled: !live,
    });

    useEffect(() => {
        if (!live) return;

        setLiveEntries([]);
        setLiveError(null);
        const source = new EventSource(`/api/logs/stream?${query}`);
        source.addEventListener('entry', (event) => {
            const entry = JSON.parse((event as MessageEvent).data) as JournalEntry;
            setLiveEntries((current) => [...current.slice(-maxLiveEntries + 1), entry]);
        });
        source.onerror = () => setLiveError('The live stream was interrupted. It reconnects automatically.');

        return () => source.close();
    }, [live, query]);

    const box = useStickToBottom<HTMLDivElement>([live, liveEntries.length]);

    function update(key: string, value: string) {
        const next = new URLSearchParams(params);

        if (value) next.set(key, value);
        else next.delete(key);

        setParams(next, { replace: true });
    }

    const rows = live ? liveEntries : (entries.data ?? []);

    return (
        <div className="space-y-6">
            <PageHeader
                title="Logs"
                description="The systemd journal: every service's output in one place."
                actions={
                    <Button variant={live ? 'danger' : 'primary'} onClick={() => setLive(!live)}>
                        {live ? '■ Stop live tail' : '▶ Live tail'}
                    </Button>
                }
            />
            <form
                className="flex flex-wrap gap-3"
                onSubmit={(e) => {
                    e.preventDefault();
                    update('grep', grep.trim());
                }}
            >
                <Select value={unit} onChange={(e) => update('unit', e.target.value)} className="w-auto max-w-xs">
                    <option value="">All services</option>
                    {(services.data ?? []).map((s) => (
                        <option key={s.name} value={s.name}>
                            {s.name}
                        </option>
                    ))}
                </Select>
                <Select value={priority} onChange={(e) => update('priority', e.target.value)} className="w-auto">
                    {priorities.map((p) => (
                        <option key={p.value} value={p.value}>
                            {p.label}
                        </option>
                    ))}
                </Select>
                <Input placeholder="Search (regex)" value={grep} onChange={(e) => setGrep(e.target.value)} className="max-w-xs" />
                <Button type="submit" variant="secondary">
                    Search
                </Button>
                {!live && (
                    <Button type="button" variant="ghost" onClick={() => void entries.refetch()}>
                        Refresh
                    </Button>
                )}
            </form>
            <ErrorNote message={entries.error ? errorMessage(entries.error) : liveError} />
            <div ref={box} className="max-h-[70vh] overflow-auto rounded-xl border border-slate-200 bg-slate-950 p-4 font-mono text-xs leading-relaxed text-slate-200 dark:border-slate-800">
                {rows.length === 0 && <p className="text-muted">{entries.isFetching ? 'Loading…' : live ? 'Waiting for new entries…' : 'No entries.'}</p>}
                {rows.map((entry) => (
                    <div key={entry.cursor || `${entry.timestampUsec}-${entry.message}`} className="flex gap-3 whitespace-pre-wrap">
                        <span className="shrink-0 text-muted tabular-nums">{new Date(entry.timestampUsec / 1000).toLocaleString()}</span>
                        <span className="w-28 shrink-0 truncate text-sky-300">{entry.identifier || entry.unit}</span>
                        <span className={priorityClass(entry.priority)}>{entry.message}</span>
                    </div>
                ))}
            </div>
        </div>
    );
}

function priorityClass(priority: number) {
    if (priority <= 3) return 'text-red-300';
    if (priority === 4) return 'text-amber-200';

    return '';
}
