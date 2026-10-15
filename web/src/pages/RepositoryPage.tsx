import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router';
import { api, errorMessage, type GitHubSetupStep, type RepositoryView, type WebhookDeliveryView } from '../api';
import { usePermissions } from '../auth';
import { GitHubSetupResult, useGitHubAccount } from '../components/GitHub';
import { Badge, Button, Card, ErrorNote, Notice, PageHeader, Spinner } from '../components/ui';
import { formatDateTime } from '../format';

type Detail = { repository: RepositoryView; deliveries: WebhookDeliveryView[]; apps: { id: number; name: string }[] };

/** Installs the deploy keys (and optionally the webhook) through the connected GitHub account. */
function useGitHubSetup(repositoryId: number) {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: (webhook: boolean) => api.post<RepositoryView>(`/api/repositories/${repositoryId}/github`, { webhook }),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['repository', String(repositoryId)] });
            void queryClient.invalidateQueries({ queryKey: ['repositories'] });
        },
    });
}

export function RepositoryPage() {
    const { id } = useParams();
    const can = usePermissions();
    const navigate = useNavigate();
    const location = useLocation();
    const queryClient = useQueryClient();
    const github = useGitHubAccount();
    const detail = useQuery({ queryKey: ['repository', id], queryFn: () => api.get<Detail>(`/api/repositories/${id}`) });
    const setup = useGitHubSetup(Number(id));
    const test = useMutation({ mutationFn: () => api.post<{ ok: boolean; error: string | null; head: string | null }>(`/api/repositories/${id}/test`) });
    const remove = useMutation({
        mutationFn: () => api.delete(`/api/repositories/${id}`),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['repositories'] });
            void navigate('/apps?tab=repositories');
        },
    });

    if (!detail.data) return detail.error ? <ErrorNote message={errorMessage(detail.error)} /> : <Spinner />;

    const { repository, deliveries, apps } = detail.data;
    const connected = github.data?.connected ?? false;
    const keysInstalled = repository.deployKeyInstalledOnGitHub && repository.submoduleKeys.every((k) => k.installedOnGitHub);
    // Steps from adding the repository (passed along by the form) or from the setup button here.
    const steps: GitHubSetupStep[] = setup.data?.gitHubSetup ?? (location.state as { gitHubSetup?: GitHubSetupStep[] } | null)?.gitHubSetup ?? [];

    return (
        <div className="space-y-6">
            <PageHeader
                title={repository.fullName}
                description={`Branch ${repository.branch} · checked out as ${repository.systemUser} in ~/repos/${repository.name}`}
                actions={
                    <Link to="/apps?tab=repositories" className="rounded-lg px-4 py-2 text-sm font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800">
                        ← Repositories
                    </Link>
                }
            />

            <GitHubSetupResult steps={steps} />

            {repository.isPrivate &&
                (keysInstalled ? (
                    <Card className="space-y-1 p-5">
                        <h2 className="font-medium">1. Read access</h2>
                        <p className="text-sm text-muted">
                            ✓ The panel added {repository.submoduleKeys.length > 0 ? 'its deploy keys' : 'its deploy key'} (read-only) on GitHub.{' '}
                            <a href={repository.deployKeysUrl} target="_blank" rel="noreferrer" className="text-brand-700 underline dark:text-brand-100">
                                See them ↗
                            </a>
                        </p>
                    </Card>
                ) : (
                    <Card className="space-y-4 p-5">
                        <h2 className="font-medium">1. Give the server read access</h2>
                        {connected && can('apps.manage') && (
                            <div className="flex flex-wrap items-center gap-3">
                                <Button disabled={setup.isPending} onClick={() => setup.mutate(repository.webhookInstalledOnGitHub)}>
                                    {setup.isPending ? 'Adding…' : `Add on GitHub as ${github.data?.login}`}
                                </Button>
                                <span className="text-sm text-muted">or add {repository.submoduleKeys.length > 0 ? 'the keys' : 'the key'} by hand:</span>
                            </div>
                        )}
                        <ErrorNote message={setup.error ? errorMessage(setup.error) : null} />
                        {!repository.deployKeyInstalledOnGitHub && (
                            <>
                                <ol className="list-decimal space-y-1 pl-5 text-sm text-slate-700 dark:text-slate-300">
                                    <li>
                                        Open{' '}
                                        <a href={repository.deployKeysUrl} target="_blank" rel="noreferrer" className="font-medium text-brand-700 underline dark:text-brand-100">
                                            {repository.fullName} → Settings → Deploy keys ↗
                                        </a>{' '}
                                        and click <strong>Add deploy key</strong>.
                                    </li>
                                    <li>Title: "Turbo Stack". Paste the key below. Leave "Allow write access" off.</li>
                                </ol>
                                <KeyBox value={repository.publicKey ?? ''} />
                            </>
                        )}
                        {repository.submoduleKeys
                            .filter((key) => !key.installedOnGitHub)
                            .map((key) => (
                                <div key={key.repository} className="space-y-2">
                                    <p className="text-sm">
                                        Private submodule <strong>{key.repository}</strong>: add this key to{' '}
                                        <a href={`https://github.com/${key.repository}/settings/keys`} target="_blank" rel="noreferrer" className="text-brand-700 underline dark:text-brand-100">
                                            its deploy keys ↗
                                        </a>
                                        .
                                    </p>
                                    <KeyBox value={key.publicKey ?? ''} />
                                </div>
                            ))}
                    </Card>
                ))}

            <Card className="space-y-3 p-5">
                <h2 className="font-medium">{repository.isPrivate ? '2. ' : ''}Check access</h2>
                <p className="text-sm text-muted">The server tries to read the branch from GitHub with its key (nothing is downloaded).</p>
                {can('apps.manage') && (
                    <Button variant="secondary" disabled={test.isPending} onClick={() => test.mutate()}>
                        {test.isPending ? 'Checking…' : 'Test access'}
                    </Button>
                )}
                {test.data?.ok && <Notice tone="success">✓ Access works. Branch {repository.branch} is at {test.data.head?.slice(0, 7)}.</Notice>}
                {test.data && !test.data.ok && <Notice tone="danger">✕ {test.data.error}</Notice>}
                <ErrorNote message={test.error ? errorMessage(test.error) : null} />
                {repository.lastError && <Notice tone="warning">Last fetch failed: {repository.lastError}</Notice>}
            </Card>

            <WebhookCard repository={repository} deliveries={deliveries} connectedAs={connected ? github.data?.login ?? null : null} />

            <Card className="space-y-3 p-5">
                <h2 className="font-medium">Apps from this repository</h2>
                {apps.length === 0 ? (
                    <p className="text-sm text-muted">
                        None yet. <Link to="/apps" className="text-brand-700 underline dark:text-brand-100">Create an app</Link> to build and deploy it.
                    </p>
                ) : (
                    <ul className="flex flex-wrap gap-2">
                        {apps.map((app) => (
                            <li key={app.id}>
                                <Link to={`/apps/${app.id}`} className="rounded-lg bg-slate-100 px-3 py-1 text-sm hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700">
                                    {app.name}
                                </Link>
                            </li>
                        ))}
                    </ul>
                )}
                {can('apps.manage') && apps.length === 0 && (
                    <Button
                        size="sm"
                        variant="ghost"
                        onClick={() =>
                            confirm(
                                repository.deployKeyInstalledOnGitHub || repository.webhookInstalledOnGitHub
                                    ? `Remove ${repository.fullName} from the panel? The deploy keys and webhook the panel added on GitHub are removed too.`
                                    : `Remove ${repository.fullName} from the panel?`,
                            ) && remove.mutate()
                        }
                    >
                        Remove repository
                    </Button>
                )}
                <ErrorNote message={remove.error ? errorMessage(remove.error) : null} />
            </Card>
        </div>
    );
}

