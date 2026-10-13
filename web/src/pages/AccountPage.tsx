import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type Me } from '../api';
import { useSudo } from '../components/Sudo';
import { Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField } from '../components/ui';
import { formatRelative } from '../format';
import { addPasskey, passkeysSupported } from '../passkeys';

type SessionRow = { id: string; createdAt: string; lastSeenAt: string; ipAddress: string | null; userAgent: string | null; current: boolean };

type TokenRow = { id: number; name: string; prefix: string; permissions: string[]; createdAt: string; expiresAt: string | null; lastUsedAt: string | null };

export function AccountPage({ me }: { me: Me }) {
    const queryClient = useQueryClient();
    const sessions = useQuery({ queryKey: ['my-sessions'], queryFn: () => api.get<SessionRow[]>('/api/account/sessions') });
    const tokens = useQuery({ queryKey: ['my-tokens'], queryFn: () => api.get<TokenRow[]>('/api/account/tokens') });
    const revoke = useMutation({
        mutationFn: (id: string) => api.delete(`/api/account/sessions/${id}`),
        onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['my-sessions'] }),
    });
    const deleteToken = useMutation({
        mutationFn: (id: number) => api.delete(`/api/account/tokens/${id}`),
        onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['my-tokens'] }),
    });

    return (
        <div className="space-y-6">
            <PageHeader title="Your account" description={`${me.username} · ${me.role} · ${me.remainingRecoveryCodes} recovery codes left`} />
            {me.remainingRecoveryCodes < 3 && <Notice tone="warning">You're running out of recovery codes. Ask an admin to reset your authenticator if you lose it.</Notice>}

            <PasskeysCard />

            <Card className="space-y-3 p-5">
                <h2 className="font-medium">Signed-in browsers</h2>
                <ErrorNote message={sessions.error ? errorMessage(sessions.error) : null} />
                <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                    {sessions.data?.map((s) => (
                        <li key={s.id} className="flex items-center justify-between gap-3 py-2">
                            <span>
                                {s.current ? <span className="font-medium">This browser</span> : s.ipAddress ?? 'unknown address'} · active {formatRelative(s.lastSeenAt)}
                                <span className="block max-w-xl truncate text-xs text-slate-500">{s.userAgent}</span>
                            </span>
                            {!s.current && (
                                <Button size="sm" variant="ghost" onClick={() => revoke.mutate(s.id)}>
                                    Sign out
                                </Button>
                            )}
                        </li>
                    ))}
                </ul>
            </Card>

            <Card className="space-y-4 p-5">
                <div>
                    <h2 className="font-medium">API tokens</h2>
                    <p className="text-sm text-slate-500">
                        For scripts and CI: send <code>Authorization: Bearer &lt;token&gt;</code>. A token can do some of what your role can, never more.
                    </p>
                </div>
                <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                    {tokens.data?.map((t) => (
                        <li key={t.id} className="flex items-center justify-between gap-3 py-2">
                            <span>
                                <span className="font-medium">{t.name}</span> <code className="text-xs">{t.prefix}…</code>
                                <span className="block text-xs text-slate-500">
                                    {t.permissions.join(', ')} · {t.lastUsedAt ? `used ${formatRelative(t.lastUsedAt)}` : 'never used'}
                                    {t.expiresAt && ` · expires ${formatRelative(t.expiresAt)}`}
                                </span>
                            </span>
                            <Button size="sm" variant="ghost" onClick={() => confirm(`Delete the token "${t.name}"?`) && deleteToken.mutate(t.id)}>
                                Delete
                            </Button>
                        </li>
                    ))}
                </ul>
                <NewToken me={me} />
            </Card>
        </div>
    );
}

