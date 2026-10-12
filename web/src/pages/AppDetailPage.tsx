import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { api, errorMessage, type AppSettings, type JobSummary, type ReleaseView } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Button, Card, ErrorNote, Field, Notice, PageHeader, Spinner } from '../components/ui';
import { formatDateTime } from '../format';
import { StatusBadge } from './AppsPage';

type Detail = { app: AppSettings; releases: ReleaseView[] };

export function AppDetailPage() {
    const { id } = useParams();
    const [params] = useSearchParams();
    const can = usePermissions();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const detail = useQuery({ queryKey: ['app', id], queryFn: () => api.get<Detail>(`/api/apps/${id}`) });
    const runningJobs = useQuery({ queryKey: ['jobs', `deploy-app-${id}`], queryFn: () => api.get<JobSummary[]>(`/api/jobs?kind=deploy-app-${id}`) });
    const [jobId, setJobId] = useState<number | null>(null);
    const activeJob = jobId ?? runningJobs.data?.find((j) => j.status === 'running')?.id ?? null;
    const refresh = () => {
        void queryClient.invalidateQueries({ queryKey: ['app', id] });
        void queryClient.invalidateQueries({ queryKey: ['apps'] });
    };

    const deploy = useMutation({ mutationFn: () => api.post<{ jobId: number }>(`/api/apps/${id}/deploy`), onSuccess: (data) => setJobId(data.jobId) });
    const restart = useMutation({ mutationFn: () => api.post(`/api/apps/${id}/restart`) });
    const rollback = useMutation({
        mutationFn: (release: string) => api.post<{ jobId: number }>(`/api/apps/${id}/releases/${release}/activate`),
        onSuccess: (data) => setJobId(data.jobId),
    });
    const remove = useMutation({
        mutationFn: () => api.delete(`/api/apps/${id}`),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['apps'] });
            void navigate('/apps');
        },
    });

    if (!detail.data) return detail.error ? <ErrorNote message={errorMessage(detail.error)} /> : <Spinner />;

    const { app, releases } = detail.data;

    return (
        <div className="space-y-6">
            <PageHeader
                title={app.name}
                description={`${app.repository} · runs as ${app.systemUser}${app.currentRelease ? ` · live: ${app.currentRelease}` : ''}`}
                actions={
                    <>
                        <Link to="/apps" className="rounded-lg px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                            ← Apps
                        </Link>
                        {can('apps.deploy') && app.serviceCommand && app.currentRelease && (
                            <Button variant="secondary" disabled={restart.isPending} onClick={() => restart.mutate()}>
                                {restart.isSuccess ? '✓ Restarted' : 'Restart'}
                            </Button>
                        )}
                        {can('apps.deploy') && (
                            <Button disabled={deploy.isPending || !!activeJob} onClick={() => deploy.mutate()}>
                                {activeJob ? 'Deploying…' : 'Deploy now'}
                            </Button>
                        )}
                    </>
                }
            />
            {params.get('new') && !app.currentRelease && <Notice>Set how this app is built and run below, then press Deploy now.</Notice>}
            <ErrorNote message={[deploy.error, restart.error, rollback.error, remove.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {activeJob && (
                <Card className="p-5">
                    <JobOutput
                        jobId={activeJob}
                        onEnd={() => {
                            refresh();
                            void runningJobs.refetch();
                        }}
                    />
                </Card>
            )}

            {app.staticPath && (
                <Card className="flex flex-wrap items-center justify-between gap-3 p-5">
                    <div>
                        <p className="font-medium">Static files</p>
                        <code className="text-xs">{app.staticPath}</code>
                    </div>
                    <Link
                        to={`/sites/new?target=${encodeURIComponent(app.staticPath)}&kind=Spa`}
                        className="rounded-lg border border-slate-300 px-3 py-1.5 text-sm font-medium hover:bg-slate-50 dark:border-slate-700 dark:hover:bg-slate-800"
                    >
                        Serve them with a site
                    </Link>
                </Card>
            )}

            <Card className="space-y-3 p-5">
                <h2 className="font-medium">Releases</h2>
                {releases.length === 0 && <p className="text-sm text-slate-500">Not deployed yet.</p>}
                <ul className="divide-y divide-slate-100 dark:divide-slate-800">
                    {releases.map((release) => (
                        <li key={release.name} className="flex flex-wrap items-start justify-between gap-3 py-2.5">
                            <div className="min-w-0">
                                <p className="flex flex-wrap items-center gap-2 text-sm">
                                    <StatusBadge status={release.status} />
                                    <code className="text-xs">{release.commit?.slice(0, 7)}</code>
                                    <span className="truncate">{release.commitMessage}</span>
                                </p>
                                <p className="text-xs text-slate-500">
                                    {formatDateTime(release.startedAt)} · {release.trigger}
                                    {release.triggeredBy && ` by ${release.triggeredBy}`}
                                    {release.commitAuthor && ` · commit by ${release.commitAuthor}`}
                                </p>
                                {release.error && <p className="text-xs text-red-700 dark:text-red-300">{release.error}</p>}
                            </div>
                            <div className="flex gap-1">
                                {release.jobId && (
                                    <Button size="sm" variant="ghost" onClick={() => setJobId(release.jobId)}>
                                        Log
                                    </Button>
                                )}
                                {can('apps.deploy') && release.status === 'inactive' && !release.error && (
                                    <Button size="sm" variant="ghost" disabled={rollback.isPending || !!activeJob} onClick={() => confirm(`Put ${release.name} live again?`) && rollback.mutate(release.name)}>
                                        Roll back to this
                                    </Button>
                                )}
                            </div>
                        </li>
                    ))}
                </ul>
            </Card>

            {can('apps.manage') && <SettingsCard app={app} onSaved={refresh} />}
            {can('apps.manage') && <EnvironmentCard appId={app.id} hasService={!!app.serviceCommand} />}

            {can('apps.manage') && (
                <Card className="space-y-2 p-5">
                    <h2 className="font-medium">Delete app</h2>
                    <p className="text-sm text-slate-500">Stops its service and removes its releases and shared files from the server.</p>
                    <Button variant="danger" size="sm" disabled={remove.isPending} onClick={() => prompt(`Type ${app.name} to delete it and its files:`) === app.name && remove.mutate()}>
                        Delete {app.name}
                    </Button>
                </Card>
            )}
        </div>
    );
}

function SettingsCard({ app, onSaved }: { app: AppSettings; onSaved: () => void }) {
    const toForm = (a: AppSettings) => ({
        workingDirectory: a.workingDirectory,
        buildScript: a.buildScript,
        dotnet: a.runtimes.includes('dotnet'),
        node: a.runtimes.includes('node'),
        sharedPaths: a.sharedPaths.map((p) => (p.directory ? `${p.path}/` : p.path)).join('\n'),
        staticOutput: a.staticOutput ?? '',
        serviceCommand: a.serviceCommand ?? '',
        servicePort: a.servicePort?.toString() ?? '',
        healthPath: a.healthPath ?? '',
        memoryMaxMb: a.memoryMaxMb,
        keepReleases: a.keepReleases,
        autoDeploy: a.autoDeploy,
        deployPaths: a.deployPaths.join('\n'),
        purgeCloudflare: a.purgeCloudflare,
    });
    const [form, setForm] = useState(toForm(app));
    const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm({ ...form, [key]: value });
    const lines = (text: string) => text.split('\n').map((l) => l.trim()).filter(Boolean);
    const save = useMutation({
        mutationFn: () =>
            api.put(`/api/apps/${app.id}`, {
                name: app.name,
                repositoryId: app.repositoryId,
                workingDirectory: form.workingDirectory,
                buildScript: form.buildScript,
                runtimes: [form.dotnet && 'dotnet', form.node && 'node'].filter(Boolean),
                sharedPaths: lines(form.sharedPaths).map((p) => ({ path: p.replace(/\/$/, ''), directory: p.endsWith('/') })),
                staticOutput: form.staticOutput || null,
                serviceCommand: form.serviceCommand || null,
                servicePort: form.servicePort ? Number(form.servicePort) : null,
                healthPath: form.healthPath || null,
                memoryMaxMb: Number(form.memoryMaxMb),
                stopTimeoutSeconds: app.stopTimeoutSeconds,
                keepReleases: Number(form.keepReleases),
                autoDeploy: form.autoDeploy,
                deployPaths: lines(form.deployPaths),
                purgeCloudflare: form.purgeCloudflare,
            }),
        onSuccess: onSaved,
    });

    useEffect(() => setForm(toForm(app)), [app]);

    return (
        <Card className="space-y-5 p-5">
            <h2 className="font-medium">Build and run</h2>
            <form
                className="space-y-5"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate();
                }}
            >
                <div className="grid gap-4 md:grid-cols-2">
                    <Field label="Folder in the repository" value={form.workingDirectory} onChange={(e) => set('workingDirectory', e.target.value)} placeholder="(repository root)" hint="Where the build runs and the service starts." />
                    <div className="space-y-1.5">
                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Toolchains</span>
                        <div className="flex gap-4 pt-2 text-sm">
                            <label className="flex items-center gap-2">
                                <input type="checkbox" className="size-4 accent-brand-600" checked={form.dotnet} onChange={(e) => set('dotnet', e.target.checked)} />
                                .NET
                            </label>
                            <label className="flex items-center gap-2">
                                <input type="checkbox" className="size-4 accent-brand-600" checked={form.node} onChange={(e) => set('node', e.target.checked)} />
                                Node.js
                            </label>
                        </div>
                    </div>
                </div>
                <label className="block space-y-1.5">
                    <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Build script</span>
                    <textarea
                        className="block min-h-28 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                        value={form.buildScript}
                        onChange={(e) => set('buildScript', e.target.value)}
                        placeholder={'corepack yarn install --immutable\ncorepack yarn build'}
                    />
                    <span className="block text-xs text-slate-500">Runs with bash as {app.systemUser} in the new release. $TM_SHARED_DIR points at the shared folder.</span>
                </label>
                <div className="grid gap-4 md:grid-cols-2">
                    <label className="block space-y-1.5">
                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Shared between releases</span>
                        <textarea
                            className="block min-h-20 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                            value={form.sharedPaths}
                            onChange={(e) => set('sharedPaths', e.target.value)}
                            placeholder={'.env\nassets/\ncamera/'}
                        />
                        <span className="block text-xs text-slate-500">One per line; end folders with "/". Kept across deploys (uploads, config, data).</span>
                    </label>
                    <Field label="Built static files" value={form.staticOutput} onChange={(e) => set('staticOutput', e.target.value)} placeholder="dist" hint="Folder a site serves, relative to the repository root." />
                </div>
                <div className="grid gap-4 md:grid-cols-[2fr_1fr_1fr]">
                    <Field label="Service command (optional)" value={form.serviceCommand} onChange={(e) => set('serviceCommand', e.target.value)} placeholder="dotnet Turbo.Main.dll" hint="Kept running by systemd; restarted on every deploy." />
                    <Field label="Port" value={form.servicePort} onChange={(e) => set('servicePort', e.target.value)} placeholder="9001" inputMode="numeric" />
                    <Field label="Health path" value={form.healthPath} onChange={(e) => set('healthPath', e.target.value)} placeholder="/health" />
                </div>
                {form.serviceCommand && !form.servicePort && <p className="text-xs text-slate-500">Without a port the deploy can't check that the service actually started.</p>}
                <div className="grid gap-4 md:grid-cols-3">
                    <Field label="Memory limit (MB, 0 = none)" type="number" min={0} value={form.memoryMaxMb} onChange={(e) => set('memoryMaxMb', Number(e.target.value))} />
                    <Field label="Releases to keep" type="number" min={1} max={50} value={form.keepReleases} onChange={(e) => set('keepReleases', Number(e.target.value))} />
                </div>
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={form.autoDeploy} onChange={(e) => set('autoDeploy', e.target.checked)} />
                    Deploy automatically when the branch is pushed (needs the repository's webhook)
                </label>
                {form.autoDeploy && (
                    <label className="block space-y-1.5">
                        <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Only when these paths change (optional)</span>
                        <textarea
                            className="block min-h-16 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                            value={form.deployPaths}
                            onChange={(e) => set('deployPaths', e.target.value)}
                            placeholder={'packages/nitro-imager/\npackages/nitro-renderer/'}
                        />
                    </label>
                )}
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={form.purgeCloudflare} onChange={(e) => set('purgeCloudflare', e.target.checked)} />
                    Purge Cloudflare's cache for sites serving this app after each deploy
                </label>
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                {save.isSuccess && <Notice tone="success">✓ Saved. Changes apply on the next deploy (or press Restart for service settings).</Notice>}
                <Button type="submit" disabled={save.isPending}>
                    Save
                </Button>
            </form>
        </Card>
    );
}

function EnvironmentCard({ appId, hasService }: { appId: number; hasService: boolean }) {
    const environment = useQuery({ queryKey: ['app-env', appId], queryFn: () => api.get<Record<string, string>>(`/api/apps/${appId}/environment`) });
    const [text, setText] = useState<string | null>(null);
    const value = text ?? Object.entries(environment.data ?? {}).map(([k, v]) => `${k}=${v}`).join('\n');
    const save = useMutation({
        mutationFn: () => {
            const entries = value
                .split('\n')
                .map((line) => line.trim())
                .filter((line) => line && !line.startsWith('#'))
                .map((line) => {
                    const index = line.indexOf('=');

                    return index > 0 ? [line.slice(0, index).trim(), line.slice(index + 1)] : [line, ''];
                });

            return api.put(`/api/apps/${appId}/environment`, Object.fromEntries(entries));
        },
        onSuccess: () => setText(null),
    });

    return (
        <Card className="space-y-3 p-5">
            <h2 className="font-medium">Environment variables</h2>
            <p className="text-sm text-slate-500">
                Available to the build{hasService && ' and the service'}. Stored encrypted. One KEY=value per line.
            </p>
            <textarea
                className="block min-h-32 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                value={value}
                onChange={(e) => setText(e.target.value)}
                placeholder={'DOTNET_ENVIRONMENT=Production\nTURBO__Turbo__HotelName=My Hotel'}
                spellCheck={false}
            />
            <ErrorNote message={save.error ? errorMessage(save.error) : environment.error ? errorMessage(environment.error) : null} />
            {save.isSuccess && <Notice tone="success">✓ Saved. Deploy or restart to apply.</Notice>}
            <Button variant="secondary" disabled={save.isPending || text === null} onClick={() => save.mutate()}>
                Save variables
            </Button>
        </Card>
    );
}