function KeyBox({ value }: { value: string }) {
    return (
        <div className="flex items-start gap-2">
            <code className="block flex-1 rounded-lg bg-slate-100 p-3 text-xs break-all dark:bg-slate-800">{value || 'Generating…'}</code>
            <Button size="sm" variant="secondary" onClick={() => void navigator.clipboard.writeText(value)}>
                Copy
            </Button>
        </div>
    );
}

type WebhookSecret = { url: string; secret: string; github?: GitHubSetupStep | null };

function WebhookCard({ repository, deliveries, connectedAs }: { repository: RepositoryView; deliveries: WebhookDeliveryView[]; connectedAs: string | null }) {
    const can = usePermissions();
    const setup = useGitHubSetup(repository.id);
    const [webhook, setWebhook] = useState<WebhookSecret | null>(null);
    const reveal = useMutation({ mutationFn: () => api.get<WebhookSecret>(`/api/repositories/${repository.id}/webhook`), onSuccess: setWebhook });
    const rotate = useMutation({ mutationFn: () => api.post<WebhookSecret>(`/api/repositories/${repository.id}/webhook/rotate`), onSuccess: setWebhook });
    const insecure = repository.webhookUrl.startsWith('http://') || /:\d+\//.test(repository.webhookUrl);
    const installed = repository.webhookInstalledOnGitHub;

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <h2 className="font-medium">Deploy automatically on push {installed ? '' : '(optional)'}</h2>
                {installed && <Badge tone="good">✓ webhook on GitHub</Badge>}
            </div>
            {installed ? (
                <p className="text-sm text-muted">
                    The panel created the webhook on GitHub; it sends pushes to <code className="text-xs">{repository.webhookUrl}</code>. Turn on "Deploy automatically" for the
                    apps that should follow pushes.
                </p>
            ) : (
                <>
                    {connectedAs && can('apps.manage') && (
                        <div className="flex flex-wrap items-center gap-3">
                            <Button disabled={setup.isPending || insecure} onClick={() => setup.mutate(true)}>
                                {setup.isPending ? 'Creating…' : `Create the webhook as ${connectedAs}`}
                            </Button>
                            <span className="text-sm text-muted">or add it by hand:</span>
                        </div>
                    )}
                    {setup.data?.gitHubSetup && <GitHubSetupResult steps={setup.data.gitHubSetup} />}
                    <ErrorNote message={setup.error ? errorMessage(setup.error) : null} />
                    <ol className="list-decimal space-y-1 pl-5 text-sm text-slate-700 dark:text-slate-300">
                        <li>
                            Open{' '}
                            <a href={repository.webhooksUrl} target="_blank" rel="noreferrer" className="font-medium text-brand-700 underline dark:text-brand-100">
                                {repository.fullName} → Settings → Webhooks → Add webhook ↗
                            </a>
                        </li>
                        <li>
                            Payload URL: <code className="text-xs">{repository.webhookUrl}</code>
                        </li>
                        <li>Content type: application/json · Secret: the value below · Events: "Just the push event".</li>
                        <li>Then turn on "Deploy automatically" for the apps that should follow pushes.</li>
                    </ol>
                </>
            )}
            {insecure && (
                <Notice tone="warning">
                    GitHub must be able to reach this address. Move the panel to its own domain (Settings → Panel address) so the webhook URL is a normal HTTPS address.
                </Notice>
            )}
            {can('apps.manage') && (
                <div className="space-y-2">
                    {webhook ? (
                        <div className="flex items-center gap-2">
                            <code className="flex-1 rounded-lg bg-slate-100 p-2 text-xs break-all dark:bg-slate-800">{webhook.secret}</code>
                            <Button size="sm" variant="secondary" onClick={() => void navigator.clipboard.writeText(webhook.secret)}>
                                Copy
                            </Button>
                        </div>
                    ) : (
                        <Button size="sm" variant="secondary" onClick={() => reveal.mutate()}>
                            Show secret
                        </Button>
                    )}
                    <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => confirm(installed ? 'Generate a new secret? The webhook on GitHub is updated too.' : 'Generate a new secret? Update it on GitHub too.') && rotate.mutate()}
                    >
                        New secret
                    </Button>
                    {webhook?.github && <GitHubSetupResult steps={[webhook.github]} />}
                </div>
            )}
            {deliveries.length > 0 && (
                <div className="space-y-1">
                    <p className="text-sm font-medium">Recent deliveries</p>
                    <ul className="space-y-1 text-xs">
                        {deliveries.map((d) => (
                            <li key={d.id} className="flex flex-wrap gap-2">
                                <span className="text-muted">{formatDateTime(d.receivedAt)}</span>
                                <Badge>{d.event}</Badge>
                                <span>{d.result}</span>
                            </li>
                        ))}
                    </ul>
                </div>
            )}
        </Card>
    );
}
