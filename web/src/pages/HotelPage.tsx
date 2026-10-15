import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, Navigate, useParams } from 'react-router';
import { api, errorMessage, type MemberChangeView, type PresetRoleView, type PresetView, type StackRoleView, type StackView } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, Spinner } from '../components/ui';
import { StatusBadge } from './AppsPage';

/** /hotel: the hotel if there is one, otherwise the wizard. */
export function HotelsPage() {
    const stacks = useQuery({ queryKey: ['stacks'], queryFn: () => api.get<{ id: number; name: string; domain: string }[]>('/api/stacks') });

    if (stacks.isPending) return <Spinner />;
    if (stacks.data?.length === 1) return <Navigate to={`/hotel/${stacks.data[0].id}`} replace />;
    if (stacks.data?.length === 0) return <Navigate to="/hotel/new" replace />;

    return (
        <div className="space-y-6">
            <PageHeader title="Hotels" actions={<Link to="/hotel/new"><Button>New hotel</Button></Link>} />
            <ErrorNote message={stacks.error ? errorMessage(stacks.error) : null} />
            <div className="grid gap-3 sm:grid-cols-2">
                {stacks.data?.map((s) => (
                    <Link key={s.id} to={`/hotel/${s.id}`}>
                        <Card className="p-4 hover:border-brand-400">
                            <p className="font-medium">{s.name}</p>
                            <p className="text-sm text-muted">{s.domain}</p>
                        </Card>
                    </Link>
                ))}
            </div>
        </div>
    );
}

export function HotelPage() {
    const { id } = useParams();
    const can = usePermissions();
    const queryClient = useQueryClient();
    const stack = useQuery({ queryKey: ['stack', id], queryFn: () => api.get<StackView>(`/api/stacks/${id}`), refetchInterval: 10_000 });
    const [jobId, setJobId] = useState<number | null>(null);
    const [editing, setEditing] = useState(false);
    const install = useMutation({ mutationFn: () => api.post<{ jobId: number }>(`/api/stacks/${id}/install`), onSuccess: (d) => setJobId(d.jobId) });
    const presets = useQuery({ queryKey: ['presets'], queryFn: () => api.get<PresetView[]>('/api/presets') });

    if (!stack.data) return stack.error ? <ErrorNote message={errorMessage(stack.error)} /> : <Spinner />;

    const hotel = stack.data;
    const shownJob = jobId ?? hotel.installJobId;
    const url = (role: string) => (hotel.hosts[role] ? `${hotel.tlsMode === 'None' ? 'http' : 'https'}://${hotel.hosts[role]}` : null);

    return (
        <div className="space-y-6">
            <PageHeader
                title={hotel.settings.hotelName || hotel.name}
                description={`${hotel.presetName} on ${hotel.domain} · runs as ${hotel.systemUser}${hotel.databaseEngine ? ` · ${hotel.databaseEngine}` : ''}`}
                actions={
                    <>
                        {url('client') && (
                            <a href={url('client')!} target="_blank" rel="noreferrer">
                                <Button variant="secondary">Play</Button>
                            </a>
                        )}
                        <a href={`/api/stacks/${hotel.id}/answers`}>
                            <Button variant="secondary">Answers file</Button>
                        </a>
                        {can('apps.manage') && (
                            <Button onClick={() => install.mutate()} disabled={install.isPending}>
                                {hotel.roles.every((r) => !r.included || !r.app || r.app.currentRelease) ? 'Run install again' : 'Install / continue'}
                            </Button>
                        )}
                    </>
                }
            />
            <ErrorNote message={install.error ? errorMessage(install.error) : null} />

            <AdminLink stackId={hotel.id} />

            <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                {hotel.roles.map((role) => (
                    <RoleCard
                        key={role.role}
                        role={role}
                        url={url(role.role)}
                        hotel={hotel}
                        preset={presets.data?.find((p) => p.id === hotel.preset)?.roles.find((r) => r.role === role.role)}
                        onJob={(job) => {
                            setJobId(job);
                            void queryClient.invalidateQueries({ queryKey: ['stack', id] });
                        }}
                    />
                ))}
            </div>

            {can('apps.manage') &&
                (editing ? (
                    <DomainEditor
                        hotel={hotel}
                        onClose={() => setEditing(false)}
                        onApplied={(job) => {
                            setEditing(false);
                            setJobId(job);
                            void queryClient.invalidateQueries({ queryKey: ['stack', id] });
                        }}
                    />
                ) : (
                    <Button variant="secondary" onClick={() => setEditing(true)}>
                        Change domains
                    </Button>
                ))}

            {shownJob && (
                <Card className="space-y-2 p-5">
                    <h2 className="font-medium">{jobId ? 'Progress' : 'Last install'}</h2>
                    <JobOutput jobId={shownJob} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['stack', id] })} />
                </Card>
            )}
        </div>
    );
}

