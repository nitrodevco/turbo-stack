import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { api, errorMessage, type GitHubAccountView, type GitHubRepositoryChoice, type GitHubSetupStep } from '../api';
import { usePermissions } from '../auth';
import { formatRelative } from '../format';
import { Badge, Button, Card, ErrorNote, Field, Input, Notice, Spinner } from './ui';

export function useGitHubAccount() {
    return useQuery({ queryKey: ['github'], queryFn: () => api.get<GitHubAccountView>('/api/github') });
}

/** Connect a GitHub account once; repositories can then be picked from a list and set up on GitHub without copy-pasting keys. */
export function GitHubConnectCard() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const account = useGitHubAccount();
    const [token, setToken] = useState('');
    const [open, setOpen] = useState(false);
    const refresh = (data?: GitHubAccountView) => {
        if (data) queryClient.setQueryData(['github'], data);
        void queryClient.invalidateQueries({ queryKey: ['github'] });
        void queryClient.removeQueries({ queryKey: ['github-repositories'] });
    };
    const connect = useMutation({
        mutationFn: () => api.put<GitHubAccountView>('/api/github/token', { token }),
        onSuccess: (data) => {
            setToken('');
            setOpen(false);
            refresh(data);
        },
    });
    const disconnect = useMutation({ mutationFn: () => api.delete('/api/github/token'), onSuccess: () => refresh() });

    if (!account.data) return account.error ? <ErrorNote message={errorMessage(account.error)} /> : null;

    const github = account.data;

    if (github.connected && !open) {
        return (
            <Card className="flex flex-wrap items-center justify-between gap-3 p-4">
                <div className="flex items-center gap-3">
                    {github.avatarUrl && <img src={github.avatarUrl} alt="" className="size-8 rounded-full" />}
                    <div>
                        <p className="text-sm font-medium">
                            GitHub: connected as {github.login}
                            {github.name && <span className="font-normal text-muted"> ({github.name})</span>}
                        </p>
                        <p className="text-xs text-muted">
                            Pick repositories from your account; deploy keys and webhooks are added for you.
                            {github.connectedBy && ` Connected by ${github.connectedBy}${github.connectedAt ? ` ${formatRelative(github.connectedAt)}` : ''}.`}
                        </p>
                    </div>
                </div>
                {can('apps.manage') && (
                    <div className="flex gap-2">
                        <Button size="sm" variant="secondary" onClick={() => setOpen(true)}>
                            Change token
                        </Button>
                        <Button
                            size="sm"
                            variant="ghost"
                            disabled={disconnect.isPending}
                            onClick={() => confirm('Disconnect GitHub? Deploy keys and webhooks already on GitHub keep working.') && disconnect.mutate()}
                        >
                            Disconnect
                        </Button>
                    </div>
                )}
                <ErrorNote message={disconnect.error ? errorMessage(disconnect.error) : null} />
            </Card>
        );
    }

    if (!can('apps.manage')) return null;

    return (
        <Card className="space-y-3 p-5">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <h2 className="font-medium">Connect GitHub</h2>
                {!github.connected && <Badge>optional</Badge>}
            </div>
            <p className="text-sm text-muted">
                Choose repositories from a list instead of typing them, and let the panel add the deploy keys and push webhook on GitHub for you. Without it, you add
                them by hand.
            </p>
            <ol className="list-decimal space-y-1 pl-5 text-sm text-slate-700 dark:text-slate-300">
                <li>
                    <a href={github.createTokenUrl} target="_blank" rel="noreferrer" className="font-medium text-brand-700 underline dark:text-brand-100">
                        Create a token on GitHub ↗
                    </a>{' '}
                    (the <code className="text-xs">repo</code> and <code className="text-xs">admin:repo_hook</code> scopes are filled in). Pick an expiry you're comfortable with.
                </li>
                <li>
                    Or use a fine-grained token with <em>Administration</em>, <em>Webhooks</em> and <em>Contents</em> (read) access to the repositories you deploy.
                </li>
                <li>Paste it below. It's stored encrypted and never shown again.</li>
            </ol>
            <form
                className="flex flex-wrap items-end gap-2"
                onSubmit={(e) => {
                    e.preventDefault();
                    connect.mutate();
                }}
            >
                <div className="min-w-64 flex-1">
                    <Field label="Personal access token" type="password" autoComplete="off" value={token} onChange={(e) => setToken(e.target.value)} placeholder="ghp_… or github_pat_…" />
                </div>
                {open && (
                    <Button type="button" variant="ghost" onClick={() => setOpen(false)}>
                        Cancel
                    </Button>
                )}
                <Button type="submit" disabled={!token.trim() || connect.isPending}>
                    {connect.isPending ? 'Checking…' : 'Connect'}
                </Button>
            </form>
            <ErrorNote message={connect.error ? errorMessage(connect.error) : null} />
        </Card>
    );
}

