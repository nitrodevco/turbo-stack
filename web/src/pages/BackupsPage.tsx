import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import {
    api,
    errorMessage,
    type BackupManifest,
    type BackupOverview,
    type BackupPlan,
    type BackupSnapshot,
    type BackupTarget,
    type BackupTargetKind,
    type DatabaseEngineView,
    type DatabaseInfo,
} from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { useSudo } from '../components/Sudo';
import { Badge, Button, Card, ErrorNote, Field, Modal, Notice, PageHeader, SelectField, Spinner, TableWrap } from '../components/ui';
import { formatDateTime, formatRelative } from '../format';

export function BackupsPage() {
    const overview = useQuery({ queryKey: ['backups'], queryFn: () => api.get<BackupOverview>('/api/backups') });
    const [jobId, setJobId] = useState<number | null>(null);
    const queryClient = useQueryClient();

    return (
        <div className="space-y-6">
            <PageHeader
                title="Backups"
                description="Encrypted, deduplicated snapshots (restic) of databases, app folders and the panel itself — on another disk, S3-compatible storage or an SFTP server."
            />
            <ErrorNote message={overview.error ? errorMessage(overview.error) : null} />
            {jobId && (
                <Card className="space-y-3 p-5">
                    <div className="flex items-center justify-between">
                        <h2 className="font-medium">Backup job</h2>
                        <Button size="sm" variant="ghost" onClick={() => setJobId(null)}>
                            Close
                        </Button>
                    </div>
                    <JobOutput jobId={jobId} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['backups'] })} />
                </Card>
            )}
            {overview.isPending ? (
                <Spinner />
            ) : (
                overview.data && (
                    <>
                        <TargetsCard targets={overview.data.targets} plans={overview.data.plans} />
                        <PlansCard targets={overview.data.targets} plans={overview.data.plans} onJob={setJobId} />
                        {overview.data.targets.length > 0 && <SnapshotsCard targets={overview.data.targets} plans={overview.data.plans} onJob={setJobId} />}
                    </>
                )
            )}
        </div>
    );
}

const kindLabels: Record<BackupTargetKind, string> = { Local: 'Local folder', S3: 'S3-compatible', Sftp: 'SFTP server' };

function location(target: BackupTarget) {
    if (target.kind === 'S3') return `${target.endpoint || 's3.amazonaws.com'} · ${target.path}`;
    if (target.kind === 'Sftp') return `${target.user}@${target.endpoint}:${target.port} · ${target.path}`;
    return target.path;
}