function RoleCard({
    role,
    url,
    hotel,
    preset,
    onJob,
}: {
    role: StackRoleView;
    url: string | null;
    hotel: StackView;
    preset: PresetRoleView | undefined;
    onJob: (jobId: number) => void;
}) {
    const can = usePermissions();
    const health = !role.included
        ? null
        : role.app?.service === 'failed' || role.app?.lastRelease?.status === 'failed' || role.site?.lastError
          ? 'danger'
          : role.app && !role.app.currentRelease
            ? 'warning'
            : 'good';

    return (
        <Card className={`space-y-3 p-4 ${!role.included ? 'opacity-60' : ''}`}>
            <div className="flex items-start justify-between gap-2">
                <div>
                    <p className="font-medium">{role.label}</p>
                    {url ? (
                        <a href={url} target="_blank" rel="noreferrer" className="text-sm text-brand-700 hover:underline dark:text-brand-100">
                            {role.domain}
                        </a>
                    ) : (
                        <p className="text-sm text-muted">Not on this server</p>
                    )}
                </div>
                {health && <Badge tone={health}>{health === 'good' ? '● healthy' : health === 'warning' ? 'not deployed' : '✕ problem'}</Badge>}
            </div>
            {role.app && (
                <div className="space-y-1 text-sm">
                    <p>
                        <Link to={`/apps/${role.app.id}`} className="text-brand-700 hover:underline dark:text-brand-100">
                            {role.app.name}
                        </Link>{' '}
                        {role.app.lastRelease && <StatusBadge status={role.app.lastRelease.status} />}
                        {role.app.service && role.app.service !== 'active' && <Badge tone="danger">service {role.app.service}</Badge>}
                    </p>
                    {Object.keys(role.app.ports).length > 0 && (
                        <p className="text-xs text-muted">
                            {Object.entries(role.app.ports)
                                .map(([name, port]) => `${name} :${port}`)
                                .join(' · ')}
                        </p>
                    )}
                    {role.app.lastRelease?.error && <p className="text-xs text-bad">{role.app.lastRelease.error}</p>}
                </div>
            )}
            {role.site?.lastError && <p className="text-xs text-bad">Site: {role.site.lastError}</p>}
            {role.wiring.length > 0 && (
                <details className="text-xs">
                    <summary className="cursor-pointer text-muted">Gets {role.wiring.length} values from the hotel</summary>
                    <ul className="mt-2 space-y-1">
                        {role.wiring.map((w) => (
                            <li key={w.input}>
                                <span className="text-muted">{w.label}</span> ← <code>{w.from}</code>
                                {w.pinned && (
                                    <span className="ml-1">
                                        <Badge tone="warning">pinned</Badge>
                                    </span>
                                )}
                                <br />
                                <code className="break-all">{w.value || '(empty)'}</code>
                            </li>
                        ))}
                    </ul>
                </details>
            )}
            {can('apps.manage') && role.optional && preset && <PartControls role={role} hotel={hotel} preset={preset} onJob={onJob} />}
        </Card>
    );
}

function AdminLink({ stackId }: { stackId: number }) {
    const can = usePermissions();
    const link = useQuery({
        queryKey: ['stack-admin-link', stackId],
        queryFn: () => api.get<{ url: string | null }>(`/api/stacks/${stackId}/admin-setup-link`),
        enabled: can('apps.manage'),
        refetchInterval: (query) => (query.state.data?.url ? false : 30_000),
    });

    if (!link.data) return null;

    return link.data.url ? (
        <Notice tone="success">
            Your admin panel setup link (works once):{' '}
            <a href={link.data.url} target="_blank" rel="noreferrer" className="font-medium underline">
                {link.data.url}
            </a>
        </Notice>
    ) : (
        <Notice>Sign up on the website with the owner's username: your admin panel setup link then appears here.</Notice>
    );
}

