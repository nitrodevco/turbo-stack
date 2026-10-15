import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import {
    api,
    errorMessage,
    type ExposureItem,
    type Fail2banJail,
    type Fail2banStatus,
    type FirewallCommitResult,
    type FirewallOverview,
    type FirewallRuleView,
    type IpListView,
} from '../api';
import { usePermissions } from '../auth';
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, Notice, PageHeader, SelectField, Spinner, TableWrap, Tabs } from '../components/ui';
import { firewallApi, useFirewallCommit } from '../firewall';

type Tab = 'rules' | 'lists' | 'exposure' | 'fail2ban';

export function FirewallPage() {
    const [tab, setTab] = useState<Tab>('rules');
    const overview = useQuery({ queryKey: ['firewall'], queryFn: () => api.get<FirewallOverview>('/api/firewall') });

    return (
        <div className="space-y-6">
            <PageHeader
                title="Firewall"
                description="Rules the panel manages are tagged in ufw; rules added by hand over SSH are left alone and shown separately."
            />
            {overview.data && <FirewallStatus overview={overview.data} />}
            <Tabs<Tab>
                tabs={[
                    { id: 'rules', label: 'Rules' },
                    { id: 'lists', label: 'IP lists' },
                    { id: 'exposure', label: 'Exposure check' },
                    { id: 'fail2ban', label: 'Brute-force protection' },
                ]}
                value={tab}
                onChange={setTab}
            />
            <ErrorNote message={overview.error ? errorMessage(overview.error) : null} />
            {tab === 'rules' && (overview.data ? <RulesTab overview={overview.data} /> : <Spinner />)}
            {tab === 'lists' && <IpListsTab />}
            {tab === 'exposure' && <ExposureTab />}
            {tab === 'fail2ban' && <Fail2banTab />}
        </div>
    );
}

function FirewallStatus({ overview }: { overview: FirewallOverview }) {
    const can = usePermissions();
    const commit = useFirewallCommit((input: { enabled: boolean; defaultIncoming: string }, acknowledgeLockout) =>
        api.put<FirewallCommitResult>('/api/firewall/settings', { ...input, acknowledgeLockout }),
    );
    const sync = useFirewallCommit(() => api.post<FirewallCommitResult>('/api/firewall/sync'));
    const { settings, actual } = overview;

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="flex flex-wrap items-center gap-3">
                    {actual.enabled ? <Badge tone="good">● Firewall on</Badge> : <Badge tone="danger">✕ Firewall off</Badge>}
                    <span className="text-sm text-muted">
                        Incoming traffic not matched by a rule is <strong>{actual.defaultIncoming === 'allow' ? 'allowed' : `${actual.defaultIncoming === 'reject' ? 'rejected' : 'dropped'}`}</strong>.
                    </span>
                </div>
                {can('firewall.manage') && (
                    <div className="flex flex-wrap gap-2">
                        <Button
                            variant={settings.enabled ? 'secondary' : 'primary'}
                            disabled={commit.isPending}
                            onClick={() => {
                                if (!settings.enabled || confirm('Turn the firewall off? Every listening port becomes reachable.')) {
                                    commit.mutate({ enabled: !settings.enabled, defaultIncoming: settings.defaultIncoming });
                                }
                            }}
                        >
                            {settings.enabled ? 'Turn off' : 'Turn on'}
                        </Button>
                        <select
                            aria-label="Default for incoming traffic"
                            className="rounded-lg border border-line bg-canvas px-3 py-2 text-sm"
                            value={settings.defaultIncoming}
                            disabled={commit.isPending}
                            onChange={(e) => commit.mutate({ enabled: settings.enabled, defaultIncoming: e.target.value })}
                        >
                            <option value="deny">Default: drop</option>
                            <option value="reject">Default: reject</option>
                            <option value="allow">Default: allow</option>
                        </select>
                    </div>
                )}
            </div>
            {!actual.available && <Notice tone="danger">ufw is not available on this server: {actual.error}</Notice>}
            {actual.available && !overview.inSync && !overview.pending && (
                <Notice tone="warning">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                        <span>The firewall differs from the panel's rules (it was probably changed by hand).</span>
                        {can('firewall.manage') && (
                            <Button size="sm" variant="secondary" disabled={sync.isPending} onClick={() => sync.mutate(undefined)}>
                                Re-apply panel rules
                            </Button>
                        )}
                    </div>
                </Notice>
            )}
            <ErrorNote message={commit.error ? errorMessage(commit.error) : sync.error ? errorMessage(sync.error) : null} />
        </Card>
    );
}

