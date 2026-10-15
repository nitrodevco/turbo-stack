import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type CloudflareOverview, type CloudflareZone, type FirewallCommitResult, type PermissionCheck, type ZoneSetting } from '../api';
import { usePermissions } from '../auth';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, Spinner } from '../components/ui';
import { useFirewallCommit } from '../firewall';
import { formatDateTime, formatRelative } from '../format';

/** What the token needs, shown as a checklist while creating it in Cloudflare's dashboard. */
const tokenPermissions = [
    { scope: 'Zone', permission: 'Zone', access: 'Read', why: 'List your domains', required: true },
    { scope: 'Zone', permission: 'DNS', access: 'Edit', why: 'Create records for your sites', required: true },
    { scope: 'Zone', permission: 'Zone Settings', access: 'Edit', why: 'SSL mode, WebSockets and other recommended settings', required: true },
    { scope: 'Zone', permission: 'SSL and Certificates', access: 'Edit', why: 'Origin certificates for this server', required: false },
    { scope: 'Zone', permission: 'Cache Purge', access: 'Purge', why: 'Clear cached files after deploys', required: false },
];

export function CloudflarePage() {
    const overview = useQuery({ queryKey: ['cloudflare'], queryFn: () => api.get<CloudflareOverview>('/api/cloudflare') });

    return (
        <div className="space-y-6">
            <PageHeader title="Cloudflare" description="Cloudflare sits in front of your sites: it hides this server's address, caches files and absorbs attacks. Connect it once and the panel manages DNS, certificates and settings for you." />
            <ErrorNote message={overview.error ? errorMessage(overview.error) : null} />
            {overview.isPending ? <Spinner /> : overview.data?.connected ? <Connected overview={overview.data} /> : <ConnectGuide />}
        </div>
    );
}