function TargetsCard({ targets, plans }: { targets: BackupTarget[]; plans: BackupPlan[] }) {
    const can = usePermissions();
    const withSudo = useSudo();
    const queryClient = useQueryClient();
    const [adding, setAdding] = useState(false);
    const [shownPassword, setShownPassword] = useState<{ name: string; password: string } | null>(null);
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['backups'] });

    const check = useMutation({ mutationFn: (id: number) => api.post<BackupTarget>(`/api/backups/targets/${id}/check`), onSuccess: refresh });
    const reveal = useMutation({
        mutationFn: (target: BackupTarget) => withSudo(() => api.get<{ password: string }>(`/api/backups/targets/${target.id}/password`)),
        onSuccess: (data, target) => setShownPassword({ name: target.name, password: data.password }),
    });
    const remove = useMutation({ mutationFn: (id: number) => withSudo(() => api.delete(`/api/backups/targets/${id}`)), onSuccess: refresh });

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    <h2 className="font-medium">Targets</h2>
                    <p className="text-sm text-slate-600 dark:text-slate-400">Where snapshots are stored. Each target is a repository with its own encryption password.</p>
                </div>
                {can('backups.manage') && <Button onClick={() => setAdding(true)}>Add target</Button>}
            </div>
            {shownPassword && (
                <Notice tone="warning">
                    <p>
                        Repository password for <span className="font-medium">{shownPassword.name}</span>:{' '}
                        <code className="select-all break-all">{shownPassword.password}</code>
                    </p>
                    <p className="mt-1">
                        Keep it somewhere other than this server (a password manager). Without it the snapshots can't be read — for example on a new server after
                        this one is lost.
                    </p>
                    <Button size="sm" variant="secondary" className="mt-2" onClick={() => setShownPassword(null)}>
                        I've saved it
                    </Button>
                </Notice>
            )}
            {targets.length === 0 ? (
                <p className="text-sm text-slate-500">No targets yet. Add one to start backing up.</p>
            ) : (
                <ul className="divide-y divide-slate-100 dark:divide-slate-800">
                    {targets.map((target) => (
                        <li key={target.id} className="space-y-3 py-3">
                            <div className="flex flex-wrap items-center justify-between gap-2">
                                <div className="min-w-0">
                                    <p className="font-medium">
                                        {target.name} <Badge>{kindLabels[target.kind]}</Badge>{' '}
                                        {target.ready ? <Badge tone="good">✓ Ready</Badge> : target.lastError ? <Badge tone="danger">✕ Unreachable</Badge> : <Badge tone="warning">Needs setup</Badge>}
                                    </p>
                                    <p className="truncate font-mono text-xs text-slate-500">{location(target)}</p>
                                    {target.checkedAt && <p className="text-xs text-slate-500">Checked {formatRelative(target.checkedAt)}</p>}
                                </div>
                                {can('backups.manage') && (
                                    <div className="flex flex-wrap gap-2">
                                        <Button size="sm" variant="secondary" disabled={check.isPending} onClick={() => check.mutate(target.id)}>
                                            Check
                                        </Button>
                                        <Button size="sm" variant="secondary" disabled={reveal.isPending} onClick={() => reveal.mutate(target)}>
                                            Show password
                                        </Button>
                                        <Button
                                            size="sm"
                                            variant="ghost"
                                            disabled={remove.isPending || plans.some((p) => p.targetId === target.id)}
                                            title={plans.some((p) => p.targetId === target.id) ? 'Plans still use this target' : undefined}
                                            onClick={() => confirm(`Remove ${target.name}? Its snapshots stay where they are; the panel just forgets the target.`) && remove.mutate(target.id)}
                                        >
                                            Remove
                                        </Button>
                                    </div>
                                )}
                            </div>
                            {target.lastError && <Notice tone="danger">{target.lastError}</Notice>}
                            {target.kind === 'Sftp' && !target.hostKey && can('backups.manage') && <SftpSetup target={target} />}
                        </li>
                    ))}
                </ul>
            )}
            <ErrorNote message={[check.error, reveal.error, remove.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {adding && (
                <AddTargetModal
                    onClose={() => setAdding(false)}
                    onCreated={(target, password) => {
                        setAdding(false);
                        if (password) setShownPassword({ name: target.name, password });
                        refresh();
                    }}
                />
            )}
        </Card>
    );
}

/** SFTP: put this target's key on the server, then compare and pin the server's host key. */
function SftpSetup({ target }: { target: BackupTarget }) {
    const queryClient = useQueryClient();
    const scan = useMutation({ mutationFn: () => api.get<{ hostKey: string; fingerprint: string }>(`/api/backups/targets/${target.id}/host-key`) });
    const trust = useMutation({
        mutationFn: (hostKey: string) => api.post<BackupTarget>(`/api/backups/targets/${target.id}/host-key`, { hostKey }),
        onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['backups'] }),
    });

    return (
        <div className="space-y-3 rounded-lg border border-slate-200 p-4 text-sm dark:border-slate-800">
            <p>
                1. Add this key to <code>~/.ssh/authorized_keys</code> of <code>{target.user}</code> on {target.endpoint}:
            </p>
            <pre className="overflow-x-auto rounded bg-slate-100 p-2 font-mono text-xs select-all dark:bg-slate-900">{target.publicKey}</pre>
            <p>2. Check the server's identity before trusting it.</p>
            {scan.data ? (
                <div className="space-y-2">
                    <p>
                        The server says its key is <code className="break-all">{scan.data.fingerprint}</code>. Compare it with what your provider shows (or{' '}
                        <code>ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub</code> on the server).
                    </p>
                    <Button size="sm" disabled={trust.isPending} onClick={() => trust.mutate(scan.data.hostKey)}>
                        {trust.isPending ? 'Connecting…' : 'It matches — connect'}
                    </Button>
                </div>
            ) : (
                <Button size="sm" variant="secondary" disabled={scan.isPending} onClick={() => scan.mutate()}>
                    {scan.isPending ? 'Asking the server…' : 'Get the server’s fingerprint'}
                </Button>
            )}
            <ErrorNote message={[scan.error, trust.error].filter(Boolean).map(errorMessage)[0] ?? null} />
        </div>
    );
}

