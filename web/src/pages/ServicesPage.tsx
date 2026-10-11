import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { api, errorMessage, type ServiceUnit } from '../api';
import { usePermissions } from '../auth';
import { Badge, Button, ErrorNote, Input, PageHeader, Select, Spinner, TableWrap } from '../components/ui';
import { formatBytes, formatRelative } from '../format';

type Filter = 'all' | 'running' | 'failed' | 'enabled';

export function ServicesPage() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const [search, setSearch] = useState('');
    const [filter, setFilter] = useState<Filter>('running');
    const services = useQuery({ queryKey: ['services'], queryFn: () => api.get<ServiceUnit[]>('/api/services'), refetchInterval: 15_000 });
    const control = useMutation({
        mutationFn: ({ name, action }: { name: string; action: string }) => api.post<ServiceUnit>(`/api/services/${encodeURIComponent(name)}/${action}`),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['services'] }),
    });

    const visible = useMemo(() => {
        const term = search.trim().toLowerCase();

        return (services.data ?? []).filter((s) => {
            const matches = !term || s.name.toLowerCase().includes(term) || s.description.toLowerCase().includes(term);
            const state =
                filter === 'all' ||
                (filter === 'running' && s.activeState === 'active') ||
                (filter === 'failed' && s.activeState === 'failed') ||
                (filter === 'enabled' && s.unitFileState === 'enabled');

            return matches && state;
        });
    }, [services.data, search, filter]);

    const failedCount = services.data?.filter((s) => s.activeState === 'failed').length ?? 0;

    return (
        <div className="space-y-6">
            <PageHeader title="Services" description="systemd services on this server. Managed apps will appear here as tm-*.service." />
            <div className="flex flex-wrap gap-3">
                <Input placeholder="Search services" value={search} onChange={(e) => setSearch(e.target.value)} className="max-w-xs" />
                <Select value={filter} onChange={(e) => setFilter(e.target.value as Filter)} className="w-auto">
                    <option value="running">Running</option>
                    <option value="failed">Failed{failedCount ? ` (${failedCount})` : ''}</option>
                    <option value="enabled">Enabled at boot</option>
                    <option value="all">All</option>
                </Select>
            </div>
            <ErrorNote message={control.error ? errorMessage(control.error) : services.error ? errorMessage(services.error) : null} />
            {services.isPending ? (
                <Spinner />
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Service</th>
                            <th>State</th>
                            <th>Boot</th>
                            <th className="text-right">Memory</th>
                            <th>Since</th>
                            <th />
                        </tr>
                    </thead>
                    <tbody>
                        {visible.map((s) => (
                            <tr key={s.name}>
                                <td>
                                    <p className="font-medium">{s.name}</p>
                                    <p className="text-xs text-slate-500">{s.description}</p>
                                </td>
                                <td>
                                    <StateBadge unit={s} />
                                    {s.restarts > 0 && <span className="ml-2 text-xs text-slate-500">{s.restarts} restarts</span>}
                                </td>
                                <td className="text-slate-600 dark:text-slate-400">{s.unitFileState}</td>
                                <td className="text-right tabular-nums">{s.memoryBytes >= 0 ? formatBytes(s.memoryBytes) : '—'}</td>
                                <td className="text-slate-600 dark:text-slate-400">{s.activeSinceUnixMs ? formatRelative(s.activeSinceUnixMs) : '—'}</td>
                                <td>
                                    <div className="flex justify-end gap-1">
                                        <Link to={`/logs?unit=${encodeURIComponent(s.name)}`} className="rounded-lg px-2.5 py-1 text-xs font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                                            Logs
                                        </Link>
                                        {can('services.control') &&
                                            (s.activeState === 'active' ? (
                                                <>
                                                    <ActionButton unit={s} action="restart" label="Restart" run={control.mutate} busy={control.isPending} />
                                                    <ActionButton unit={s} action="stop" label="Stop" run={control.mutate} busy={control.isPending} />
                                                </>
                                            ) : (
                                                <ActionButton unit={s} action="start" label="Start" run={control.mutate} busy={control.isPending} />
                                            ))}
                                    </div>
                                </td>
                            </tr>
                        ))}
                        {visible.length === 0 && (
                            <tr>
                                <td colSpan={6} className="py-8 text-center text-slate-500">
                                    No services match.
                                </td>
                            </tr>
                        )}
                    </tbody>
                </TableWrap>
            )}
        </div>
    );
}

function StateBadge({ unit }: { unit: ServiceUnit }) {
    if (unit.activeState === 'active') return <Badge tone="good">● {unit.subState}</Badge>;
    if (unit.activeState === 'failed') return <Badge tone="danger">✕ failed</Badge>;
    if (unit.activeState === 'activating' || unit.activeState === 'reloading') return <Badge tone="info">{unit.activeState}</Badge>;

    return <Badge>{unit.activeState}</Badge>;
}

function ActionButton({ unit, action, label, run, busy }: { unit: ServiceUnit; action: string; label: string; run: (v: { name: string; action: string }) => void; busy: boolean }) {
    return (
        <Button
            size="sm"
            variant="ghost"
            disabled={busy}
            onClick={() => {
                if (action !== 'stop' || confirm(`Stop ${unit.name}?`)) run({ name: unit.name, action });
            }}
        >
            {label}
        </Button>
    );
}
