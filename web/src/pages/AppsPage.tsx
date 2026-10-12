import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, errorMessage, type AppSummary, type RepositoryView, type RuntimeView } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField, Spinner, TableWrap, Tabs } from '../components/ui';
import { formatRelative } from '../format';

type Tab = 'apps' | 'repositories' | 'runtimes';

export function AppsPage() {
    const [params, setParams] = useSearchParams();
    const tab = (params.get('tab') as Tab) ?? 'apps';

    return (
        <div className="space-y-6">
            <PageHeader title="Apps & deploys" description="Build and run code from GitHub. Each app has its own releases; a deploy only goes live when it builds (and its service is healthy)." />
            <Tabs<Tab>
                tabs={[
                    { id: 'apps', label: 'Apps' },
                    { id: 'repositories', label: 'Repositories' },
                    { id: 'runtimes', label: 'Runtimes' },
                ]}
                value={tab}
                onChange={(next) => setParams({ tab: next }, { replace: true })}
            />
            {tab === 'apps' && <AppsTab />}
            {tab === 'repositories' && <RepositoriesTab />}
            {tab === 'runtimes' && <RuntimesTab />}
        </div>
    );
}

export function StatusBadge({ status }: { status: string }) {
    switch (status) {
        case 'active':
            return <Badge tone="good">● live</Badge>;
        case 'building':
            return <Badge tone="info">building…</Badge>;
        case 'failed':
            return <Badge tone="danger">✕ failed</Badge>;
        default:
            return <Badge>{status}</Badge>;
    }
}