const actionTone = { allow: 'good', limit: 'info', deny: 'danger', reject: 'danger' } as const;

function RulesTab({ overview }: { overview: FirewallOverview }) {
    const can = usePermissions();
    const [editing, setEditing] = useState<FirewallRuleView | 'new' | null>(null);
    const remove = useFirewallCommit((rule: FirewallRuleView, acknowledgeLockout) => firewallApi.deleteRule(rule.id, acknowledgeLockout));

    return (
        <div className="space-y-4">
            {can('firewall.manage') && (
                <div className="flex justify-end">
                    <Button onClick={() => setEditing('new')}>Add rule</Button>
                </div>
            )}
            <ErrorNote message={remove.error ? errorMessage(remove.error) : null} />
            <TableWrap>
                <thead>
                    <tr>
                        <th>Name</th>
                        <th>Action</th>
                        <th>Ports</th>
                        <th>From</th>
                        <th>Status</th>
                        <th />
                    </tr>
                </thead>
                <tbody>
                    {overview.rules.map((rule) => (
                        <tr key={rule.id} className={rule.enabled ? '' : 'opacity-60'}>
                            <td>
                                <p className="font-medium">{rule.name}</p>
                                {rule.system && <p className="text-xs text-muted">Created by the installer</p>}
                            </td>
                            <td>
                                <Badge tone={actionTone[rule.action]}>{rule.action === 'limit' ? 'allow (rate-limited)' : rule.action}</Badge>
                            </td>
                            <td className="font-mono text-xs">
                                {rule.ports}/{rule.protocol}
                            </td>
                            <td>{rule.sourceKind === 'Any' ? 'Anywhere' : rule.sourceKind === 'IpList' ? `List: ${rule.ipListName}` : rule.sourceAddress}</td>
                            <td>
                                {rule.status === 'applied' && <Badge tone="good">✓ Applied</Badge>}
                                {rule.status === 'not-applied' && <Badge tone="warning">⚠ Not applied</Badge>}
                                {rule.status === 'disabled' && <Badge>Disabled</Badge>}
                            </td>
                            <td>
                                {can('firewall.manage') && (
                                    <div className="flex justify-end gap-1">
                                        <Button size="sm" variant="ghost" onClick={() => setEditing(rule)}>
                                            Edit
                                        </Button>
                                        <Button
                                            size="sm"
                                            variant="ghost"
                                            disabled={remove.isPending}
                                            onClick={() => {
                                                const warning = rule.system ? `\n\n"${rule.name}" was created by the installer.` : '';

                                                if (confirm(`Delete the rule "${rule.name}"?${warning}`)) remove.mutate(rule);
                                            }}
                                        >
                                            Delete
                                        </Button>
                                    </div>
                                )}
                            </td>
                        </tr>
                    ))}
                </tbody>
            </TableWrap>
            {overview.external.length > 0 && (
                <div className="space-y-2">
                    <h3 className="text-sm font-medium text-muted">Other rules in ufw (not managed by the panel)</h3>
                    <TableWrap>
                        <tbody>
                            {overview.external.map((rule, i) => (
                                <tr key={i}>
                                    <td>
                                        <Badge tone={actionTone[rule.action as keyof typeof actionTone] ?? 'neutral'}>{rule.action}</Badge>
                                    </td>
                                    <td className="font-mono text-xs">
                                        {rule.ports}/{rule.protocol}
                                    </td>
                                    <td>{rule.source === 'any' ? 'Anywhere' : rule.source}</td>
                                    <td className="text-muted">
                                        {rule.direction}
                                        {rule.ipv6Only ? ' · IPv6' : ''}
                                        {rule.comment ? ` · ${rule.comment}` : ''}
                                    </td>
                                </tr>
                            ))}
                        </tbody>
                    </TableWrap>
                </div>
            )}
            {editing && <RuleModal rule={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
        </div>
    );
}

function RuleModal({ rule, onClose }: { rule: FirewallRuleView | null; onClose: () => void }) {
    const lists = useQuery({ queryKey: ['firewall', 'ip-lists'], queryFn: () => api.get<IpListView[]>('/api/firewall/ip-lists') });
    const [form, setForm] = useState({
        name: rule?.name ?? '',
        action: rule?.action ?? 'allow',
        protocol: rule?.protocol ?? 'tcp',
        ports: rule?.ports ?? '',
        sourceKind: rule?.sourceKind ?? 'Any',
        sourceAddress: rule?.sourceAddress ?? '',
        ipListId: rule?.ipListId ?? null,
        enabled: rule?.enabled ?? true,
    });
    const save = useFirewallCommit((_: void, acknowledgeLockout) => {
        const body = { ...form, acknowledgeLockout };

        return rule ? api.put<FirewallCommitResult>(`/api/firewall/rules/${rule.id}`, body) : api.post<FirewallCommitResult>('/api/firewall/rules', body);
    });
    const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm({ ...form, [key]: value });

    return (
        <Modal open title={rule ? `Edit "${rule.name}"` : 'Add firewall rule'} onClose={onClose}>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate(undefined, { onSuccess: (result) => result && onClose() });
                }}
            >
                <Field label="Name" value={form.name} onChange={(e) => set('name', e.target.value)} placeholder="e.g. Game server (Flash clients)" required />
                <div className="grid grid-cols-2 gap-3">
                    <SelectField label="Action" value={form.action} onChange={(e) => set('action', e.target.value as typeof form.action)}>
                        <option value="allow">Allow</option>
                        <option value="limit">Allow, rate-limited</option>
                        <option value="deny">Deny (drop)</option>
                        <option value="reject">Reject</option>
                    </SelectField>
                    <SelectField label="Protocol" value={form.protocol} onChange={(e) => set('protocol', e.target.value as typeof form.protocol)}>
                        <option value="tcp">TCP</option>
                        <option value="udp">UDP</option>
                        <option value="any">Any</option>
                    </SelectField>
                </div>
                <Field label="Ports" value={form.ports} onChange={(e) => set('ports', e.target.value)} placeholder="22 · 80,443 · 6000:6010" hint="A port, a list, or a range." required />
                <SelectField label="From" value={form.sourceKind} onChange={(e) => set('sourceKind', e.target.value as typeof form.sourceKind)}>
                    <option value="Any">Anywhere</option>
                    <option value="Address">An IP address or network</option>
                    <option value="IpList">An IP list</option>
                </SelectField>
                {form.sourceKind === 'Address' && (
                    <Field label="Address" value={form.sourceAddress} onChange={(e) => set('sourceAddress', e.target.value)} placeholder="203.0.113.7 or 203.0.113.0/24" required />
                )}
                {form.sourceKind === 'IpList' && (
                    <SelectField label="IP list" value={form.ipListId ?? ''} onChange={(e) => set('ipListId', e.target.value ? Number(e.target.value) : null)} required>
                        <option value="">Choose a list…</option>
                        {(lists.data ?? []).map((list) => (
                            <option key={list.id} value={list.id}>
                                {list.name} ({list.entries.length})
                            </option>
                        ))}
                    </SelectField>
                )}
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={form.enabled} onChange={(e) => set('enabled', e.target.checked)} />
                    Enabled
                </label>
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={save.isPending}>
                        {save.isPending ? 'Applying…' : 'Save and apply'}
                    </Button>
                </div>
            </form>
        </Modal>
    );
}

