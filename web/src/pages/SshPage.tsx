import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type AuthorizedKey, type LoginUser, type SshdSettings } from '../api';
import { usePermissions } from '../auth';
import { SftpAccounts } from '../components/SftpAccounts';
import { Badge, Button, Card, ErrorNote, Notice, PageHeader, Spinner } from '../components/ui';

export function SshPage() {
    const users = useQuery({ queryKey: ['ssh', 'users'], queryFn: () => api.get<LoginUser[]>('/api/ssh/users') });
    const [selected, setSelected] = useState<string | null>(null);
    const user = selected ?? users.data?.find((u) => u.uid !== 0 && u.sudo)?.name ?? users.data?.[0]?.name ?? null;

    return (
        <div className="space-y-6">
            <PageHeader title="SSH access" description="Who can log in to this server over SSH, and how." />
            <SshdSettingsCard users={users.data ?? []} />
            <ErrorNote message={users.error ? errorMessage(users.error) : null} />
            {users.isPending ? (
                <Spinner />
            ) : (
                <div className="grid gap-6 lg:grid-cols-[16rem_1fr]">
                    <nav className="space-y-1">
                        {(users.data ?? []).map((u) => (
                            <button
                                key={u.name}
                                onClick={() => setSelected(u.name)}
                                className={`flex w-full items-center justify-between rounded-lg px-3 py-2 text-left text-sm ${u.name === user ? 'bg-brand-50 font-medium text-brand-700 dark:bg-brand-600/20 dark:text-brand-100' : 'hover:bg-slate-100 dark:hover:bg-slate-800'}`}
                            >
                                <span>
                                    {u.name}
                                    {u.sudo && <span className="ml-2 text-xs text-slate-500">admin</span>}
                                </span>
                                <span className="text-xs text-slate-500">{u.keyCount} keys</span>
                            </button>
                        ))}
                    </nav>
                    {user && <KeysCard user={user} />}
                </div>
            )}
            <SftpAccounts />
        </div>
    );
}

function SshdSettingsCard({ users }: { users: LoginUser[] }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const settings = useQuery({ queryKey: ['ssh', 'settings'], queryFn: () => api.get<SshdSettings>('/api/ssh/settings') });
    const update = useMutation({
        mutationFn: (body: { passwordAuthentication?: boolean; permitRootLogin?: string }) => api.put<SshdSettings>('/api/ssh/settings', body),
        onSuccess: (data) => queryClient.setQueryData(['ssh', 'settings'], data),
    });
    const adminKeys = users.filter((u) => u.sudo).reduce((sum, u) => sum + u.keyCount, 0);

    if (!settings.data) return <ErrorNote message={settings.error ? errorMessage(settings.error) : null} />;

    const s = settings.data;

    return (
        <Card className="space-y-4 p-5">
            <div className="grid gap-4 sm:grid-cols-3">
                <Setting label="Password login" value={s.passwordAuthentication ? <Badge tone="warning">⚠ Allowed</Badge> : <Badge tone="good">✓ Keys only</Badge>}>
                    {can('ssh.manage') && (
                        <Button
                            size="sm"
                            variant="secondary"
                            disabled={update.isPending || (s.passwordAuthentication && adminKeys === 0)}
                            title={s.passwordAuthentication && adminKeys === 0 ? 'Add an SSH key for an admin user first' : undefined}
                            onClick={() => {
                                const turningOff = s.passwordAuthentication;

                                if (!turningOff || confirm('Disable password login? Make sure you can log in with your SSH key first.')) {
                                    update.mutate({ passwordAuthentication: !turningOff });
                                }
                            }}
                        >
                            {s.passwordAuthentication ? 'Require keys' : 'Allow passwords'}
                        </Button>
                    )}
                </Setting>
                <Setting label="Root login" value={<Badge tone={s.permitRootLogin === 'yes' ? 'warning' : 'good'}>{rootLabel(s.permitRootLogin)}</Badge>}>
                    {can('ssh.manage') && (
                        <select
                            aria-label="Root login"
                            className="rounded-lg border border-slate-300 bg-white px-2 py-1 text-xs dark:border-slate-700 dark:bg-slate-950"
                            value={s.permitRootLogin}
                            disabled={update.isPending}
                            onChange={(e) => update.mutate({ permitRootLogin: e.target.value })}
                        >
                            <option value="prohibit-password">Keys only</option>
                            <option value="no">Not allowed</option>
                            <option value="yes">Keys or password</option>
                        </select>
                    )}
                </Setting>
                <Setting label="Port" value={<span className="font-mono text-sm">{s.ports.join(', ')}</span>} />
            </div>
            {s.passwordAuthentication && adminKeys === 0 && (
                <Notice tone="warning">No admin user has an SSH key yet. Add one below, check that it works, then require keys.</Notice>
            )}
            <ErrorNote message={update.error ? errorMessage(update.error) : null} />
        </Card>
    );
}