function AppsTab() {
    const can = usePermissions();
    const apps = useQuery({ queryKey: ['apps'], queryFn: () => api.get<AppSummary[]>('/api/apps'), refetchInterval: 10_000 });
    const repositories = useQuery({ queryKey: ['repositories'], queryFn: () => api.get<RepositoryView[]>('/api/repositories') });
    const [creating, setCreating] = useState(false);

    return (
        <div className="space-y-4">
            <ErrorNote message={apps.error ? errorMessage(apps.error) : null} />
            {can('apps.manage') && (
                <div className="flex justify-end gap-2">
                    <Button
                        variant="secondary"
                        onClick={() => setCreating(true)}
                        disabled={!repositories.data?.length}
                        title={repositories.data?.length ? undefined : 'Add a repository first'}
                    >
                        New app by hand
                    </Button>
                    <Link to="/apps/new">
                        <Button>New from a recipe</Button>
                    </Link>
                </div>
            )}
            {apps.data?.length === 0 && (
                <Notice>
                    Setting up a hotel? Start with a <Link to="/apps/new" className="font-medium underline">recipe</Link>: the Turbo emulator, its admin panel and site, the Nitro
                    client and imager are all ready to go.
                </Notice>
            )}
            {creating && repositories.data && <NewAppCard repositories={repositories.data} onClose={() => setCreating(false)} />}
            {apps.isPending ? (
                <Spinner />
            ) : (
                (apps.data?.length ?? 0) > 0 && (
                    <TableWrap>
                        <thead>
                            <tr>
                                <th>App</th>
                                <th>From</th>
                                <th>Last deploy</th>
                                <th>Runs</th>
                            </tr>
                        </thead>
                        <tbody>
                            {apps.data!.map((app) => (
                                <tr key={app.id}>
                                    <td>
                                        <Link to={`/apps/${app.id}`} className="font-medium text-brand-700 hover:underline dark:text-brand-100">
                                            {app.name}
                                        </Link>
                                        <p className="text-xs text-slate-500">user {app.systemUser}</p>
                                    </td>
                                    <td className="text-slate-600 dark:text-slate-400">
                                        {app.repository} <span className="text-xs">({app.branch})</span>
                                        {app.autoDeploy && <span className="ml-2"><Badge tone="info">auto</Badge></span>}
                                    </td>
                                    <td>
                                        {app.lastRelease ? (
                                            <>
                                                <StatusBadge status={app.lastRelease.status} />
                                                <p className="mt-1 max-w-xs truncate text-xs text-slate-500">
                                                    {formatRelative(app.lastRelease.startedAt)} · {app.lastRelease.commitMessage}
                                                </p>
                                            </>
                                        ) : (
                                            <span className="text-sm text-slate-500">Never deployed</span>
                                        )}
                                    </td>
                                    <td className="text-sm text-slate-600 dark:text-slate-400">
                                        {[app.hasService && 'service', app.staticPath && 'static files'].filter(Boolean).join(' + ') || '—'}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </TableWrap>
                )
            )}
        </div>
    );
}

function NewAppCard({ repositories, onClose }: { repositories: RepositoryView[]; onClose: () => void }) {
    const navigate = useNavigate();
    const [name, setName] = useState('');
    const [repositoryId, setRepositoryId] = useState(repositories[0]?.id ?? 0);
    const create = useMutation({
        mutationFn: () => api.post<{ id: number }>('/api/apps', { name, repositoryId }),
        onSuccess: (data) => void navigate(`/apps/${data.id}?new=1`),
    });

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">New app</h2>
            <form
                className="grid gap-3 sm:grid-cols-[1fr_1fr_auto] sm:items-end"
                onSubmit={(e) => {
                    e.preventDefault();
                    create.mutate();
                }}
            >
                <Field label="Name" value={name} onChange={(e) => setName(e.target.value.toLowerCase())} placeholder="emulator" pattern="[a-z0-9][a-z0-9-]{0,39}" required />
                <SelectField label="Repository" value={repositoryId} onChange={(e) => setRepositoryId(Number(e.target.value))}>
                    {repositories.map((r) => (
                        <option key={r.id} value={r.id}>
                            {r.fullName} ({r.branch}) · user {r.systemUser}
                        </option>
                    ))}
                </SelectField>
                <div className="flex gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={!name || create.isPending}>
                        Create
                    </Button>
                </div>
            </form>
            <p className="text-xs text-slate-500">Next you'll set how it's built and run.</p>
            <ErrorNote message={create.error ? errorMessage(create.error) : null} />
        </Card>
    );
}

function RepositoriesTab() {
    const can = usePermissions();
    const repositories = useQuery({ queryKey: ['repositories'], queryFn: () => api.get<RepositoryView[]>('/api/repositories') });
    const [creating, setCreating] = useState(false);

    return (
        <div className="space-y-4">
            <ErrorNote message={repositories.error ? errorMessage(repositories.error) : null} />
            {can('apps.manage') && (
                <div className="flex justify-end">
                    <Button onClick={() => setCreating(true)}>Add repository</Button>
                </div>
            )}
            {creating && <NewRepositoryCard onClose={() => setCreating(false)} />}
            {(repositories.data ?? []).map((repository) => (
                <Card key={repository.id} className="flex flex-wrap items-center justify-between gap-3 p-5">
                    <div>
                        <Link to={`/repositories/${repository.id}`} className="font-medium text-brand-700 hover:underline dark:text-brand-100">
                            {repository.fullName}
                        </Link>
                        <p className="text-xs text-slate-500">
                            {repository.branch} · {repository.isPrivate ? 'private (deploy key)' : 'public'} · user {repository.systemUser}
                            {repository.lastSyncedAt && ` · fetched ${formatRelative(repository.lastSyncedAt)}`}
                        </p>
                    </div>
                    {repository.lastError ? <Badge tone="danger">✕ {repository.lastError.slice(0, 60)}</Badge> : repository.lastCommit && <code className="text-xs">{repository.lastCommit.slice(0, 7)}</code>}
                </Card>
            ))}
        </div>
    );
}

function NewRepositoryCard({ onClose }: { onClose: () => void }) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [form, setForm] = useState({ repository: '', branch: 'main', name: '', systemUser: 'hotel', isPrivate: true, submodules: false, privateSubmodules: '' });
    const guessedName = form.repository.split('/').pop()?.replace(/\.git$/, '').toLowerCase().replace(/[^a-z0-9-]/g, '-') ?? '';
    const create = useMutation({
        mutationFn: () =>
            api.post<RepositoryView>('/api/repositories', {
                name: form.name || guessedName,
                systemUser: form.systemUser,
                repository: form.repository,
                branch: form.branch,
                private: form.isPrivate,
                submodules: form.submodules,
                privateSubmodules: form.privateSubmodules.split(/[\s,]+/).filter(Boolean),
            }),
        onSuccess: (data) => {
            void queryClient.invalidateQueries({ queryKey: ['repositories'] });
            void navigate(`/repositories/${data.id}`);
        },
    });

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">Add a GitHub repository</h2>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    create.mutate();
                }}
            >
                <div className="grid gap-3 sm:grid-cols-2">
                    <Field label="Repository" value={form.repository} onChange={(e) => setForm({ ...form, repository: e.target.value })} placeholder="owner/name or https://github.com/owner/name" required />
                    <Field label="Branch" value={form.branch} onChange={(e) => setForm({ ...form, branch: e.target.value })} required />
                    <Field label="Short name" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value.toLowerCase() })} placeholder={guessedName || 'turbo-cloud'} hint="Checkout folder and deploy key name." />
                    <Field
                        label="Linux user"
                        value={form.systemUser}
                        onChange={(e) => setForm({ ...form, systemUser: e.target.value.toLowerCase() })}
                        hint="Apps from this repository run as this user. Use separate users to isolate apps from each other."
                        required
                    />
                </div>
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={form.isPrivate} onChange={(e) => setForm({ ...form, isPrivate: e.target.checked })} />
                    Private repository (the panel creates a deploy key for you to add on GitHub)
                </label>
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={form.submodules} onChange={(e) => setForm({ ...form, submodules: e.target.checked })} />
                    Has git submodules
                </label>
                {form.submodules && (
                    <Field
                        label="Private submodules (optional)"
                        value={form.privateSubmodules}
                        onChange={(e) => setForm({ ...form, privateSubmodules: e.target.value })}
                        placeholder="owner/turbo-admin, owner/turbo-web"
                        hint="Each private submodule needs its own deploy key (GitHub allows a key on one repository only). Public submodules need nothing."
                    />
                )}
                <ErrorNote message={create.error ? errorMessage(create.error) : null} />
                <div className="flex gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={!form.repository || create.isPending}>
                        {create.isPending ? 'Setting up…' : 'Add repository'}
                    </Button>
                </div>
            </form>
        </Card>
    );
}