function IpListsTab() {
    const can = usePermissions();
    const lists = useQuery({ queryKey: ['firewall', 'ip-lists'], queryFn: () => api.get<IpListView[]>('/api/firewall/ip-lists') });
    const [editing, setEditing] = useState<IpListView | 'new' | null>(null);
    const remove = useFirewallCommit((list: IpListView) => api.delete<FirewallCommitResult>(`/api/firewall/ip-lists/${list.id}`));

    return (
        <div className="space-y-4">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <p className="text-sm text-muted">Named address lists you can use in rules, e.g. "Staff IPs". Editing a list updates every rule that uses it.</p>
                {can('firewall.manage') && <Button onClick={() => setEditing('new')}>New list</Button>}
            </div>
            <ErrorNote message={remove.error ? errorMessage(remove.error) : lists.error ? errorMessage(lists.error) : null} />
            {lists.data?.length === 0 && <p className="text-sm text-muted">No IP lists yet.</p>}
            <div className="grid gap-4 md:grid-cols-2">
                {(lists.data ?? []).map((list) => (
                    <Card key={list.id} className="space-y-3 p-5">
                        <div className="flex items-start justify-between gap-2">
                            <div>
                                <p className="font-medium">{list.name}</p>
                                {list.description && <p className="text-sm text-muted">{list.description}</p>}
                            </div>
                            {can('firewall.manage') && (
                                <div className="flex gap-1">
                                    <Button size="sm" variant="ghost" onClick={() => setEditing(list)}>
                                        Edit
                                    </Button>
                                    <Button size="sm" variant="ghost" disabled={list.usedBy.length > 0} title={list.usedBy.length ? `Used by ${list.usedBy.join(', ')}` : undefined} onClick={() => confirm(`Delete "${list.name}"?`) && remove.mutate(list)}>
                                        Delete
                                    </Button>
                                </div>
                            )}
                        </div>
                        <ul className="space-y-1 font-mono text-xs">
                            {list.entries.map((entry) => (
                                <li key={entry.source}>
                                    {entry.source}
                                    {entry.note && <span className="font-sans text-muted"> — {entry.note}</span>}
                                </li>
                            ))}
                        </ul>
                        {list.usedBy.length > 0 && <p className="text-xs text-muted">Used by: {list.usedBy.join(', ')}</p>}
                    </Card>
                ))}
            </div>
            {editing && <IpListModal list={editing === 'new' ? null : editing} onClose={() => setEditing(null)} />}
        </div>
    );
}