function Setting({ label, value, children }: { label: string; value: React.ReactNode; children?: React.ReactNode }) {
    return (
        <div className="space-y-2">
            <p className="text-sm text-slate-600 dark:text-slate-400">{label}</p>
            <div className="flex flex-wrap items-center gap-2">
                {value}
                {children}
            </div>
        </div>
    );
}

function rootLabel(value: string) {
    return value === 'yes' ? '⚠ Password allowed' : value === 'no' ? 'Not allowed' : value === 'prohibit-password' ? 'Keys only' : value;
}

function KeysCard({ user }: { user: string }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const [publicKey, setPublicKey] = useState('');
    const keys = useQuery({ queryKey: ['ssh', 'keys', user], queryFn: () => api.get<AuthorizedKey[]>(`/api/ssh/users/${user}/keys`) });
    const onSuccess = (data: AuthorizedKey[]) => {
        queryClient.setQueryData(['ssh', 'keys', user], data);
        void queryClient.invalidateQueries({ queryKey: ['ssh', 'users'] });
    };
    const add = useMutation({
        mutationFn: () => api.post<AuthorizedKey[]>(`/api/ssh/users/${user}/keys`, { publicKey }),
        onSuccess: (data) => {
            onSuccess(data);
            setPublicKey('');
        },
    });
    const remove = useMutation({
        mutationFn: (fingerprint: string) => api.delete<AuthorizedKey[]>(`/api/ssh/users/${user}/keys?fingerprint=${encodeURIComponent(fingerprint)}`),
        onSuccess,
    });

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">Authorized keys for {user}</h2>
            {keys.isPending && <Spinner />}
            {keys.data?.length === 0 && <p className="text-sm text-slate-500">No keys yet.</p>}
            <ul className="divide-y divide-slate-100 dark:divide-slate-800">
                {(keys.data ?? []).map((key) => (
                    <li key={key.fingerprint} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                        <div className="min-w-0">
                            <p className="text-sm font-medium">{key.comment || '(no comment)'}</p>
                            <p className="truncate font-mono text-xs text-slate-500">
                                {key.type} · {key.fingerprint}
                                {key.options && ` · ${key.options}`}
                            </p>
                        </div>
                        {can('ssh.manage') && (
                            <Button size="sm" variant="ghost" disabled={remove.isPending} onClick={() => confirm(`Remove ${key.comment || key.fingerprint}?`) && remove.mutate(key.fingerprint)}>
                                Remove
                            </Button>
                        )}
                    </li>
                ))}
            </ul>
            {can('ssh.manage') && (
                <form
                    className="space-y-2"
                    onSubmit={(e) => {
                        e.preventDefault();
                        add.mutate();
                    }}
                >
                    <textarea
                        className="block min-h-20 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                        placeholder="ssh-ed25519 AAAA… you@laptop"
                        value={publicKey}
                        onChange={(e) => setPublicKey(e.target.value)}
                    />
                    <p className="text-xs text-slate-500">
                        Paste a public key (the contents of <code>~/.ssh/id_ed25519.pub</code>). Don't have one? Run <code>ssh-keygen -t ed25519</code> on your computer.
                    </p>
                    <ErrorNote message={add.error ? errorMessage(add.error) : remove.error ? errorMessage(remove.error) : null} />
                    <Button type="submit" disabled={!publicKey.trim() || add.isPending}>
                        Add key
                    </Button>
                </form>
            )}
        </Card>
    );
}
