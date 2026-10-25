import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api, errorMessage } from '../api';
import { usePermissions } from '../auth';
import { Badge, Button, Card, ErrorNote, Field, Notice, SelectField, Spinner } from '../components/ui';
import { formatRelative } from '../format';

export type AlertEventView = {
    id: number;
    key: string;
    severity: 'Warning' | 'Critical';
    title: string;
    detail: string | null;
    firedAt: string;
    resolvedAt: string | null;
    oneOff: boolean;
};

type ChannelView = { id: number; name: string; kind: 'Discord' | 'Webhook' | 'Email'; target: string; enabled: boolean; criticalOnly: boolean; lastError: string | null };

type AlertSettingsView = {
    diskPercent: number;
    memoryPercent: number;
    certificateDays: number;
    securityUpdates: boolean;
    backupDays: number;
    smtpHost: string | null;
    smtpPort: number;
    smtpUser: string | null;
    smtpPasswordSet: boolean;
    smtpFrom: string | null;
    channels: ChannelView[];
};

/** What the panel watches and who it tells, a tab of Updates & alerts. */
export function AlertsTab() {
    const can = usePermissions();
    const alerts = useQuery({ queryKey: ['alerts'], queryFn: () => api.get<{ open: AlertEventView[]; recent: AlertEventView[] }>('/api/alerts'), refetchInterval: 30_000 });

    return (
        <div className="space-y-6">
            <p className="text-sm text-muted">The panel checks the server every minute and tells you when something needs you: once when it starts, once when it's fixed.</p>
            <ErrorNote message={alerts.error ? errorMessage(alerts.error) : null} />
            {alerts.isPending ? (
                <Spinner />
            ) : alerts.data!.open.length === 0 ? (
                <Notice tone="success">Nothing needs your attention.</Notice>
            ) : (
                <div className="space-y-2">
                    {alerts.data!.open.map((a) => (
                        <AlertRow key={a.id} alert={a} />
                    ))}
                </div>
            )}
            {can('alerts.manage') && <AlertSettingsCard />}
            {(alerts.data?.recent.length ?? 0) > 0 && (
                <Card className="space-y-2 p-5">
                    <h2 className="font-medium">History</h2>
                    <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                        {alerts.data!.recent.map((a) => (
                            <li key={a.id} className="py-2">
                                <span className={a.severity === 'Critical' ? 'text-bad' : 'text-warn'}>●</span> {a.title}
                                <span className="text-xs text-muted">
                                    {' '}
                                    · {formatRelative(a.firedAt)}
                                    {!a.oneOff && a.resolvedAt && ` · fixed ${formatRelative(a.resolvedAt)}`}
                                </span>
                            </li>
                        ))}
                    </ul>
                </Card>
            )}
        </div>
    );
}

export function AlertRow({ alert }: { alert: AlertEventView }) {
    return (
        <Notice tone={alert.severity === 'Critical' ? 'danger' : 'warning'}>
            <span className="font-medium">{alert.title}</span> <span className="text-xs opacity-75">since {formatRelative(alert.firedAt)}</span>
            {alert.detail && <span className="mt-1 block whitespace-pre-wrap text-xs">{alert.detail}</span>}
        </Notice>
    );
}

function AlertSettingsCard() {
    const queryClient = useQueryClient();
    const settings = useQuery({ queryKey: ['alert-settings'], queryFn: () => api.get<AlertSettingsView>('/api/alerts/settings') });
    const [form, setForm] = useState<(Omit<AlertSettingsView, 'channels' | 'smtpPasswordSet'> & { smtpPassword: string }) | null>(null);
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['alert-settings'] });

    useEffect(() => {
        if (settings.data && !form) {
            const { channels: _channels, smtpPasswordSet: _set, ...rest } = settings.data;
            setForm({ ...rest, smtpPassword: '' });
        }
    }, [settings.data, form]);

    const save = useMutation({ mutationFn: () => api.put('/api/alerts/settings', form), onSuccess: refresh });
    const check = useMutation({ mutationFn: () => api.post('/api/alerts/check'), onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['alerts'] }) });

    if (!settings.data || !form) return settings.error ? <ErrorNote message={errorMessage(settings.error)} /> : <Spinner />;

    const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm({ ...form, [key]: value });

    return (
        <>
            <Card className="space-y-4 p-5">
                <div className="flex items-center justify-between">
                    <h2 className="font-medium">Where alerts go</h2>
                    <Button size="sm" variant="ghost" onClick={() => check.mutate()} disabled={check.isPending}>
                        Check now
                    </Button>
                </div>
                {settings.data.channels.length === 0 && <Notice tone="warning">No channels yet: alerts only show here and on the dashboard.</Notice>}
                <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                    {settings.data.channels.map((c) => (
                        <ChannelRow key={c.id} channel={c} onChanged={refresh} />
                    ))}
                </ul>
                <NewChannel onAdded={refresh} />
            </Card>

            <Card className="space-y-4 p-5">
                <h2 className="font-medium">When to alert</h2>
                <form
                    className="space-y-4"
                    onSubmit={(e) => {
                        e.preventDefault();
                        save.mutate();
                    }}
                >
                    <div className="grid gap-3 sm:grid-cols-4">
                        <Field label="Disk fuller than (%)" type="number" min={50} max={99} value={form.diskPercent} onChange={(e) => set('diskPercent', Number(e.target.value))} />
                        <Field label="Memory used over (%)" type="number" min={50} max={100} value={form.memoryPercent} onChange={(e) => set('memoryPercent', Number(e.target.value))} />
                        <Field label="Certificate expiring within (days)" type="number" min={1} max={60} value={form.certificateDays} onChange={(e) => set('certificateDays', Number(e.target.value))} />
                        <Field label="No successful backup for (days)" type="number" min={1} max={60} value={form.backupDays} onChange={(e) => set('backupDays', Number(e.target.value))} />
                    </div>
                    <label className="flex items-center gap-2 text-sm">
                        <input type="checkbox" className="size-4 accent-brand-600" checked={form.securityUpdates} onChange={(e) => set('securityUpdates', e.target.checked)} />
                        Security updates waiting
                    </label>
                    <p className="text-sm text-muted">Always: failed or crash-looping services, failed deploys, hotel installs, backups and certificates.</p>
                    <details className="text-sm">
                        <summary className="cursor-pointer font-medium">Mail server (for email alerts)</summary>
                        <div className="mt-3 grid gap-3 sm:grid-cols-2">
                            <Field label="SMTP host" value={form.smtpHost ?? ''} onChange={(e) => set('smtpHost', e.target.value)} placeholder="smtp.example.com" />
                            <Field label="Port" type="number" value={form.smtpPort} onChange={(e) => set('smtpPort', Number(e.target.value))} />
                            <Field label="User" value={form.smtpUser ?? ''} onChange={(e) => set('smtpUser', e.target.value)} />
                            <Field
                                label="Password"
                                type="password"
                                value={form.smtpPassword}
                                onChange={(e) => set('smtpPassword', e.target.value)}
                                placeholder={settings.data.smtpPasswordSet ? '(unchanged)' : ''}
                            />
                            <Field label="From address" value={form.smtpFrom ?? ''} onChange={(e) => set('smtpFrom', e.target.value)} placeholder="panel@example.com" />
                        </div>
                    </details>
                    <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                    <Button type="submit" disabled={save.isPending}>
                        {save.isSuccess ? '✓ Saved' : 'Save'}
                    </Button>
                </form>
            </Card>
        </>
    );
}

