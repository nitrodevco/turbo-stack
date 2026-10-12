import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type AppSummary } from '../api';
import { useSudo } from '../components/Sudo';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField, Spinner, TableWrap } from '../components/ui';
import { formatRelative } from '../format';

type Member = {
    id: number;
    username: string;
    role: string;
    twoFactor: boolean;
    invitePending: boolean;
    inviteExpiresAt: string | null;
    invitedBy: string | null;
    lastLoginAt: string | null;
    scopeStacks: number[];
    scopeApps: number[];
    activeSessions: number;
};

type SessionRow = { id: string; username: string; createdAt: string; lastSeenAt: string; ipAddress: string | null; userAgent: string | null };

const roles = [
    { id: 'Admin', description: 'Everything except a root shell, including the team.' },
    { id: 'Developer', description: 'Deploys, logs, files, console and a shell as the app user.' },
    { id: 'Operator', description: 'Restarts services, deploys, reads logs, uses the console.' },
    { id: 'Viewer', description: 'Sees the dashboard and services.' },
];

export function TeamPage() {
    const members = useQuery({ queryKey: ['team'], queryFn: () => api.get<Member[]>('/api/team') });
    const sessions = useQuery({ queryKey: ['team-sessions'], queryFn: () => api.get<SessionRow[]>('/api/team/sessions') });
    const apps = useQuery({ queryKey: ['apps'], queryFn: () => api.get<AppSummary[]>('/api/apps') });
    const stacks = useQuery({ queryKey: ['stacks'], queryFn: () => api.get<{ id: number; name: string }[]>('/api/stacks') });
    const queryClient = useQueryClient();
    const withSudo = useSudo();
    const [inviteUrl, setInviteUrl] = useState<{ username: string; url: string } | null>(null);
    const [editing, setEditing] = useState<Member | 'new' | null>(null);
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['team'] });

    const reset = useMutation({
        mutationFn: (m: Member) => withSudo(() => api.post<{ inviteUrl: string }>(`/api/team/${m.id}/reset`)),
        onSuccess: (data, m) => {
            setInviteUrl({ username: m.username, url: data.inviteUrl });
            refresh();
        },
    });
    const remove = useMutation({ mutationFn: (m: Member) => withSudo(() => api.delete(`/api/team/${m.id}`)), onSuccess: refresh });
    const revoke = useMutation({
        mutationFn: (id: string) => api.delete(`/api/team/sessions/${id}`),
        onSuccess: () => void queryClient.invalidateQueries({ queryKey: ['team-sessions'] }),
    });

    const scopeLabel = (m: Member) =>
        [
            ...m.scopeStacks.map((id) => stacks.data?.find((s) => s.id === id)?.name ?? `hotel ${id}`),
            ...m.scopeApps.map((id) => apps.data?.find((a) => a.id === id)?.name ?? `app ${id}`),
        ].join(', ');

    return (
        <div className="space-y-6">
            <PageHeader
                title="Team"
                description="Who can use this panel. Everyone signs in with a password and an authenticator app; members can be limited to some hotels or apps."
                actions={<Button onClick={() => setEditing('new')}>Invite someone</Button>}
            />
            <ErrorNote message={[members, reset, remove].map((q) => q.error).find(Boolean) ? errorMessage([members.error, reset.error, remove.error].find(Boolean)) : null} />
            {inviteUrl && (
                <Notice tone="success">
                    Send this link to {inviteUrl.username} (it works once, for 3 days):{' '}
                    <code className="break-all">{inviteUrl.url}</code>{' '}
                    <button type="button" className="underline" onClick={() => void navigator.clipboard.writeText(inviteUrl.url)}>
                        Copy
                    </button>
                </Notice>
            )}
            {editing && (
                <MemberEditor
                    member={editing === 'new' ? null : editing}
                    apps={apps.data ?? []}
                    stacks={stacks.data ?? []}
                    onClose={() => setEditing(null)}
                    onInvited={(username, url) => setInviteUrl({ username, url })}
                />
            )}
            {members.isPending ? (
                <Spinner />
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Member</th>
                            <th>Role</th>
                            <th>Limited to</th>
                            <th>Last sign-in</th>
                            <th />
                        </tr>
                    </thead>
                    <tbody>
                        {members.data?.map((m) => (
                            <tr key={m.id}>
                                <td>
                                    <span className="font-medium">{m.username}</span>
                                    {m.invitePending && (
                                        <span className="ml-2">
                                            <Badge tone="warning">invite pending</Badge>
                                        </span>
                                    )}
                                    {m.invitedBy && <p className="text-xs text-slate-500">invited by {m.invitedBy}</p>}
                                </td>
                                <td>{m.role}</td>
                                <td className="text-sm text-slate-600 dark:text-slate-400">{scopeLabel(m) || 'Everything'}</td>
                                <td className="text-sm text-slate-600 dark:text-slate-400">
                                    {m.lastLoginAt ? formatRelative(m.lastLoginAt) : 'Never'}
                                    {m.activeSessions > 0 && <p className="text-xs">{m.activeSessions} signed-in browser(s)</p>}
                                </td>
                                <td className="whitespace-nowrap text-right">
                                    {m.role !== 'Owner' && (
                                        <>
                                            <Button size="sm" variant="ghost" onClick={() => setEditing(m)}>
                                                Edit
                                            </Button>
                                            <Button size="sm" variant="ghost" onClick={() => confirm(`Reset ${m.username}'s password and authenticator? They get a new link.`) && reset.mutate(m)}>
                                                Reset
                                            </Button>
                                            <Button size="sm" variant="ghost" onClick={() => confirm(`Remove ${m.username}?`) && remove.mutate(m)}>
                                                Remove
                                            </Button>
                                        </>
                                    )}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </TableWrap>
            )}

            <Card className="space-y-3 p-5">
                <h2 className="font-medium">Signed-in browsers</h2>
                {sessions.data?.length ? (
                    <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                        {sessions.data.map((s) => (
                            <li key={s.id} className="flex items-center justify-between gap-3 py-2">
                                <span>
                                    <span className="font-medium">{s.username}</span> · {s.ipAddress ?? 'unknown address'} · active {formatRelative(s.lastSeenAt)}
                                    <span className="block max-w-xl truncate text-xs text-slate-500">{s.userAgent}</span>
                                </span>
                                <Button size="sm" variant="ghost" onClick={() => revoke.mutate(s.id)}>
                                    Sign out
                                </Button>
                            </li>
                        ))}
                    </ul>
                ) : (
                    <p className="text-sm text-slate-500">None.</p>
                )}
            </Card>
        </div>
    );
}

function MemberEditor({
    member,
    apps,
    stacks,
    onClose,
    onInvited,
}: {
    member: Member | null;
    apps: AppSummary[];
    stacks: { id: number; name: string }[];
    onClose: () => void;
    onInvited: (username: string, url: string) => void;
}) {
    const queryClient = useQueryClient();
    const withSudo = useSudo();
    const [username, setUsername] = useState(member?.username ?? '');
    const [role, setRole] = useState(member?.role === 'Owner' ? 'Admin' : (member?.role ?? 'Developer'));
    const [scopeStacks, setScopeStacks] = useState<number[]>(member?.scopeStacks ?? []);
    const [scopeApps, setScopeApps] = useState<number[]>(member?.scopeApps ?? []);
    const limited = role !== 'Admin';
    const save = useMutation({
        mutationFn: () =>
            withSudo<{ inviteUrl: string } | void>(() =>
                member
                    ? api.put(`/api/team/${member.id}`, { role, scopeStacks: limited ? scopeStacks : [], scopeApps: limited ? scopeApps : [] })
                    : api.post<{ inviteUrl: string }>('/api/team', { username, role, scopeStacks: limited ? scopeStacks : [], scopeApps: limited ? scopeApps : [] }),
            ),
        onSuccess: (result) => {
            if (!member && result) onInvited(username, (result as { inviteUrl: string }).inviteUrl);
            void queryClient.invalidateQueries({ queryKey: ['team'] });
            onClose();
        },
    });
    const toggle = (list: number[], id: number) => (list.includes(id) ? list.filter((x) => x !== id) : [...list, id]);

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">{member ? `Edit ${member.username}` : 'Invite someone'}</h2>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate();
                }}
            >
                {!member && <Field label="Username" value={username} onChange={(e) => setUsername(e.target.value.toLowerCase())} required pattern="[a-z0-9][a-z0-9_.\-]{2,31}" />}
                <SelectField label="Role" value={role} onChange={(e) => setRole(e.target.value)} hint={roles.find((r) => r.id === role)?.description}>
                    {roles.map((r) => (
                        <option key={r.id} value={r.id}>
                            {r.id}
                        </option>
                    ))}
                </SelectField>
                {limited && (stacks.length > 0 || apps.length > 0) && (
                    <div className="space-y-2 text-sm">
                        <p className="font-medium">Limit to (nothing ticked = everything the role allows)</p>
                        <div className="flex flex-wrap gap-x-4 gap-y-1">
                            {stacks.map((s) => (
                                <label key={`s${s.id}`} className="flex items-center gap-2">
                                    <input type="checkbox" className="size-4 accent-brand-600" checked={scopeStacks.includes(s.id)} onChange={() => setScopeStacks(toggle(scopeStacks, s.id))} />
                                    Hotel {s.name}
                                </label>
                            ))}
                            {apps.map((a) => (
                                <label key={`a${a.id}`} className="flex items-center gap-2">
                                    <input type="checkbox" className="size-4 accent-brand-600" checked={scopeApps.includes(a.id)} onChange={() => setScopeApps(toggle(scopeApps, a.id))} />
                                    {a.name}
                                </label>
                            ))}
                        </div>
                    </div>
                )}
                <ErrorNote message={save.error && !(save.error instanceof Error && save.error.message === 'Cancelled.') ? errorMessage(save.error) : null} />
                <div className="flex gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={save.isPending}>
                        {member ? 'Save' : 'Create invite link'}
                    </Button>
                </div>
            </form>
        </Card>
    );
}
