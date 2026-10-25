import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, errorMessage, type AppSummary, type RepositoryView, type RuntimeView, type SiteSummary } from '../api';
import { usePermissions } from '../auth';
import { cx } from '../components/cx';
import { GitHubConnectCard, GitHubRepositoryPicker, useGitHubAccount, useGitHubBranches } from '../components/GitHub';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField, Spinner, TableWrap, Tabs } from '../components/ui';
import { formatDateTime, formatRelative } from '../format';
import { newSiteLink, useAppsAndSites } from '../siteLinks';
import { CertificatesTab } from './CertificatesTab';

type Tab = 'apps' | 'certificates' | 'repositories' | 'runtimes';

/**
 * Apps (code built and run from GitHub) and sites (domains nginx serves) on one page: an app lists
 * the sites that serve it. The certificates the sites use, and the repositories and runtimes apps
 * are built from, are its other tabs, in the header's one row of tabs.
 */
export function AppsPage() {
    const can = usePermissions();
    const [params, setParams] = useSearchParams();
    const tabs: { id: Tab; label: string }[] = [
        { id: 'apps', label: 'Apps & sites' },
        ...(can('sites.view') ? [{ id: 'certificates' as const, label: 'Certificates' }] : []),
        ...(can('apps.view') ? [{ id: 'repositories' as const, label: 'Repositories' }, { id: 'runtimes' as const, label: 'Runtimes' }] : []),
    ];
    const asked = params.get('tab');
    const tab: Tab = tabs.find((t) => t.id === asked)?.id ?? 'apps';
    const repositories = useQuery({ queryKey: ['repositories'], queryFn: () => api.get<RepositoryView[]>('/api/repositories'), enabled: can('apps.view') });
    const [creating, setCreating] = useState(false);

    return (
        <div className="space-y-6">
            <PageHeader<Tab>
                title="Apps & sites"
                description="Apps are built from GitHub and run here; each deploy only goes live when it builds (and its service is healthy). Sites are the domains nginx serves: an app's files or port, a folder, a proxy or a redirect."
                tabs={tabs.length > 1 ? { items: tabs, value: tab, onChange: (next) => setParams(next === 'apps' ? {} : { tab: next }, { replace: true }) } : undefined}
                actions={
                    tab === 'apps' && (
                        <>
                            {can('sites.manage') && (
                                <Link to="/sites/new">
                                    <Button variant="secondary">Add site</Button>
                                </Link>
                            )}
                            {can('apps.manage') && (
                                <>
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
                                </>
                            )}
                        </>
                    )
                }
            />
            {tab === 'apps' && (
                <>
                    {creating && repositories.data && <NewAppCard repositories={repositories.data} onClose={() => setCreating(false)} />}
                    <AppsAndSitesTab />
                </>
            )}
            {tab === 'certificates' && <CertificatesTab />}
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

const kindLabels = { Static: 'Files', Spa: 'Single-page app', Proxy: 'Proxy', WebSocket: 'WebSocket', Redirect: 'Redirect' } as const;

/** A site's state at a glance: not applied, off, waiting for its certificate, or live (with or without HTTPS). */
function siteState(site: SiteSummary): { tone: 'bad' | 'muted' | 'warn' | 'good'; label: string } {
    if (site.lastError) return { tone: 'bad', label: 'Not applied' };
    if (!site.enabled) return { tone: 'muted', label: 'Disabled' };
    if (site.tlsMode !== 'None' && !site.https) return { tone: 'warn', label: 'Waiting for certificate' };

    return { tone: 'good', label: site.https ? 'Live, HTTPS' : 'Live, HTTP only' };
}

const dotTones = { bad: 'bg-bad', muted: 'bg-muted', warn: 'bg-warn', good: 'bg-good' } as const;

/** A site as a small link: its first domain, with a dot for its state. */
export function SiteChip({ site }: { site: SiteSummary }) {
    const state = siteState(site);

    return (
        <Link
            to={`/sites/${site.id}`}
            title={`${site.domains.join(', ')} · ${state.label}`}
            className="inline-flex max-w-full items-center gap-1.5 rounded-md border border-line bg-subtle px-2 py-0.5 text-xs hover:border-muted/50"
        >
            <span className={cx('size-1.5 shrink-0 rounded-full', dotTones[state.tone])} />
            <span className="truncate">{site.domains[0] ?? site.name}</span>
            {site.https && <span aria-label="HTTPS">🔒</span>}
        </Link>
    );
}

/** An app as a small link, for the sites that serve it. */
export function AppChip({ app }: { app: AppSummary }) {
    return (
        <Link to={`/apps/${app.id}`} className="inline-flex items-center gap-1.5 rounded-md border border-accent/40 bg-accent-soft px-2 py-0.5 text-xs text-accent hover:border-accent">
            {app.name}
        </Link>
    );
}

function AppsAndSitesTab() {
    const can = usePermissions();
    const { apps, sites, sitesOf, appsOf } = useAppsAndSites();
    const appList = apps.data ?? [];
    const siteList = sites.data ?? [];
    // A site that serves an app is listed with it; the rest (a folder, a proxy, a redirect) stand on their own.
    const shownApps = appList;
    const shownSites = siteList.filter((site) => appsOf(site).length === 0);
    const pending = (can('apps.view') && apps.isPending) || (can('sites.view') && sites.isPending);
    const empty = !pending && appList.length === 0 && siteList.length === 0;
    const error = apps.error ?? sites.error;

    return (
        <div className="space-y-4">
            <ErrorNote message={error ? errorMessage(error) : null} />
            {empty && (
                <Notice>
                    {can('apps.view') ? (
                        <>
                            Setting up a hotel? Start with a <Link to="/apps/new" className="font-medium underline">recipe</Link>: the Turbo emulator, its admin panel and site, the
                            Nitro client and imager are all ready to go. Or add a site on its own to serve a folder, proxy to a port or redirect a domain.
                        </>
                    ) : (
                        'No sites yet. Add one to serve a domain from this server.'
                    )}
                </Notice>
            )}
            {pending ? (
                <Spinner />
            ) : (
                shownApps.length + shownSites.length > 0 && (
                    <TableWrap>
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>What it is</th>
                                <th>Sites & HTTPS</th>
                                <th>Status</th>
                            </tr>
                        </thead>
                        <tbody>
                            {shownApps.map((app) => (
                                <AppRow key={`app-${app.id}`} app={app} sites={sitesOf(app)} canAddSite={can('sites.manage')} showSites={can('sites.view')} />
                            ))}
                            {shownSites.map((site) => (
                                <SiteRow key={`site-${site.id}`} site={site} apps={appsOf(site)} />
                            ))}
                        </tbody>
                    </TableWrap>
                )
            )}
        </div>
    );
}

function AppRow({ app, sites, canAddSite, showSites }: { app: AppSummary; sites: SiteSummary[]; canAddSite: boolean; showSites: boolean }) {
    return (
        <tr>
            <td>
                <div className="flex items-center gap-2">
                    <Badge tone="info">app</Badge>
                    <Link to={`/apps/${app.id}`} className="font-medium text-accent hover:underline">
                        {app.name}
                    </Link>
                </div>
                <p className="mt-1 text-xs text-muted">user {app.systemUser}</p>
            </td>
            <td className="text-muted">
                {app.repository} <span className="text-xs">({app.branch})</span>
                {app.autoDeploy && <span className="ml-2"><Badge tone="info">auto</Badge></span>}
                <p className="text-xs">Runs {[app.hasService && `a service${app.servicePort ? ` on port ${app.servicePort}` : ''}`, app.staticPath && 'static files'].filter(Boolean).join(' + ') || 'nothing yet'}</p>
            </td>
            <td>
                {showSites && (
                    <div className="flex max-w-xs flex-wrap gap-1.5">
                        {sites.map((site) => (
                            <SiteChip key={site.id} site={site} />
                        ))}
                        {sites.length === 0 &&
                            (canAddSite && (app.staticPath || app.servicePort) ? (
                                <Link to={newSiteLink(app)} className="text-xs text-accent hover:underline">
                                    + Add a site
                                </Link>
                            ) : (
                                <span className="text-xs text-muted">No site</span>
                            ))}
                    </div>
                )}
            </td>
            <td>
                {app.lastRelease ? (
                    <>
                        <StatusBadge status={app.lastRelease.status} />
                        <p className="mt-1 max-w-xs truncate text-xs text-muted">
                            {formatRelative(app.lastRelease.startedAt)} · {app.lastRelease.commitMessage}
                        </p>
                    </>
                ) : (
                    <span className="text-sm text-muted">Never deployed</span>
                )}
            </td>
        </tr>
    );
}

function SiteRow({ site, apps }: { site: SiteSummary; apps: AppSummary[] }) {
    return (
        <tr>
            <td>
                <div className="flex items-center gap-2">
                    <Badge>site</Badge>
                    <Link to={`/sites/${site.id}`} className="font-medium text-accent hover:underline">
                        {site.domains[0] ?? site.name}
                    </Link>
                </div>
                <p className="mt-1 text-xs text-muted">
                    {site.name}
                    {site.domains.length > 1 && ` · +${site.domains.length - 1} more`}
                    {site.system && ' · panel'}
                </p>
            </td>
            <td className="text-muted">
                {site.custom
                    ? 'Custom config'
                    : site.routes.map((r) => (
                          <span key={r.path} className="mr-3 whitespace-nowrap">
                              <code className="text-xs">{r.path}</code> {kindLabels[r.kind]}
                          </span>
                      ))}
                {apps.length > 0 && (
                    <div className="mt-1 flex flex-wrap items-center gap-1.5 text-xs">
                        Serves
                        {apps.map((app) => (
                            <AppChip key={app.id} app={app} />
                        ))}
                    </div>
                )}
            </td>
            <td>
                {site.tlsMode === 'None' ? (
                    <Badge>HTTP only</Badge>
                ) : site.https ? (
                    <Badge tone="good">🔒 until {formatDateTime(site.certificateExpires!).split(',')[0]}</Badge>
                ) : (
                    <Badge tone="warning">⚠ Waiting for certificate</Badge>
                )}
            </td>
            <td>
                {site.lastError ? <Badge tone="danger">✕ Not applied</Badge> : site.enabled ? <Badge tone="good">● Live</Badge> : <Badge>Disabled</Badge>}
            </td>
        </tr>
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
            <p className="text-xs text-muted">Next you'll set how it's built and run.</p>
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
            <GitHubConnectCard />
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
                        <p className="text-xs text-muted">
                            {repository.branch} · {repository.isPrivate ? 'private (deploy key)' : 'public'} · user {repository.systemUser}
                            {repository.webhookInstalledOnGitHub && ' · webhook on GitHub'}
                            {repository.lastSyncedAt && ` · fetched ${formatRelative(repository.lastSyncedAt)}`}
                        </p>
                    </div>
                    {repository.lastError ? <Badge tone="danger">✕ {repository.lastError.slice(0, 60)}</Badge> : repository.lastCommit && <code className="text-xs">{repository.lastCommit.slice(0, 7)}</code>}
                </Card>
            ))}
        </div>
    );
}