function AddTargetModal({ onClose, onCreated }: { onClose: () => void; onCreated: (target: BackupTarget, password: string | null) => void }) {
    const [form, setForm] = useState({
        name: '',
        kind: 'Local' as BackupTargetKind,
        path: '/var/backups/turbo-manager',
        endpoint: '',
        port: 22,
        user: '',
        accessKeyId: '',
        secretAccessKey: '',
        region: '',
        password: '',
    });
    const set = (patch: Partial<typeof form>) => setForm((f) => ({ ...f, ...patch }));
    const create = useMutation({
        mutationFn: () => api.post<{ target: BackupTarget; password: string | null }>('/api/backups/targets', { ...form, password: form.password || null }),
        onSuccess: (data) => onCreated(data.target, data.password),
    });

    function submit(e: FormEvent) {
        e.preventDefault();
        create.mutate();
    }

    return (
        <Modal open title="Add a backup target" onClose={onClose}>
            <form className="space-y-4" onSubmit={submit}>
                <Field label="Name" value={form.name} onChange={(e) => set({ name: e.target.value })} placeholder="Storage box" required />
                <SelectField
                    label="Kind"
                    value={form.kind}
                    onChange={(e) => {
                        const kind = e.target.value as BackupTargetKind;
                        set({ kind, path: kind === 'Local' ? '/var/backups/turbo-manager' : '', endpoint: kind === 'S3' ? 'https://' : '' });
                    }}
                >
                    <option value="Local">Local folder (best: a separate disk under /mnt)</option>
                    <option value="S3">S3-compatible storage (AWS, Backblaze B2, Wasabi, R2, …)</option>
                    <option value="Sftp">SFTP server (e.g. a storage box)</option>
                </SelectField>
                {form.kind === 'Local' && (
                    <Field label="Folder" value={form.path} onChange={(e) => set({ path: e.target.value })} hint="Under /var/backups, /srv, /mnt, /media or /backups." />
                )}
                {form.kind === 'S3' && (
                    <>
                        <Field label="Endpoint" value={form.endpoint} onChange={(e) => set({ endpoint: e.target.value })} placeholder="https://s3.eu-central-1.amazonaws.com" />
                        <Field label="Bucket" value={form.path} onChange={(e) => set({ path: e.target.value })} placeholder="hotel-backups or hotel-backups/server-1" />
                        <div className="grid gap-4 sm:grid-cols-2">
                            <Field label="Access key" value={form.accessKeyId} onChange={(e) => set({ accessKeyId: e.target.value })} />
                            <Field label="Secret key" type="password" value={form.secretAccessKey} onChange={(e) => set({ secretAccessKey: e.target.value })} />
                        </div>
                        <Field label="Region" value={form.region} onChange={(e) => set({ region: e.target.value })} hint="Only if your provider asks for one." />
                    </>
                )}
                {form.kind === 'Sftp' && (
                    <>
                        <div className="grid gap-4 sm:grid-cols-[1fr_6rem]">
                            <Field label="Server" value={form.endpoint} onChange={(e) => set({ endpoint: e.target.value })} placeholder="u123456.your-storagebox.de" />
                            <Field label="Port" type="number" value={form.port} onChange={(e) => set({ port: Number(e.target.value) })} />
                        </div>
                        <div className="grid gap-4 sm:grid-cols-2">
                            <Field label="User" value={form.user} onChange={(e) => set({ user: e.target.value })} />
                            <Field label="Folder on the server" value={form.path} onChange={(e) => set({ path: e.target.value })} placeholder="hotel-backups" />
                        </div>
                    </>
                )}
                <Field
                    label="Repository password"
                    type="password"
                    value={form.password}
                    onChange={(e) => set({ password: e.target.value })}
                    hint="Leave empty to generate one (shown once). To connect a repository that already exists, enter its password."
                />
                <ErrorNote message={create.error ? errorMessage(create.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={create.isPending}>
                        {create.isPending ? 'Connecting…' : 'Add target'}
                    </Button>
                </div>
            </form>
        </Modal>
    );
}

function PlansCard({ targets, plans, onJob }: { targets: BackupTarget[]; plans: BackupPlan[]; onJob: (id: number) => void }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const [editing, setEditing] = useState<BackupPlan | 'new' | null>(null);
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['backups'] });
    const run = useMutation({ mutationFn: (id: number) => api.post<{ jobId: number }>(`/api/backups/plans/${id}/run`), onSuccess: (data) => onJob(data.jobId) });
    const remove = useMutation({ mutationFn: (id: number) => api.delete(`/api/backups/plans/${id}`), onSuccess: refresh });

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    <h2 className="font-medium">Plans</h2>
                    <p className="text-sm text-slate-600 dark:text-slate-400">What to back up, every day at a set time, and how many snapshots to keep.</p>
                </div>
                {can('backups.manage') && (
                    <Button disabled={targets.length === 0} onClick={() => setEditing('new')}>
                        Add plan
                    </Button>
                )}
            </div>
            {plans.length === 0 ? (
                <p className="text-sm text-slate-500">{targets.length === 0 ? 'Add a target first.' : 'No plans yet.'}</p>
            ) : (
                <TableWrap>
                    <table className="w-full text-sm">
                        <thead className="text-left text-xs text-slate-500">
                            <tr>
                                <th className="py-2 pr-3 font-medium">Plan</th>
                                <th className="py-2 pr-3 font-medium">Contents</th>
                                <th className="py-2 pr-3 font-medium">Schedule</th>
                                <th className="py-2 pr-3 font-medium">Last run</th>
                                <th />
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                            {plans.map((plan) => (
                                <tr key={plan.id} className="align-top">
                                    <td className="py-2 pr-3">
                                        <p className="font-medium">{plan.name}</p>
                                        <p className="text-xs text-slate-500">→ {targets.find((t) => t.id === plan.targetId)?.name}</p>
                                    </td>
                                    <td className="py-2 pr-3 text-xs">
                                        {[...plan.databases, ...plan.folders, ...(plan.panelState ? ['panel state'] : [])].join(', ')}
                                    </td>
                                    <td className="py-2 pr-3 text-xs">
                                        {plan.enabled ? `Daily at ${plan.timeOfDay}` : <Badge>Paused</Badge>}
                                        <p className="text-slate-500">
                                            Keeps {plan.keepDaily} daily, {plan.keepWeekly} weekly, {plan.keepMonthly} monthly
                                        </p>
                                    </td>
                                    <td className="py-2 pr-3 text-xs">
                                        {plan.lastRunAt ? (
                                            <>
                                                <span title={formatDateTime(plan.lastRunAt)}>{formatRelative(plan.lastRunAt)}</span>{' '}
                                                {plan.lastResult === 'OK' ? <Badge tone="good">✓ OK</Badge> : <Badge tone="danger">✕ Failed</Badge>}
                                                {plan.lastResult !== 'OK' && <p className="mt-1 text-red-700 dark:text-red-300">{plan.lastResult}</p>}
                                            </>
                                        ) : (
                                            <span className="text-slate-500">Never</span>
                                        )}
                                    </td>
                                    <td className="py-2 text-right whitespace-nowrap">
                                        {can('backups.manage') && (
                                            <>
                                                <Button size="sm" variant="secondary" disabled={run.isPending} onClick={() => run.mutate(plan.id)}>
                                                    Run now
                                                </Button>{' '}
                                                <Button size="sm" variant="ghost" onClick={() => setEditing(plan)}>
                                                    Edit
                                                </Button>{' '}
                                                <Button size="sm" variant="ghost" onClick={() => confirm(`Delete the plan ${plan.name}? Its snapshots stay.`) && remove.mutate(plan.id)}>
                                                    Delete
                                                </Button>
                                            </>
                                        )}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </TableWrap>
            )}
            <ErrorNote message={[run.error, remove.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {editing && (
                <PlanModal
                    plan={editing === 'new' ? null : editing}
                    targets={targets}
                    onClose={() => setEditing(null)}
                    onSaved={() => {
                        setEditing(null);
                        refresh();
                    }}
                />
            )}
        </Card>
    );
}

/** Databases on every installed engine, as "engine/name". */
function useAllDatabases() {
    const engines = useQuery({ queryKey: ['db-engines'], queryFn: () => api.get<DatabaseEngineView[]>('/api/databases/engines') });
    const installed = (engines.data ?? []).filter((e) => e.installed);
    const lists = useQueries({
        queries: installed.map((e) => ({ queryKey: ['dbs', e.engine], queryFn: () => api.get<DatabaseInfo[]>(`/api/databases/${e.engine}/databases`) })),
    });

    return installed.flatMap((e, i) => (lists[i]?.data ?? []).map((d) => `${e.engine}/${d.name}`));
}

function PlanModal({ plan, targets, onClose, onSaved }: { plan: BackupPlan | null; targets: BackupTarget[]; onClose: () => void; onSaved: () => void }) {
    const databases = useAllDatabases();
    const [form, setForm] = useState({
        name: plan?.name ?? '',
        targetId: plan?.targetId ?? targets[0]?.id ?? 0,
        databases: plan?.databases ?? [],
        folders: (plan?.folders ?? []).join('\n'),
        panelState: plan?.panelState ?? true,
        enabled: plan?.enabled ?? true,
        timeOfDay: plan?.timeOfDay ?? '03:00',
        keepDaily: plan?.keepDaily ?? 7,
        keepWeekly: plan?.keepWeekly ?? 4,
        keepMonthly: plan?.keepMonthly ?? 6,
    });
    const set = (patch: Partial<typeof form>) => setForm((f) => ({ ...f, ...patch }));
    const save = useMutation({
        mutationFn: () => {
            const body = { ...form, folders: form.folders.split('\n').map((f) => f.trim()).filter(Boolean) };
            return plan ? api.put(`/api/backups/plans/${plan.id}`, body) : api.post('/api/backups/plans', body);
        },
        onSuccess: onSaved,
    });
    const toggle = (database: string) =>
        set({ databases: form.databases.includes(database) ? form.databases.filter((d) => d !== database) : [...form.databases, database] });

    return (
        <Modal open title={plan ? `Edit ${plan.name}` : 'Add a backup plan'} onClose={onClose}>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate();
                }}
            >
                <div className="grid gap-4 sm:grid-cols-2">
                    <Field label="Name" value={form.name} onChange={(e) => set({ name: e.target.value })} placeholder="Nightly" required />
                    <SelectField label="Target" value={form.targetId} onChange={(e) => set({ targetId: Number(e.target.value) })}>
                        {targets.map((t) => (
                            <option key={t.id} value={t.id}>
                                {t.name}
                            </option>
                        ))}
                    </SelectField>
                </div>
                <fieldset className="space-y-2">
                    <legend className="text-sm font-medium">Databases</legend>
                    {databases.length === 0 && <p className="text-sm text-slate-500">No databases yet.</p>}
                    <div className="grid gap-1 sm:grid-cols-2">
                        {[...new Set([...databases, ...form.databases])].map((d) => (
                            <label key={d} className="flex items-center gap-2 text-sm">
                                <input type="checkbox" checked={form.databases.includes(d)} onChange={() => toggle(d)} /> <code>{d}</code>
                            </label>
                        ))}
                    </div>
                </fieldset>
                <label className="block space-y-1 text-sm">
                    <span className="font-medium">Folders</span>
                    <textarea
                        className="block min-h-20 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                        value={form.folders}
                        onChange={(e) => set({ folders: e.target.value })}
                        placeholder={'hotel:sites/assets\nhotel:apps/emulator/shared'}
                    />
                    <span className="text-xs text-slate-500">One per line: app user, colon, folder inside its home.</span>
                </label>
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.panelState} onChange={(e) => set({ panelState: e.target.checked })} />
                    The panel's own state (its database, keys and settings) — needed to rebuild this server
                </label>
                <div className="grid gap-4 sm:grid-cols-4">
                    <Field label="Daily at" type="time" value={form.timeOfDay} onChange={(e) => set({ timeOfDay: e.target.value })} />
                    <Field label="Keep daily" type="number" min={0} value={form.keepDaily} onChange={(e) => set({ keepDaily: Number(e.target.value) })} />
                    <Field label="Weekly" type="number" min={0} value={form.keepWeekly} onChange={(e) => set({ keepWeekly: Number(e.target.value) })} />
                    <Field label="Monthly" type="number" min={0} value={form.keepMonthly} onChange={(e) => set({ keepMonthly: Number(e.target.value) })} />
                </div>
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" checked={form.enabled} onChange={(e) => set({ enabled: e.target.checked })} /> Run on schedule
                </label>
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={save.isPending}>
                        Save
                    </Button>
                </div>
            </form>
        </Modal>
    );
}

function SnapshotsCard({ targets, plans, onJob }: { targets: BackupTarget[]; plans: BackupPlan[]; onJob: (id: number) => void }) {
    const can = usePermissions();
    const [targetId, setTargetId] = useState(targets[0].id);
    const [restoring, setRestoring] = useState<BackupSnapshot | null>(null);
    const target = targets.find((t) => t.id === targetId) ?? targets[0];
    const snapshots = useQuery({
        queryKey: ['snapshots', target.id],
        queryFn: () => api.get<BackupSnapshot[]>(`/api/backups/targets/${target.id}/snapshots`),
        enabled: target.ready,
    });
    const planName = (planId: string) => plans.find((p) => `p${p.id}` === planId)?.name ?? planId;

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    <h2 className="font-medium">Snapshots</h2>
                    <p className="text-sm text-slate-600 dark:text-slate-400">Restoring never overwrites anything: you choose what to bring back, and where it goes is shown.</p>
                </div>
                <select
                    aria-label="Target"
                    className="rounded-lg border border-slate-300 bg-white px-2 py-1.5 text-sm dark:border-slate-700 dark:bg-slate-950"
                    value={target.id}
                    onChange={(e) => setTargetId(Number(e.target.value))}
                >
                    {targets.map((t) => (
                        <option key={t.id} value={t.id}>
                            {t.name}
                        </option>
                    ))}
                </select>
            </div>
            {!target.ready ? (
                <p className="text-sm text-slate-500">This target isn't ready yet.</p>
            ) : snapshots.isPending ? (
                <Spinner />
            ) : (snapshots.data ?? []).length === 0 ? (
                <p className="text-sm text-slate-500">No snapshots yet.</p>
            ) : (
                <ul className="divide-y divide-slate-100 dark:divide-slate-800">
                    {(snapshots.data ?? []).map((s) => (
                        <li key={s.id} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
                            <span>
                                {formatDateTime(s.time)} <span className="text-slate-500">· {planName(s.planId)} ·</span> <code className="text-xs">{s.shortId}</code>
                            </span>
                            {can('backups.manage') && (
                                <Button size="sm" variant="secondary" onClick={() => setRestoring(s)}>
                                    Restore…
                                </Button>
                            )}
                        </li>
                    ))}
                </ul>
            )}
            <ErrorNote message={snapshots.error ? errorMessage(snapshots.error) : null} />
            {restoring && (
                <RestoreModal
                    target={target}
                    snapshot={restoring}
                    onClose={() => setRestoring(null)}
                    onStarted={(jobId) => {
                        setRestoring(null);
                        onJob(jobId);
                    }}
                />
            )}
        </Card>
    );
}