function ConnectGuide() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const [token, setToken] = useState('');
    const connect = useMutation({
        mutationFn: () => api.put<CloudflareOverview>('/api/cloudflare/token', { token }),
        onSuccess: (data) => queryClient.setQueryData(['cloudflare'], data),
    });

    return (
        <div className="grid gap-6 xl:grid-cols-2">
            <Card className="space-y-4 p-5">
                <h2 className="font-medium">1. Create an API token in Cloudflare</h2>
                <ol className="list-decimal space-y-2 pl-5 text-sm text-slate-700 dark:text-slate-300">
                    <li>
                        Open{' '}
                        <a href="https://dash.cloudflare.com/profile/api-tokens" target="_blank" rel="noreferrer" className="font-medium text-brand-700 underline dark:text-brand-100">
                            My Profile → API Tokens ↗
                        </a>{' '}
                        and click <strong>Create Token</strong>, then <strong>Create Custom Token</strong>.
                    </li>
                    <li>Name it, e.g. "Turbo Stack".</li>
                    <li>Add these permissions:</li>
                </ol>
                <div className="overflow-x-auto">
                    <table className="w-full text-left text-sm">
                        <thead className="text-xs text-slate-500 uppercase">
                            <tr>
                                <th className="py-1 pr-3">Permission</th>
                                <th className="py-1 pr-3">Access</th>
                                <th className="py-1">Used for</th>
                            </tr>
                        </thead>
                        <tbody>
                            {tokenPermissions.map((p) => (
                                <tr key={p.permission} className="border-t border-slate-100 dark:border-slate-800">
                                    <td className="py-1.5 pr-3 whitespace-nowrap">
                                        {p.scope} → <strong>{p.permission}</strong>
                                        {!p.required && <span className="ml-1 text-xs text-slate-500">(optional)</span>}
                                    </td>
                                    <td className="py-1.5 pr-3">{p.access}</td>
                                    <td className="py-1.5 text-slate-600 dark:text-slate-400">{p.why}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
                <ol start={4} className="list-decimal space-y-2 pl-5 text-sm text-slate-700 dark:text-slate-300">
                    <li>
                        Under <strong>Zone Resources</strong>, choose <em>Include → Specific zone → your domain</em> (or all zones).
                    </li>
                    <li>Optional: under Client IP Address Filtering, allow only this server's IP.</li>
                    <li>
                        Click <strong>Continue to summary</strong>, then <strong>Create Token</strong>, and copy it. Cloudflare shows it only once.
                    </li>
                </ol>
                <p className="text-xs text-slate-500">Use an API <em>token</em>, not the Global API Key: a token can only do what you allow here.</p>
            </Card>
            <Card className="h-fit space-y-4 p-5">
                <h2 className="font-medium">2. Paste it here</h2>
                <form
                    className="space-y-4"
                    onSubmit={(e) => {
                        e.preventDefault();
                        connect.mutate();
                    }}
                >
                    <Field label="API token" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" spellCheck={false} disabled={!can('sites.manage')} />
                    <ErrorNote message={connect.error ? errorMessage(connect.error) : null} />
                    <Button type="submit" disabled={!token.trim() || connect.isPending || !can('sites.manage')}>
                        {connect.isPending ? 'Checking the token…' : 'Connect'}
                    </Button>
                    <p className="text-xs text-slate-500">The panel tests each permission (it creates and immediately deletes a test DNS record) and stores the token encrypted.</p>
                </form>
            </Card>
        </div>
    );
}

function Connected({ overview }: { overview: Extract<CloudflareOverview, { connected: true }> }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const setData = (data: CloudflareOverview) => queryClient.setQueryData(['cloudflare'], data);
    const recheck = useMutation({ mutationFn: () => api.post<CloudflareOverview>('/api/cloudflare/recheck'), onSuccess: setData });
    const disconnect = useMutation({ mutationFn: () => api.delete('/api/cloudflare/token'), onSuccess: () => setData({ connected: false }) });
    const settings = useMutation({ mutationFn: (realIpEnabled: boolean) => api.put<CloudflareOverview>('/api/cloudflare/settings', { realIpEnabled }), onSuccess: setData });
    const cloudflareOnly = useFirewallCommit((enabled: boolean, acknowledgeLockout) => api.put<FirewallCommitResult>('/api/cloudflare/cloudflare-only', { enabled, acknowledgeLockout }));
    const missingRequired = overview.checks.filter((c) => c.required && c.status !== 'ok');

    return (
        <div className="space-y-6">
            <ErrorNote message={[recheck.error, disconnect.error, settings.error, cloudflareOnly.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            <Card className="space-y-4 p-5">
                <div className="flex flex-wrap items-start justify-between gap-3">
                    <div>
                        <p className="font-medium">
                            <Badge tone="good">● Connected</Badge> <span className="ml-2 text-sm text-slate-500">checked {formatRelative(overview.token.verifiedAt)}</span>
                        </p>
                        {overview.token.expiresOn && <p className="mt-1 text-sm text-slate-500">Token expires {formatDateTime(overview.token.expiresOn)}</p>}
                    </div>
                    {can('sites.manage') && (
                        <div className="flex gap-2">
                            <Button size="sm" variant="secondary" disabled={recheck.isPending} onClick={() => recheck.mutate()}>
                                {recheck.isPending ? 'Checking…' : 'Re-check permissions'}
                            </Button>
                            <Button size="sm" variant="ghost" onClick={() => confirm('Disconnect Cloudflare? DNS records and settings stay as they are.') && disconnect.mutate()}>
                                Disconnect
                            </Button>
                        </div>
                    )}
                </div>
                {missingRequired.length > 0 && (
                    <Notice tone="warning">⚠ The token is missing permissions some features need. Edit the token in Cloudflare, then re-check.</Notice>
                )}
                <ul className="grid gap-2 sm:grid-cols-2">
                    {overview.checks.map((check) => (
                        <CheckRow key={check.key} check={check} />
                    ))}
                </ul>
            </Card>

            <Card className="space-y-4 p-5">
                <h2 className="font-medium">Protection</h2>
                <label className="flex items-start gap-3 text-sm">
                    <input type="checkbox" className="mt-0.5 size-4 accent-brand-600" checked={overview.realIpEnabled} disabled={!can('sites.manage') || settings.isPending} onChange={(e) => settings.mutate(e.target.checked)} />
                    <span>
                        <strong>Restore visitor IP addresses.</strong> Without this, every visitor looks like a Cloudflare server to your apps (bans, logs, rate limits).
                        {overview.ipRangesUpdatedAt && <span className="block text-xs text-slate-500">Cloudflare's address ranges updated {formatRelative(overview.ipRangesUpdatedAt)}.</span>}
                    </span>
                </label>
                <label className="flex items-start gap-3 text-sm">
                    <input
                        type="checkbox"
                        className="mt-0.5 size-4 accent-brand-600"
                        checked={overview.cloudflareOnlyWeb}
                        disabled={!can('firewall.manage') || cloudflareOnly.isPending}
                        onChange={(e) => {
                            const enabled = e.target.checked;

                            if (!enabled || confirm('Only Cloudflare will be able to reach ports 80 and 443. Sites with DNS-only (grey cloud) records, and Let\'s Encrypt checks for them, stop working. Continue?')) {
                                cloudflareOnly.mutate(enabled);
                            }
                        }}
                    />
                    <span>
                        <strong>Only allow web traffic through Cloudflare.</strong> Blocks anyone who finds this server's IP from bypassing Cloudflare. The panel's setup port isn't affected.
                    </span>
                </label>
            </Card>

            <div className="space-y-3">
                <h2 className="font-medium">Domains</h2>
                {overview.zones.map((zone) => (
                    <ZoneCard key={zone.id} zone={zone} canEdit={can('sites.manage')} />
                ))}
            </div>
        </div>
    );
}

function CheckRow({ check }: { check: PermissionCheck }) {
    return (
        <li className="flex items-start gap-2 text-sm">
            {check.status === 'ok' ? <Badge tone="good">✓</Badge> : check.required ? <Badge tone="danger">✕</Badge> : <Badge tone="warning">⚠</Badge>}
            <span>
                <strong>{check.label}</strong>
                <span className="block text-xs text-slate-500">
                    {check.usedFor}
                    {check.status !== 'ok' && ` — ${check.status === 'missing' ? 'not granted' : (check.error ?? check.status)}`}
                </span>
            </span>
        </li>
    );
}

function ZoneCard({ zone, canEdit }: { zone: CloudflareZone; canEdit: boolean }) {
    const queryClient = useQueryClient();
    const [open, setOpen] = useState(false);
    const settings = useQuery({ queryKey: ['cloudflare', 'zone', zone.id], queryFn: () => api.get<ZoneSetting[]>(`/api/cloudflare/zones/${zone.id}/settings`), enabled: open });
    const preset = useMutation({
        mutationFn: () => api.post<ZoneSetting[]>(`/api/cloudflare/zones/${zone.id}/preset`),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['cloudflare', 'zone', zone.id] }),
    });
    const purge = useMutation({ mutationFn: () => api.post(`/api/cloudflare/zones/${zone.id}/purge`, { hosts: [] }) });
    const outdated = settings.data?.filter((s) => !s.ok).length ?? 0;
    const sslStrict = settings.data?.find((s) => s.setting === 'ssl');

    return (
        <Card className="space-y-3 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    <p className="font-medium">{zone.name}</p>
                    <p className="text-xs text-slate-500">
                        {zone.plan ?? 'Unknown plan'} · {zone.status}
                    </p>
                </div>
                <div className="flex gap-2">
                    <Button size="sm" variant="secondary" onClick={() => setOpen(!open)}>
                        {open ? 'Hide settings' : 'Settings'}
                    </Button>
                    {canEdit && (
                        <Button size="sm" variant="ghost" disabled={purge.isPending} onClick={() => confirm(`Purge everything cached for ${zone.name}?`) && purge.mutate()}>
                            {purge.isSuccess ? '✓ Purged' : 'Purge cache'}
                        </Button>
                    )}
                </div>
            </div>
            <ErrorNote message={purge.error ? errorMessage(purge.error) : settings.error ? errorMessage(settings.error) : preset.error ? errorMessage(preset.error) : null} />
            {open && settings.data && (
                <div className="space-y-3">
                    <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                        {(preset.data ?? settings.data).map((s) => (
                            <li key={s.setting} className="flex flex-wrap items-center justify-between gap-2 py-2">
                                <span>{s.label}</span>
                                <span className="flex items-center gap-2">
                                    <code className="text-xs">{s.current ?? '—'}</code>
                                    {s.ok ? <Badge tone="good">✓</Badge> : <Badge tone="warning">recommended: {s.recommended}</Badge>}
                                    {s.error && <span className="text-xs text-red-700 dark:text-red-300">{s.error}</span>}
                                </span>
                            </li>
                        ))}
                    </ul>
                    {sslStrict && !sslStrict.ok && (
                        <Notice tone="warning">
                            ⚠ "Full (strict)" makes Cloudflare check this server's certificate. Give your sites a Cloudflare Origin or Let's Encrypt certificate before applying, or they will show error 526.
                        </Notice>
                    )}
                    {canEdit && outdated > 0 && (
                        <Button size="sm" disabled={preset.isPending} onClick={() => preset.mutate()}>
                            Apply recommended settings
                        </Button>
                    )}
                </div>
            )}
        </Card>
    );
}