/** Searchable list of the connected account's repositories. */
export function GitHubRepositoryPicker({ value, onPick }: { value: string; onPick: (repository: GitHubRepositoryChoice) => void }) {
    const repositories = useQuery({
        queryKey: ['github-repositories'],
        queryFn: () => api.get<GitHubRepositoryChoice[]>('/api/github/repositories'),
        staleTime: 60_000,
    });
    const [filter, setFilter] = useState('');
    const shown = useMemo(() => {
        const needle = filter.trim().toLowerCase();

        return (repositories.data ?? []).filter((r) => !needle || r.fullName.toLowerCase().includes(needle)).slice(0, 50);
    }, [repositories.data, filter]);

    return (
        <div className="space-y-2">
            <div className="flex gap-2">
                <Input value={filter} onChange={(e) => setFilter(e.target.value)} placeholder="Search your repositories…" aria-label="Search repositories" />
                <Button type="button" variant="ghost" size="sm" disabled={repositories.isFetching} onClick={() => void repositories.refetch()} title="Reload from GitHub">
                    ↻
                </Button>
            </div>
            <ErrorNote message={repositories.error ? errorMessage(repositories.error) : null} />
            {repositories.isPending ? (
                <Spinner label="Loading repositories from GitHub…" />
            ) : (
                <ul className="max-h-72 divide-y divide-line overflow-y-auto rounded-lg border border-line">
                    {shown.length === 0 && <li className="p-3 text-sm text-muted">No repositories match.</li>}
                    {shown.map((repository) => (
                        <li key={repository.fullName}>
                            <button
                                type="button"
                                onClick={() => onPick(repository)}
                                className={`flex w-full items-center justify-between gap-3 px-3 py-2 text-left text-sm ${value === repository.fullName ? 'bg-accent-soft' : 'hover:bg-subtle'}`}
                            >
                                <span className="min-w-0">
                                    <span className="font-medium">{repository.fullName}</span>
                                    {repository.description && <span className="block truncate text-xs text-muted">{repository.description}</span>}
                                </span>
                                <span className="flex shrink-0 items-center gap-2">
                                    {!repository.admin && <Badge tone="warning">no admin</Badge>}
                                    <Badge>{repository.private ? 'private' : 'public'}</Badge>
                                    {repository.pushedAt && <span className="hidden text-xs text-muted sm:inline">{formatRelative(repository.pushedAt)}</span>}
                                </span>
                            </button>
                        </li>
                    ))}
                </ul>
            )}
        </div>
    );
}

/** Branches of a repository on GitHub (empty until a repository is chosen). */
export function useGitHubBranches(fullName: string | null) {
    return useQuery({
        queryKey: ['github-branches', fullName],
        queryFn: () => api.get<string[]>(`/api/github/repositories/${fullName}/branches`),
        enabled: !!fullName,
        staleTime: 60_000,
    });
}

export function GitHubSetupResult({ steps }: { steps: GitHubSetupStep[] }) {
    if (steps.length === 0) return null;

    const failed = steps.filter((s) => s.status !== 'ok');

    return (
        <Notice tone={failed.length ? 'warning' : 'success'}>
            <ul className="space-y-1">
                {steps.map((step) => (
                    <li key={step.label}>
                        {step.status === 'ok' ? '✓' : '✕'} {step.label}
                        {step.detail && <span className="block text-xs">{step.detail}</span>}
                    </li>
                ))}
            </ul>
        </Notice>
    );
}