function ChannelRow({ channel, onChanged }: { channel: ChannelView; onChanged: () => void }) {
    const test = useMutation({ mutationFn: () => api.post(`/api/alerts/channels/${channel.id}/test`), onSettled: onChanged });
    const remove = useMutation({ mutationFn: () => api.delete(`/api/alerts/channels/${channel.id}`), onSuccess: onChanged });
    const toggle = useMutation({
        mutationFn: () => api.put(`/api/alerts/channels/${channel.id}`, { name: channel.name, kind: channel.kind, enabled: !channel.enabled, criticalOnly: channel.criticalOnly }),
        onSuccess: onChanged,
    });

    return (
        <li className="flex items-center justify-between gap-3 py-2">
            <span>
                <span className="font-medium">{channel.name}</span> <Badge>{channel.kind}</Badge> {channel.criticalOnly && <Badge tone="danger">critical only</Badge>}{' '}
                {!channel.enabled && <Badge>paused</Badge>}
                <span className="block text-xs text-muted">{channel.target}</span>
                {channel.lastError && <span className="block text-xs text-bad">Last send failed: {channel.lastError}</span>}
                {test.isSuccess && <span className="block text-xs text-good">Test sent.</span>}
            </span>
            <span className="whitespace-nowrap">
                <Button size="sm" variant="ghost" onClick={() => test.mutate()} disabled={test.isPending}>
                    Test
                </Button>
                <Button size="sm" variant="ghost" onClick={() => toggle.mutate()}>
                    {channel.enabled ? 'Pause' : 'Resume'}
                </Button>
                <Button size="sm" variant="ghost" onClick={() => confirm(`Remove ${channel.name}?`) && remove.mutate()}>
                    Remove
                </Button>
            </span>
        </li>
    );
}

function NewChannel({ onAdded }: { onAdded: () => void }) {
    const [kind, setKind] = useState<ChannelView['kind']>('Discord');
    const [name, setName] = useState('');
    const [target, setTarget] = useState('');
    const [criticalOnly, setCriticalOnly] = useState(false);
    const add = useMutation({
        mutationFn: () => api.post('/api/alerts/channels', { name, kind, target, criticalOnly }),
        onSuccess: () => {
            setName('');
            setTarget('');
            onAdded();
        },
    });

    return (
        <form
            className="grid gap-3 border-t border-slate-100 pt-4 sm:grid-cols-[8rem_1fr_2fr_auto] sm:items-end dark:border-slate-800"
            onSubmit={(e) => {
                e.preventDefault();
                add.mutate();
            }}
        >
            <SelectField label="Kind" value={kind} onChange={(e) => setKind(e.target.value as ChannelView['kind'])}>
                <option value="Discord">Discord</option>
                <option value="Webhook">Webhook</option>
                <option value="Email">Email</option>
            </SelectField>
            <Field label="Name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Staff Discord" required />
            <Field
                label={kind === 'Email' ? 'Address' : 'URL'}
                value={target}
                onChange={(e) => setTarget(e.target.value)}
                placeholder={kind === 'Discord' ? 'https://discord.com/api/webhooks/…' : kind === 'Email' ? 'you@example.com' : 'https://…'}
                required
            />
            <Button type="submit" disabled={add.isPending}>
                Add
            </Button>
            <label className="flex items-center gap-2 text-sm sm:col-span-4">
                <input type="checkbox" className="size-4 accent-brand-600" checked={criticalOnly} onChange={(e) => setCriticalOnly(e.target.checked)} />
                Only failures (not warnings like a filling disk)
            </label>
            {add.error && (
                <div className="sm:col-span-4">
                    <ErrorNote message={errorMessage(add.error)} />
                </div>
            )}
        </form>
    );
}