function NewToken({ me }: { me: Me }) {
    const queryClient = useQueryClient();
    const withSudo = useSudo();
    const [name, setName] = useState('');
    const [permissions, setPermissions] = useState<string[]>([]);
    const [days, setDays] = useState('90');
    const [created, setCreated] = useState<string | null>(null);
    const available = me.permissions.filter((p) => p !== 'terminal.root' && p !== 'users.manage');
    const create = useMutation({
        mutationFn: () => withSudo(() => api.post<{ token: string }>('/api/account/tokens', { name, permissions, expiresInDays: Number(days) || null })),
        onSuccess: (data) => {
            setCreated(data.token);
            setName('');
            setPermissions([]);
            void queryClient.invalidateQueries({ queryKey: ['my-tokens'] });
        },
    });

    return (
        <form
            className="space-y-3 border-t border-slate-100 pt-4 dark:border-slate-800"
            onSubmit={(e) => {
                e.preventDefault();
                create.mutate();
            }}
        >
            {created && (
                <Notice tone="success">
                    Copy the token now; it won't be shown again: <code className="break-all">{created}</code>
                </Notice>
            )}
            <div className="grid gap-3 sm:grid-cols-[2fr_1fr]">
                <Field label="New token" value={name} onChange={(e) => setName(e.target.value)} placeholder="GitHub Actions" required />
                <SelectField label="Expires" value={days} onChange={(e) => setDays(e.target.value)}>
                    <option value="30">in 30 days</option>
                    <option value="90">in 90 days</option>
                    <option value="365">in a year</option>
                    <option value="">never</option>
                </SelectField>
            </div>
            <div className="flex flex-wrap gap-x-4 gap-y-1 text-sm">
                {available.map((p) => (
                    <label key={p} className="flex items-center gap-2">
                        <input
                            type="checkbox"
                            className="size-4 accent-brand-600"
                            checked={permissions.includes(p)}
                            onChange={() => setPermissions(permissions.includes(p) ? permissions.filter((x) => x !== p) : [...permissions, p])}
                        />
                        <code className="text-xs">{p}</code>
                    </label>
                ))}
            </div>
            <ErrorNote message={create.error && !(create.error instanceof Error && create.error.message === 'Cancelled.') ? errorMessage(create.error) : null} />
            <Button type="submit" disabled={create.isPending || permissions.length === 0}>
                Create token
            </Button>
        </form>
    );
}

type PasskeyRow = { id: number; name: string; rpId: string; createdAt: string; lastUsedAt: string | null };

/** Passkeys sign in without a password or code (a fingerprint, face or PIN on your device). */
function PasskeysCard() {
    const queryClient = useQueryClient();
    const withSudo = useSudo();
    const [name, setName] = useState('');
    const list = useQuery({ queryKey: ['my-passkeys'], queryFn: () => api.get<{ rpId: string | null; passkeys: PasskeyRow[] }>('/api/account/passkeys') });
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['my-passkeys'] });
    const add = useMutation({
        mutationFn: () => withSudo(() => addPasskey(name.trim())),
        onSuccess: () => {
            setName('');
            refresh();
        },
    });
    const remove = useMutation({ mutationFn: (id: number) => withSudo(() => api.delete(`/api/account/passkeys/${id}`)), onSuccess: refresh });
    const rpId = list.data?.rpId;

    return (
        <Card className="space-y-4 p-5">
            <div>
                <h2 className="font-medium">Passkeys</h2>
                <p className="text-sm text-slate-600 dark:text-slate-400">
                    Sign in with your device's fingerprint, face or PIN instead of a password and code. A passkey works on the address it was added on.
                </p>
            </div>
            <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                {(list.data?.passkeys ?? []).map((p) => (
                    <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 py-2">
                        <span>
                            <span className="font-medium">{p.name}</span> · {p.rpId} ·{' '}
                            {p.lastUsedAt ? `used ${formatRelative(p.lastUsedAt)}` : `added ${formatRelative(p.createdAt)}`}
                        </span>
                        <Button size="sm" variant="ghost" onClick={() => confirm(`Remove the passkey ${p.name}?`) && remove.mutate(p.id)}>
                            Remove
                        </Button>
                    </li>
                ))}
            </ul>
            {!passkeysSupported() ? (
                <Notice>This browser doesn't support passkeys.</Notice>
            ) : !rpId ? (
                <Notice>Passkeys need the panel's own domain: move the panel to it under Settings → Panel address first.</Notice>
            ) : (
                <form
                    className="flex flex-wrap items-end gap-2"
                    onSubmit={(e) => {
                        e.preventDefault();
                        add.mutate();
                    }}
                >
                    <Field label="Name" value={name} onChange={(e) => setName(e.target.value)} placeholder="Laptop fingerprint" required />
                    <Button type="submit" disabled={add.isPending || !name.trim()}>
                        {add.isPending ? 'Waiting for your device…' : 'Add a passkey'}
                    </Button>
                </form>
            )}
            <ErrorNote
                message={
                    add.error && !(add.error instanceof Error && add.error.message === 'Cancelled.')
                        ? add.error instanceof DOMException
                            ? add.error.message
                            : errorMessage(add.error)
                        : remove.error
                          ? errorMessage(remove.error)
                          : null
                }
            />
        </Card>
    );
}