function RuntimesTab() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const runtimes = useQuery({ queryKey: ['runtimes'], queryFn: () => api.get<RuntimeView[]>('/api/runtimes') });
    const install = useMutation({
        mutationFn: ({ name, channel }: { name: string; channel: string }) => api.post<{ jobId: number }>(`/api/runtimes/${name}/install`, { channel }),
    });
    const labels: Record<string, { title: string; channel: string; usedBy: string }> = {
        dotnet: { title: '.NET SDK', channel: '10.0', usedBy: 'Turbo (the emulator)' },
        node: { title: 'Node.js (with Yarn via Corepack)', channel: '24', usedBy: 'Nitro, turbo-admin and turbo-web builds' },
    };

    return (
        <div className="space-y-4">
            <p className="text-sm text-slate-600 dark:text-slate-400">Toolchains apps build and run with. They're installed once under /opt/turbo-manager/runtimes and shared by every app.</p>
            <ErrorNote message={runtimes.error ? errorMessage(runtimes.error) : install.error ? errorMessage(install.error) : null} />
            <div className="grid gap-4 md:grid-cols-2">
                {(runtimes.data ?? []).map((runtime) => (
                    <Card key={runtime.name} className="space-y-3 p-5">
                        <div className="flex items-center justify-between">
                            <p className="font-medium">{labels[runtime.name]?.title ?? runtime.name}</p>
                            {runtime.installed ? <Badge tone="good">✓ {runtime.version}</Badge> : <Badge>Not installed</Badge>}
                        </div>
                        <p className="text-xs text-slate-500">Used by {labels[runtime.name]?.usedBy}.</p>
                        {can('apps.manage') && (
                            <Button size="sm" variant="secondary" disabled={install.isPending} onClick={() => install.mutate({ name: runtime.name, channel: labels[runtime.name]?.channel ?? '' })}>
                                {runtime.installed ? 'Update' : 'Install'} {runtime.name} {labels[runtime.name]?.channel}
                            </Button>
                        )}
                    </Card>
                ))}
            </div>
            {install.data && (
                <Card className="p-5">
                    <JobOutput jobId={install.data.jobId} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['runtimes'] })} />
                </Card>
            )}
        </div>
    );
}