function IpListModal({ list, onClose }: { list: IpListView | null; onClose: () => void }) {
    const queryClient = useQueryClient();
    const [name, setName] = useState(list?.name ?? '');
    const [description, setDescription] = useState(list?.description ?? '');
    const [entries, setEntries] = useState((list?.entries ?? []).map((e) => (e.note ? `${e.source} # ${e.note}` : e.source)).join('\n'));
    const save = useFirewallCommit((_: void, acknowledgeLockout) => {
        const body = {
            name,
            description,
            acknowledgeLockout,
            entries: entries
                .split('\n')
                .map((line) => line.trim())
                .filter(Boolean)
                .map((line) => {
                    const [source, ...note] = line.split('#');

                    return { source: source.trim(), note: note.join('#').trim() || null };
                }),
        };

        return list ? api.put<FirewallCommitResult>(`/api/firewall/ip-lists/${list.id}`, body) : api.post<FirewallCommitResult>('/api/firewall/ip-lists', body);
    });

    return (
        <Modal open title={list ? `Edit "${list.name}"` : 'New IP list'} onClose={onClose}>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate(undefined, {
                        onSuccess: (result) => {
                            if (!result) return;
                            void queryClient.invalidateQueries({ queryKey: ['firewall', 'ip-lists'] });
                            onClose();
                        },
                    });
                }}
            >
                <Field label="Name" value={name} onChange={(e) => setName(e.target.value)} required />
                <Field label="Description" value={description} onChange={(e) => setDescription(e.target.value)} />
                <label className="block space-y-1.5">
                    <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Addresses</span>
                    <textarea
                        className="block min-h-40 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-sm"
                        value={entries}
                        onChange={(e) => setEntries(e.target.value)}
                        placeholder={'203.0.113.7 # Bill (home)\n198.51.100.0/24 # Office'}
                    />
                    <span className="block text-xs text-muted">One address or network per line. Add a note after #.</span>
                </label>
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button type="button" variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button type="submit" disabled={save.isPending}>
                        {save.isPending ? 'Saving…' : 'Save'}
                    </Button>
                </div>
            </form>
        </Modal>
    );
}