function RestoreModal({ target, snapshot, onClose, onStarted }: { target: BackupTarget; snapshot: BackupSnapshot; onClose: () => void; onStarted: (jobId: number) => void }) {
    const withSudo = useSudo();
    const manifest = useQuery({
        queryKey: ['snapshot', target.id, snapshot.id],
        queryFn: () => api.get<BackupManifest>(`/api/backups/targets/${target.id}/snapshots/${snapshot.id}`),
    });
    const [chosen, setChosen] = useState<Set<string>>(new Set());
    const flip = (key: string) =>
        setChosen((current) => {
            const next = new Set(current);
            if (next.has(key)) next.delete(key);
            else next.add(key);
            return next;
        });
    const restore = useMutation({
        mutationFn: () =>
            withSudo(() =>
                api.post<{ jobId: number }>(`/api/backups/targets/${target.id}/snapshots/${snapshot.id}/restore`, {
                    databases: [...chosen].filter((c) => c.startsWith('db:')).map((c) => c.slice(3)),
                    folders: [...chosen].filter((c) => c.startsWith('dir:')).map((c) => c.slice(4)),
                    panelState: chosen.has('panel'),
                }),
            ),
        onSuccess: (data) => onStarted(data.jobId),
    });
    const m = manifest.data;

    return (
        <Modal open title={`Restore from ${formatDateTime(snapshot.time)}`} onClose={onClose}>
            {!m ? (
                manifest.error ? <ErrorNote message={errorMessage(manifest.error)} /> : <Spinner />
            ) : (
                <div className="space-y-4 text-sm">
                    {m.databases.length > 0 && (
                        <fieldset className="space-y-1">
                            <legend className="font-medium">Databases</legend>
                            <p className="text-xs text-slate-500">The dump joins the database's backups list; restore it from the Databases page.</p>
                            {m.databases.map((d) => (
                                <label key={d} className="flex items-center gap-2">
                                    <input type="checkbox" checked={chosen.has(`db:${d}`)} onChange={() => flip(`db:${d}`)} /> <code>{d}</code>
                                </label>
                            ))}
                        </fieldset>
                    )}
                    {m.folders.length > 0 && (
                        <fieldset className="space-y-1">
                            <legend className="font-medium">Folders</legend>
                            <p className="text-xs text-slate-500">Restored next to the original, as ~/restored-&lt;time&gt;/&lt;folder&gt; of the app user.</p>
                            {m.folders.map((f) => (
                                <label key={f} className="flex items-center gap-2">
                                    <input type="checkbox" checked={chosen.has(`dir:${f}`)} onChange={() => flip(`dir:${f}`)} /> <code>{f}</code>
                                </label>
                            ))}
                        </fieldset>
                    )}
                    {m.panelState && (
                        <label className="flex items-center gap-2">
                            <input type="checkbox" checked={chosen.has('panel')} onChange={() => flip('panel')} /> The panel's state (unpacked for a manual restore; see the docs)
                        </label>
                    )}
                    <ErrorNote message={restore.error ? errorMessage(restore.error) : null} />
                    <div className="flex justify-end gap-2">
                        <Button variant="ghost" onClick={onClose}>
                            Cancel
                        </Button>
                        <Button disabled={chosen.size === 0 || restore.isPending} onClick={() => restore.mutate()}>
                            Restore {chosen.size > 0 && `(${chosen.size})`}
                        </Button>
                    </div>
                </div>
            )}
        </Modal>
    );
}