function DomainEditor({ hotel, onClose, onApplied }: { hotel: StackView; onClose: () => void; onApplied: (jobId: number) => void }) {
    const [hosts, setHosts] = useState(hotel.hosts);
    const [changes, setChanges] = useState<MemberChangeView[] | null>(null);
    const preview = useMutation({
        mutationFn: () => api.put<MemberChangeView[]>(`/api/stacks/${hotel.id}`, { tlsMode: hotel.tlsMode, hosts }),
        onSuccess: setChanges,
    });
    const apply = useMutation({ mutationFn: () => api.post<{ jobId: number }>(`/api/stacks/${hotel.id}/apply`), onSuccess: (d) => onApplied(d.jobId) });
    const relevant = changes?.filter((c) => c.action !== 'none' || c.environment.length > 0 || c.site !== 'none') ?? [];

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">Domains</h2>
            <div className="grid gap-3 sm:grid-cols-2">
                {hotel.roles
                    .filter((r) => r.included)
                    .map((role) => (
                        <Field key={role.role} label={role.label} value={hosts[role.role] ?? ''} onChange={(e) => setHosts({ ...hosts, [role.role]: e.target.value.trim().toLowerCase() })} />
                    ))}
            </div>
            <ErrorNote message={preview.error ? errorMessage(preview.error) : apply.error ? errorMessage(apply.error) : null} />
            {changes && (
                <div className="space-y-2 text-sm">
                    {relevant.length === 0 ? (
                        <Notice>Nothing to change.</Notice>
                    ) : (
                        relevant.map((change) => (
                            <div key={change.role} className="rounded-lg border border-slate-200 p-3 dark:border-slate-800">
                                <p className="font-medium">
                                    {change.name} <Badge tone={change.action === 'redeploy' ? 'warning' : 'info'}>{actionLabel[change.action]}</Badge>
                                </p>
                                {change.site === 'update' && change.domainsFrom.join() !== change.domainsTo.join() && (
                                    <p className="text-muted">
                                        {change.domainsFrom.join(', ')} → {change.domainsTo.join(', ')}
                                    </p>
                                )}
                                {change.site === 'hand-edited' && <p className="text-warn">The site was edited by hand: it won't be changed.</p>}
                                <ul className="mt-1 space-y-0.5 text-xs">
                                    {change.environment.map((e) => (
                                        <li key={e.key}>
                                            <code>{e.key}</code>: {e.from ?? '(unset)'} → {e.to ?? '(unset)'}
                                            {e.pinned && <span className="ml-1 text-warn">(edited by hand: kept)</span>}
                                        </li>
                                    ))}
                                </ul>
                            </div>
                        ))
                    )}
                </div>
            )}
            <div className="flex gap-2">
                <Button variant="secondary" onClick={onClose}>
                    Cancel
                </Button>
                <Button variant="secondary" onClick={() => preview.mutate()} disabled={preview.isPending}>
                    {changes ? 'Save and preview again' : 'Save and preview'}
                </Button>
                {relevant.length > 0 && (
                    <Button onClick={() => apply.mutate()} disabled={apply.isPending}>
                        Apply
                    </Button>
                )}
            </div>
        </Card>
    );
}

const actionLabel: Record<MemberChangeView['action'], string> = {
    none: 'settings only',
    reload: 'nginx reload',
    restart: 'restart',
    redeploy: 'rebuild',
};

/** Adding an optional part to this server, or removing it in favour of an address elsewhere. */
function PartControls({ role, hotel, preset, onJob }: { role: StackRoleView; hotel: StackView; preset: PresetRoleView; onJob: (jobId: number) => void }) {
    const [open, setOpen] = useState(false);
    const [host, setHost] = useState(preset.host === '@' ? hotel.domain : `${preset.host}.${hotel.domain}`);
    const [replacements, setReplacements] = useState<Record<string, string>>(
        Object.fromEntries(preset.exports.map((e) => [`${role.role}.${e}`, hotel.settings[`${role.role}.${e}`] ?? ''])),
    );
    const add = useMutation({
        mutationFn: () => api.post<{ jobId: number }>(`/api/stacks/${hotel.id}/roles/${role.role}`, { host }),
        onSuccess: (d) => onJob(d.jobId),
    });
    const remove = useMutation({
        mutationFn: () => api.delete<{ jobId: number }>(`/api/stacks/${hotel.id}/roles/${role.role}`, { replacements }),
        onSuccess: (d) => onJob(d.jobId),
    });
    const error = add.error ?? remove.error;

    if (!open) {
        return (
            <Button size="sm" variant="ghost" onClick={() => setOpen(true)}>
                {role.included ? 'Remove from this server…' : 'Add to this server…'}
            </Button>
        );
    }

    return (
        <form
            className="space-y-3 border-t border-slate-200 pt-3 dark:border-slate-800"
            onSubmit={(e) => {
                e.preventDefault();
                if (role.included) remove.mutate();
                else add.mutate();
            }}
        >
            {role.included ? (
                <>
                    <p className="text-xs text-muted">
                        The other parts will use these addresses instead. The app and its site are removed{role.role === 'assets' ? '; uploaded files stay' : ''}.
                    </p>
                    {preset.exports.map((e) => (
                        <Field
                            key={e}
                            label={`${role.role}.${e}`}
                            value={replacements[`${role.role}.${e}`] ?? ''}
                            onChange={(event) => setReplacements({ ...replacements, [`${role.role}.${e}`]: event.target.value })}
                            placeholder="https://"
                            required
                        />
                    ))}
                </>
            ) : (
                <Field label="Domain" value={host} onChange={(e) => setHost(e.target.value.trim().toLowerCase())} required />
            )}
            <ErrorNote message={error ? errorMessage(error) : null} />
            <div className="flex gap-2">
                <Button type="button" size="sm" variant="ghost" onClick={() => setOpen(false)}>
                    Cancel
                </Button>
                <Button type="submit" size="sm" variant={role.included ? 'danger' : 'primary'} disabled={add.isPending || remove.isPending}>
                    {role.included ? `Remove ${role.label.toLowerCase()}` : `Add ${role.label.toLowerCase()}`}
                </Button>
            </div>
        </form>
    );
}