const exposureTone = { local: 'neutral', blocked: 'good', restricted: 'info', public: 'warning' } as const;

function ExposureTab() {
    const items = useQuery({ queryKey: ['firewall', 'exposure'], queryFn: () => api.get<ExposureItem[]>('/api/firewall/exposure') });
    const dangers = items.data?.filter((i) => i.risk === 'danger').length ?? 0;

    return (
        <div className="space-y-4">
            <p className="text-sm text-muted">Every port a program is listening on, compared with the firewall: what can the internet actually reach?</p>
            {dangers > 0 && <Notice tone="danger">✕ {dangers} sensitive {dangers === 1 ? 'port is' : 'ports are'} reachable from the internet.</Notice>}
            <ErrorNote message={items.error ? errorMessage(items.error) : null} />
            {items.isPending ? (
                <Spinner />
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Port</th>
                            <th>Program</th>
                            <th>Listening on</th>
                            <th>Reachable</th>
                            <th>Notes</th>
                        </tr>
                    </thead>
                    <tbody>
                        {(items.data ?? []).map((item) => (
                            <tr key={`${item.protocol}-${item.address}-${item.port}`}>
                                <td className="font-mono text-xs">
                                    {item.port}/{item.protocol}
                                </td>
                                <td>{item.process || '—'}</td>
                                <td className="font-mono text-xs">{item.address}</td>
                                <td>
                                    {item.risk === 'danger' ? (
                                        <Badge tone="danger">✕ {item.exposure}</Badge>
                                    ) : item.risk === 'warning' ? (
                                        <Badge tone="warning">⚠ {item.exposure}</Badge>
                                    ) : (
                                        <Badge tone={exposureTone[item.exposure]}>{item.exposure}</Badge>
                                    )}
                                </td>
                                <td className="text-muted">{item.note}</td>
                            </tr>
                        ))}
                    </tbody>
                </TableWrap>
            )}
        </div>
    );
}

function Fail2banTab() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const status = useQuery({ queryKey: ['firewall', 'fail2ban'], queryFn: () => api.get<Fail2banStatus>('/api/firewall/fail2ban'), refetchInterval: 30_000 });
    const ban = useMutation({
        mutationFn: ({ jail, ip, unban }: { jail: string; ip: string; unban: boolean }) => api.post<Fail2banStatus>(`/api/firewall/fail2ban/${jail}/${unban ? 'unban' : 'ban'}`, { ip }),
        onSuccess: (data) => queryClient.setQueryData(['firewall', 'fail2ban'], data),
    });
    const [editing, setEditing] = useState<Fail2banJail | null>(null);

    if (status.isPending) return <Spinner />;

    return (
        <div className="space-y-4">
            <p className="text-sm text-muted">
                fail2ban bans addresses that keep failing to sign in (SSH and this panel). Banned addresses are blocked for the ban time.
            </p>
            <ErrorNote message={status.error ? errorMessage(status.error) : ban.error ? errorMessage(ban.error) : null} />
            {status.data && !status.data.available && <Notice tone="warning">fail2ban is not running: {status.data.error}</Notice>}
            {(status.data?.jails ?? []).map((jail) => (
                <Card key={jail.name} className="space-y-4 p-5">
                    <div className="flex flex-wrap items-start justify-between gap-3">
                        <div>
                            <p className="font-medium">{jail.name === 'sshd' ? 'SSH' : jail.name === 'turbo-stack-panel' ? 'Panel sign-in' : jail.name}</p>
                            <p className="text-sm text-muted">
                                Ban for {formatSeconds(jail.banTimeSeconds)} after {jail.maxRetry} failures within {formatSeconds(jail.findTimeSeconds)} · {jail.totalBanned} banned so far
                            </p>
                        </div>
                        {can('firewall.manage') && (
                            <Button size="sm" variant="secondary" onClick={() => setEditing(jail)}>
                                Settings
                            </Button>
                        )}
                    </div>
                    {jail.bannedIps.length === 0 ? (
                        <p className="text-sm text-muted">Nobody is banned right now.</p>
                    ) : (
                        <ul className="flex flex-wrap gap-2">
                            {jail.bannedIps.map((ip) => (
                                <li key={ip} className="flex items-center gap-2 rounded-lg bg-slate-100 py-1 pr-1 pl-3 font-mono text-xs dark:bg-slate-800">
                                    {ip}
                                    {can('firewall.manage') && (
                                        <Button size="sm" variant="ghost" onClick={() => ban.mutate({ jail: jail.name, ip, unban: true })}>
                                            Unban
                                        </Button>
                                    )}
                                </li>
                            ))}
                        </ul>
                    )}
                    {can('firewall.manage') && <BanForm onBan={(ip) => ban.mutate({ jail: jail.name, ip, unban: false })} />}
                </Card>
            ))}
            {editing && <JailModal jail={editing} onClose={() => setEditing(null)} />}
        </div>
    );
}

