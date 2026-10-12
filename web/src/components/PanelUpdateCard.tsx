import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api, errorMessage, type PanelUpdate } from '../api';
import { usePermissions } from '../auth';
import { formatDateTime, formatRelative } from '../format';
import { useSudo } from './Sudo';
import { Badge, Button, Card, ErrorNote, Notice } from './ui';

/** The panel's own version: release channel, what's new, one-click update (rolled back automatically if it fails). */
export function PanelUpdateCard() {
    const can = usePermissions();
    const withSudo = useSudo();
    const queryClient = useQueryClient();
    // While an update runs the panel restarts: keep asking until it answers with the outcome.
    const [updatingTo, setUpdatingTo] = useState<string | null>(null);
    const state = useQuery({
        queryKey: ['panel-update'],
        queryFn: () => api.get<PanelUpdate>('/api/panel-update'),
        refetchInterval: updatingTo ? 3000 : false,
        retry: updatingTo ? true : 1,
    });
    const onData = (data: PanelUpdate) => queryClient.setQueryData(['panel-update'], (old: PanelUpdate | undefined) => (old ? { ...old, ...data } : old));

    const check = useMutation({
        mutationFn: () => api.post<Partial<PanelUpdate>>('/api/panel-update/check'),
        onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['panel-update'] }),
    });
    const channel = useMutation({
        mutationFn: (value: string) => api.put<Partial<PanelUpdate>>('/api/panel-update/channel', { channel: value }),
        onSuccess: (data) => onData(data as PanelUpdate),
    });
    const apply = useMutation({
        mutationFn: (version: string) => withSudo(() => api.post('/api/panel-update/apply', { version })),
        onSuccess: (_, version) => setUpdatingTo(version),
    });

    const s = state.data;
    const finished = updatingTo && s && !s.running && (s.currentVersion === updatingTo || (s.last?.to === updatingTo && s.last.state !== 'running'));

    useEffect(() => {
        if (finished) {
            setUpdatingTo(null);
            // New panel version: the rest of the UI may have changed too.
            void queryClient.invalidateQueries();
        }
    }, [finished, queryClient]);

    if (!s) return <ErrorNote message={state.error && !updatingTo ? errorMessage(state.error) : null} />;

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-start justify-between gap-3">
                <div className="space-y-1">
                    <h2 className="font-medium">Turbo Manager {s.currentVersion}</h2>
                    <p className="text-sm text-slate-600 dark:text-slate-400">
                        {s.checkedAt ? `Checked ${formatRelative(s.checkedAt)}` : 'Not checked yet'} · releases are verified against the signing key from install time.
                    </p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                    {can('panel.update') ? (
                        <select
                            aria-label="Release channel"
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm dark:border-slate-700 dark:bg-slate-950"
                            value={s.channel}
                            disabled={channel.isPending || !!updatingTo}
                            onChange={(e) => channel.mutate(e.target.value)}
                        >
                            <option value="stable">Stable</option>
                            <option value="beta">Beta</option>
                        </select>
                    ) : (
                        <Badge>{s.channel}</Badge>
                    )}
                    {can('panel.update') && (
                        <Button variant="secondary" disabled={check.isPending || !!updatingTo} onClick={() => check.mutate()}>
                            {check.isPending ? 'Checking…' : 'Check now'}
                        </Button>
                    )}
                </div>
            </div>

            {updatingTo ? (
                <Notice tone="info">
                    Updating to {updatingTo}. The panel restarts on the way; this page picks it up again by itself. If the new version doesn't start, the
                    previous one is put back automatically.
                </Notice>
            ) : s.available && s.latestVersion ? (
                <div className="space-y-3 rounded-lg border border-slate-200 p-4 dark:border-slate-800">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                        <p className="font-medium">
                            {s.latestVersion} is available{s.published && <span className="font-normal text-slate-500"> · released {formatDateTime(s.published)}</span>}
                        </p>
                        {can('panel.update') && (
                            <Button disabled={apply.isPending} onClick={() => confirm(`Update the panel to ${s.latestVersion}? It restarts (about a minute); apps keep running.`) && apply.mutate(s.latestVersion!)}>
                                {apply.isPending ? 'Starting…' : `Update to ${s.latestVersion}`}
                            </Button>
                        )}
                    </div>
                    {s.notes && <pre className="max-h-64 overflow-auto text-sm whitespace-pre-wrap text-slate-700 dark:text-slate-300">{s.notes}</pre>}
                </div>
            ) : (
                !s.error && s.checkedAt && <p className="text-sm text-slate-600 dark:text-slate-400">✓ Up to date on the {s.channel} channel.</p>
            )}

            {s.error && <Notice tone="warning">{s.error}</Notice>}
            {s.last && !updatingTo && <LastUpdate last={s.last} />}
            <ErrorNote message={[check.error, channel.error, apply.error].filter(Boolean).map(errorMessage)[0] ?? null} />
        </Card>
    );
}

function LastUpdate({ last }: { last: NonNullable<PanelUpdate['last']> }) {
    if (last.state === 'done') {
        return (
            <p className="text-sm text-slate-600 dark:text-slate-400">
                Last update: {last.from} → {last.to}, {formatRelative(last.at)}.
            </p>
        );
    }

    return (
        <Notice tone="danger">
            <p className="font-medium">
                The update from {last.from} to {last.to} {last.state === 'rolled-back' ? 'was rolled back' : 'failed'} ({formatRelative(last.at)}).
            </p>
            <pre className="mt-2 max-h-48 overflow-auto text-xs whitespace-pre-wrap">{last.message}</pre>
        </Notice>
    );
}