/** GitHub only delivers webhooks to an address it can reach with a trusted certificate. */
const panelReachable = window.location.protocol === 'https:' && !window.location.port;

function NewRepositoryCard({ onClose }: { onClose: () => void }) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const github = useGitHubAccount();
    const connected = github.data?.connected ?? false;
    const [source, setSource] = useState<'github' | 'manual'>('github');
    const fromGitHub = connected && source === 'github';
    const [form, setForm] = useState({ repository: '', branch: 'main', name: '', systemUser: 'hotel', isPrivate: true, submodules: false, privateSubmodules: '', webhook: panelReachable });
    const branches = useGitHubBranches(fromGitHub && form.repository ? form.repository : null);
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
                setUpOnGitHub: connected,
                webhook: connected && form.webhook,
            }),
        onSuccess: (data) => {
            void queryClient.invalidateQueries({ queryKey: ['repositories'] });
            void navigate(`/repositories/${data.id}`, { state: { gitHubSetup: data.gitHubSetup } });
        },
    });

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <h2 className="font-medium">Add a GitHub repository</h2>
                {connected && (
                    <Tabs<'github' | 'manual'>
                        tabs={[
                            { id: 'github', label: `From ${github.data?.login}` },
                            { id: 'manual', label: 'Any repository' },
                        ]}
                        value={source}
                        onChange={setSource}
                    />
                )}
            </div>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    create.mutate();
                }}
            >
                {fromGitHub && (
                    <GitHubRepositoryPicker
                        value={form.repository}
                        onPick={(r) => setForm({ ...form, repository: r.fullName, branch: r.defaultBranch, isPrivate: r.private, name: '' })}
                    />
                )}
                <div className="grid gap-3 sm:grid-cols-2">
                    {fromGitHub ? (
                        <>
                            <Field label="Repository" value={form.repository} readOnly placeholder="Pick one above" required />
                            {branches.data?.length ? (
                                <SelectField label="Branch" value={form.branch} onChange={(e) => setForm({ ...form, branch: e.target.value })}>
                                    {branches.data.map((branch) => (
                                        <option key={branch}>{branch}</option>
                                    ))}
                                </SelectField>
                            ) : (
                                <Field label="Branch" value={form.branch} onChange={(e) => setForm({ ...form, branch: e.target.value })} required />
                            )}
                        </>
                    ) : (
                        <>
                            <Field label="Repository" value={form.repository} onChange={(e) => setForm({ ...form, repository: e.target.value })} placeholder="owner/name or https://github.com/owner/name" required />
                            <Field label="Branch" value={form.branch} onChange={(e) => setForm({ ...form, branch: e.target.value })} required />
                        </>
                    )}
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
                    {connected ? 'Private repository (the panel creates a deploy key and adds it on GitHub)' : 'Private repository (the panel creates a deploy key for you to add on GitHub)'}
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
                {connected && (
                    <label className="flex items-start gap-2 text-sm">
                        <input type="checkbox" className="mt-0.5 size-4 accent-brand-600" checked={form.webhook} onChange={(e) => setForm({ ...form, webhook: e.target.checked })} />
                        <span>
                            Create the push webhook on GitHub (apps with "Deploy automatically" then deploy on every push)
                            {!panelReachable && (
                                <span className="block text-xs text-muted">
                                    GitHub can't deliver to this address: it needs HTTPS with a real certificate. Move the panel to its own domain first, or leave this off.
                                </span>
                            )}
                        </span>
                    </label>
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
            <p className="text-sm text-muted">Toolchains apps build and run with. They're installed once under /opt/turbo-stack/runtimes and shared by every app.</p>
            <ErrorNote message={runtimes.error ? errorMessage(runtimes.error) : install.error ? errorMessage(install.error) : null} />
            <div className="grid gap-4 md:grid-cols-2">
                {(runtimes.data ?? []).map((runtime) => (
                    <Card key={runtime.name} className="space-y-3 p-5">
                        <div className="flex items-center justify-between">
                            <p className="font-medium">{labels[runtime.name]?.title ?? runtime.name}</p>
                            {runtime.installed ? <Badge tone="good">✓ {runtime.version}</Badge> : <Badge>Not installed</Badge>}
                        </div>
                        <p className="text-xs text-muted">Used by {labels[runtime.name]?.usedBy}.</p>
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