function BanForm({ onBan }: { onBan: (ip: string) => void }) {
    const [ip, setIp] = useState('');

    return (
        <form
            className="flex gap-2"
            onSubmit={(e) => {
                e.preventDefault();
                onBan(ip.trim());
                setIp('');
            }}
        >
            <Input value={ip} onChange={(e) => setIp(e.target.value)} placeholder="Ban an IP address" className="max-w-xs" />
            <Button type="submit" variant="secondary" disabled={!ip.trim()}>
                Ban
            </Button>
        </form>
    );
}

function JailModal({ jail, onClose }: { jail: Fail2banJail; onClose: () => void }) {
    const queryClient = useQueryClient();
    const [form, setForm] = useState({
        banMinutes: Math.round(jail.banTimeSeconds / 60),
        findMinutes: Math.round(jail.findTimeSeconds / 60),
        maxRetry: jail.maxRetry,
        ignoreIps: jail.ignoreIps.filter((ip) => !['127.0.0.1/8', '::1'].includes(ip)).join('\n'),
    });
    const save = useMutation({
        mutationFn: () =>
            api.put<Fail2banStatus>(`/api/firewall/fail2ban/${jail.name}`, {
                banTimeSeconds: form.banMinutes * 60,
                findTimeSeconds: form.findMinutes * 60,
                maxRetry: form.maxRetry,
                ignoreIps: form.ignoreIps.split('\n').map((s) => s.trim()).filter(Boolean),
            }),
        onSuccess: (data) => {
            queryClient.setQueryData(['firewall', 'fail2ban'], data);
            onClose();
        },
    });

    return (
        <Modal open title={`Settings: ${jail.name}`} onClose={onClose}>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    save.mutate();
                }}
            >
                <div className="grid grid-cols-3 gap-3">
                    <Field label="Ban (minutes)" type="number" min={1} value={form.banMinutes} onChange={(e) => setForm({ ...form, banMinutes: Number(e.target.value) })} />
                    <Field label="Window (minutes)" type="number" min={1} value={form.findMinutes} onChange={(e) => setForm({ ...form, findMinutes: Number(e.target.value) })} />
                    <Field label="Max failures" type="number" min={1} value={form.maxRetry} onChange={(e) => setForm({ ...form, maxRetry: Number(e.target.value) })} />
                </div>
                <label className="block space-y-1.5">
                    <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Never ban</span>
                    <textarea
                        className="block min-h-24 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-sm"
                        value={form.ignoreIps}
                        onChange={(e) => setForm({ ...form, ignoreIps: e.target.value })}
                        placeholder="203.0.113.7"
                    />
                    <span className="block text-xs text-muted">One address or network per line. This server itself is always excluded.</span>
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

function formatSeconds(seconds: number) {
    if (seconds >= 86400) return `${Math.round(seconds / 86400)} d`;
    if (seconds >= 3600) return `${Math.round(seconds / 3600)} h`;

    return `${Math.round(seconds / 60)} min`;
}
