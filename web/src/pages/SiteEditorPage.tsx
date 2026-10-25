import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router';
import { api, errorMessage, type CloudflareOverview, type DnsActionView, type DnsCheck, type RouteKind, type SiteApplyResponse, type SiteDetail, type SiteModel, type SiteRouteModel } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Input, Modal, Notice, PageHeader, Select, SelectField, Spinner, Tabs } from '../components/ui';
import { formatDateTime } from '../format';
import { useAppsAndSites } from '../siteLinks';
import { AppChip } from './AppsPage';

const routeKinds: { value: RouteKind; label: string; placeholder: string; hint: string }[] = [
    { value: 'Spa', label: 'Single-page app', placeholder: '/var/www/site', hint: 'Unknown paths load index.html (React/Vite apps).' },
    { value: 'Static', label: 'Files', placeholder: '/var/www/site', hint: 'Serves files from a directory.' },
    { value: 'Proxy', label: 'Proxy', placeholder: 'http://127.0.0.1:8080', hint: 'Forwards requests to an app on this server.' },
    { value: 'WebSocket', label: 'WebSocket', placeholder: 'http://127.0.0.1:9001', hint: 'Proxy with WebSocket upgrade and long timeouts.' },
    { value: 'Redirect', label: 'Redirect', placeholder: 'https://example.com', hint: 'Permanent redirect, keeping the path.' },
];

const emptyRoute = (): SiteRouteModel => ({ path: '/', kind: 'Spa', target: '', cors: false, cacheSeconds: null, snippet: null });

const newModel = (): SiteModel => ({
    name: '',
    domains: [],
    routes: [emptyRoute()],
    tlsMode: 'LetsEncrypt',
    forceHttps: true,
    hsts: false,
    securityHeaders: true,
    clientMaxBodyMb: 10,
    serverSnippet: null,
    customConfig: null,
    enabled: true,
    cloudflareDns: false,
    cloudflareProxied: true,
});

function slugify(domain: string) {
    return domain.toLowerCase().replace(/\./g, '-').replace(/[^a-z0-9-]/g, '').slice(0, 40).replace(/^-+|-+$/g, '');
}

export function SiteEditorPage() {
    const { id } = useParams();
    const isNew = id === undefined;
    const detail = useQuery({ queryKey: ['site', id], queryFn: () => api.get<SiteDetail>(`/api/sites/${id}`), enabled: !isNew });

    if (!isNew && !detail.data) {
        return detail.error ? <ErrorNote message={errorMessage(detail.error)} /> : <Spinner />;
    }

    return <SiteEditor key={id ?? 'new'} siteId={isNew ? null : Number(id)} detail={detail.data ?? null} />;
}

function SiteEditor({ siteId, detail }: { siteId: number | null; detail: SiteDetail | null }) {
    const can = usePermissions();
    const editable = can('sites.manage');
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [params] = useSearchParams();
    // "Add a site" for an app pre-fills its name and the route to its files or port.
    const [model, setModel] = useState<SiteModel>(() => {
        if (detail) return detail.model;

        const initial = { ...newModel(), name: params.get('name') ?? '' };
        const target = params.get('target');

        return target ? { ...initial, routes: [{ ...initial.routes[0], target, kind: (params.get('kind') as RouteKind) ?? 'Spa' }] } : initial;
    });
    const { sites, appsOf } = useAppsAndSites();
    const saved = sites.data?.find((s) => s.id === siteId);
    const servedApps = saved ? appsOf(saved) : [];
    const [domainsText, setDomainsText] = useState((detail?.model.domains ?? []).join('\n'));
    const [tab, setTab] = useState<'preview' | 'dns' | 'history'>('preview');
    const [result, setResult] = useState<SiteApplyResponse | null>(null);
    const cloudflare = useQuery({ queryKey: ['cloudflare'], queryFn: () => api.get<CloudflareOverview>('/api/cloudflare') });

    const domains = domainsText.split(/[\s,]+/).map((d) => d.trim().toLowerCase()).filter(Boolean);
    const connected = cloudflare.data?.connected ? cloudflare.data : null;
    const zone = connected?.zones.filter((z) => domains[0] === z.name || domains[0]?.endsWith('.' + z.name)).sort((a, b) => b.name.length - a.name.length)[0] ?? null;
    const canIssueOrigin = connected?.checks.some((c) => c.key === 'ssl.edit' && c.status === 'ok') ?? false;
    const current: SiteModel ={ ...model, domains, name: siteId ? model.name : model.name || slugify(domains[0] ?? '') };
    const set = <K extends keyof SiteModel>(key: K, value: SiteModel[K]) => setModel({ ...model, [key]: value });
    const setRoute = (index: number, route: Partial<SiteRouteModel>) =>
        set('routes', model.routes.map((r, i) => (i === index ? { ...r, ...route } : r)));

    const save = useMutation({
        mutationFn: () => (siteId ? api.put<SiteApplyResponse>(`/api/sites/${siteId}`, current) : api.post<SiteApplyResponse>('/api/sites', current)),
        onSuccess: (response) => {
            setResult(response);
            void queryClient.invalidateQueries({ queryKey: ['sites'] });
            void queryClient.invalidateQueries({ queryKey: ['site'] });

            if (!siteId) {
                void navigate(`/sites/${response.id}`, { replace: true, state: { result: response } });
            }
        },
    });
    const remove = useMutation({
        mutationFn: () => api.delete(`/api/sites/${siteId}`),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['sites'] });
            void navigate('/apps');
        },
    });

    return (
        <div className="space-y-6">
            <PageHeader
                title={siteId ? (detail!.model.domains[0] ?? model.name) : 'Add site'}
                description={siteId ? `Config file ts-${model.name}.conf` : 'Serve one or more domains from this server: an app, a folder, a proxy or a redirect.'}
                actions={
                    <Link to="/apps" className="rounded-lg px-4 py-2 text-sm font-medium text-muted hover:bg-subtle hover:text-ink">
                        ← Apps & sites
                    </Link>
                }
            />
            {servedApps.length > 0 && (
                <div className="flex flex-wrap items-center gap-2 text-sm text-muted">
                    Serves
                    {servedApps.map((app) => (
                        <AppChip key={app.id} app={app} />
                    ))}
                </div>
            )}
            {detail?.lastError && !result && (
                <Notice tone="danger">
                    <p className="font-medium">The last change was not applied; nginx is still running the previous version.</p>
                    <pre className="mt-2 text-xs whitespace-pre-wrap">{detail.lastError}</pre>
                </Notice>
            )}
            {result && <ApplyResult result={result} siteId={siteId ?? result.id} />}
            <div className="grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
                <form
                    className="space-y-6"
                    onSubmit={(e) => {
                        e.preventDefault();
                        setResult(null);
                        save.mutate();
                    }}
                >
                    <fieldset disabled={!editable} className="space-y-6">
                        <Card className="space-y-4 p-5">
                            <label className="block space-y-1.5">
                                <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Domains</span>
                                <textarea
                                    className="block min-h-20 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-sm"
                                    value={domainsText}
                                    onChange={(e) => setDomainsText(e.target.value)}
                                    placeholder={'play.myhotel.com'}
                                    required
                                />
                                <span className="block text-xs text-muted">One per line; the first is the main domain. Point their DNS at this server.</span>
                            </label>
                            {!siteId && (
                                <Field
                                    label="Name"
                                    value={model.name}
                                    placeholder={slugify(domains[0] ?? '') || 'play-myhotel-com'}
                                    onChange={(e) => set('name', e.target.value.toLowerCase())}
                                    hint="Used for the config and log file names. Can't be changed later."
                                />
                            )}
                            <SelectField label="HTTPS" value={model.tlsMode} onChange={(e) => set('tlsMode', e.target.value as SiteModel['tlsMode'])}>
                                <option value="LetsEncrypt">Let's Encrypt (free, automatic renewal)</option>
                                <option value="SelfSigned">Self-signed (testing, or behind a proxy)</option>
                                <option value="CloudflareOrigin" disabled={!zone || !canIssueOrigin}>
                                    Cloudflare Origin certificate{!zone ? ' (domain not in a connected Cloudflare zone)' : !canIssueOrigin ? ' (token lacks SSL permission)' : ' (for proxied sites)'}
                                </option>
                                <option value="None">None (HTTP only)</option>
                            </SelectField>
                            {zone && (
                                <div className="space-y-2 rounded-lg border border-slate-200 p-3 text-sm dark:border-slate-800">
                                    <p className="font-medium">Cloudflare · {zone.name}</p>
                                    <Toggle label="Create and update DNS records for these domains" checked={model.cloudflareDns} onChange={(v) => set('cloudflareDns', v)} />
                                    {model.cloudflareDns && (
                                        <Toggle label="Proxy through Cloudflare (orange cloud: hides this server, caches files)" checked={model.cloudflareProxied} onChange={(v) => set('cloudflareProxied', v)} />
                                    )}
                                </div>
                            )}
                        </Card>

                        {model.customConfig === null ? (
                            <Card className="space-y-4 p-5">
                                <div className="flex items-center justify-between">
                                    <h2 className="font-medium">Routes</h2>
                                    <Button type="button" size="sm" variant="secondary" onClick={() => set('routes', [...model.routes, { ...emptyRoute(), path: '/api/', kind: 'Proxy' }])}>
                                        Add route
                                    </Button>
                                </div>
                                {model.routes.map((route, index) => {
                                    const kind = routeKinds.find((k) => k.value === route.kind)!;

                                    return (
                                        <div key={index} className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
                                            <div className="grid gap-2 sm:grid-cols-[7rem_10rem_1fr_auto]">
                                                <Input aria-label="Path" value={route.path} onChange={(e) => setRoute(index, { path: e.target.value })} placeholder="/" />
                                                <Select aria-label="Type" value={route.kind} onChange={(e) => setRoute(index, { kind: e.target.value as RouteKind })}>
                                                    {routeKinds.map((k) => (
                                                        <option key={k.value} value={k.value}>
                                                            {k.label}
                                                        </option>
                                                    ))}
                                                </Select>
                                                <Input aria-label="Target" value={route.target} onChange={(e) => setRoute(index, { target: e.target.value })} placeholder={kind.placeholder} required />
                                                <Button type="button" size="sm" variant="ghost" disabled={model.routes.length === 1} onClick={() => set('routes', model.routes.filter((_, i) => i !== index))}>
                                                    Remove
                                                </Button>
                                            </div>
                                            <p className="text-xs text-muted">{kind.hint}</p>
                                            <details className="text-sm">
                                                <summary className="cursor-pointer text-xs text-muted">Options</summary>
                                                <div className="mt-3 space-y-3">
                                                    {(route.kind === 'Static' || route.kind === 'Spa') && (
                                                        <div className="flex flex-wrap items-center gap-4">
                                                            <label className="flex items-center gap-2">
                                                                <input type="checkbox" className="size-4 accent-brand-600" checked={route.cors} onChange={(e) => setRoute(index, { cors: e.target.checked })} />
                                                                Allow other sites to load these files (CORS)
                                                            </label>
                                                            <label className="flex items-center gap-2">
                                                                Browser cache
                                                                <Select className="w-auto" value={route.cacheSeconds ?? ''} onChange={(e) => setRoute(index, { cacheSeconds: e.target.value === '' ? null : Number(e.target.value) })}>
                                                                    <option value="">Default</option>
                                                                    <option value="0">Never</option>
                                                                    <option value="3600">1 hour</option>
                                                                    <option value="86400">1 day</option>
                                                                    <option value="31536000">1 year</option>
                                                                </Select>
                                                            </label>
                                                        </div>
                                                    )}
                                                    <textarea
                                                        className="block min-h-16 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-xs"
                                                        placeholder="Extra nginx directives for this route"
                                                        value={route.snippet ?? ''}
                                                        onChange={(e) => setRoute(index, { snippet: e.target.value || null })}
                                                    />
                                                </div>
                                            </details>
                                        </div>
                                    );
                                })}
                            </Card>
                        ) : (
                            <Card className="space-y-3 p-5">
                                <h2 className="font-medium">Custom config</h2>
                                <p className="text-sm text-muted">This site uses your config verbatim. It is still checked for unsafe directives and by nginx before it goes live.</p>
                                <textarea
                                    className="block min-h-96 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-xs"
                                    value={model.customConfig}
                                    onChange={(e) => set('customConfig', e.target.value)}
                                />
                                <Button type="button" size="sm" variant="secondary" onClick={() => set('customConfig', null)}>
                                    Go back to generated config
                                </Button>
                            </Card>
                        )}

                        <Card className="p-5">
                            <details>
                                <summary className="cursor-pointer font-medium">Advanced</summary>
                                <div className="mt-4 space-y-4 text-sm">
                                    <Toggle label="Redirect HTTP to HTTPS" checked={model.forceHttps} onChange={(v) => set('forceHttps', v)} />
                                    <Toggle label="HSTS (browsers remember to always use HTTPS)" checked={model.hsts} onChange={(v) => set('hsts', v)} />
                                    <Toggle label="Security headers (nosniff, referrer policy)" checked={model.securityHeaders} onChange={(v) => set('securityHeaders', v)} />
                                    <Toggle label="Site enabled" checked={model.enabled} onChange={(v) => set('enabled', v)} />
                                    <Field label="Upload limit (MB)" type="number" min={1} max={2048} value={model.clientMaxBodyMb} onChange={(e) => set('clientMaxBodyMb', Number(e.target.value))} />
                                    <label className="block space-y-1.5">
                                        <span className="font-medium text-slate-700 dark:text-slate-300">Extra server directives</span>
                                        <textarea
                                            className="block min-h-20 w-full rounded-lg border border-line bg-canvas px-3 py-2 font-mono text-xs"
                                            value={model.serverSnippet ?? ''}
                                            onChange={(e) => set('serverSnippet', e.target.value || null)}
                                            placeholder='add_header Content-Security-Policy "default-src &apos;self&apos;" always;'
                                        />
                                    </label>
                                    {model.customConfig === null && (
                                        <CustomConfigButton model={current} onEject={(config) => set('customConfig', config)} />
                                    )}
                                </div>
                            </details>
                        </Card>
                    </fieldset>

                    <ErrorNote message={save.error ? errorMessage(save.error) : remove.error ? errorMessage(remove.error) : null} />
                    {editable && (
                        <div className="flex flex-wrap justify-between gap-2">
                            <Button type="submit" disabled={save.isPending}>
                                {save.isPending ? 'Applying…' : siteId ? 'Save and apply' : 'Create site'}
                            </Button>
                            {siteId && !detail?.system && (
                                <Button type="button" variant="danger" disabled={remove.isPending} onClick={() => confirm(`Delete ${domains[0]}? nginx stops serving it.`) && remove.mutate()}>
                                    Delete site
                                </Button>
                            )}
                        </div>
                    )}
                </form>

                <div className="space-y-4">
                    <Tabs
                        tabs={[
                            { id: 'preview' as const, label: 'Config preview' },
                            ...(siteId ? [{ id: 'dns' as const, label: 'DNS' }, { id: 'history' as const, label: 'History' }] : []),
                        ]}
                        value={tab}
                        onChange={setTab}
                    />
                    {tab === 'preview' && <Preview model={current} enabled={editable} />}
                    {tab === 'dns' && siteId && <DnsPanel siteId={siteId} />}
                    {tab === 'history' && detail && <HistoryPanel siteId={siteId!} detail={detail} onRestored={setResult} />}
                </div>
            </div>
        </div>
    );
}

function Toggle({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
    return (
        <label className="flex items-center gap-2">
            <input type="checkbox" className="size-4 accent-brand-600" checked={checked} onChange={(e) => onChange(e.target.checked)} />
            {label}
        </label>
    );
}

function ApplyResult({ result, siteId }: { result: SiteApplyResponse; siteId: number }) {
    const queryClient = useQueryClient();
    const fixDns = useMutation({ mutationFn: () => api.post<DnsActionView[]>(`/api/sites/${siteId}/cloudflare-dns`, { replaceConflicts: true }) });

    if (!result.applied) {
        return (
            <Notice tone="danger">
                <p className="font-medium">nginx rejected the change; the previous version is still live.</p>
                <pre className="mt-2 text-xs whitespace-pre-wrap">{result.error}</pre>
            </Notice>
        );
    }

    return (
        <div className="space-y-3">
            <Notice tone="success">✓ Applied (revision {result.revision}).</Notice>
            {result.dnsError && <Notice tone="warning">⚠ DNS records were not updated: {result.dnsError}</Notice>}
            {result.dnsConflicts > 0 && !fixDns.isSuccess && (
                <Notice tone="warning">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                        <span>⚠ {result.dnsConflicts} existing DNS record(s) point somewhere else and were left alone.</span>
                        <Button size="sm" variant="secondary" disabled={fixDns.isPending} onClick={() => confirm('Replace those records so the domains point to this server?') && fixDns.mutate()}>
                            Point them here
                        </Button>
                    </div>
                    {fixDns.error && <p className="mt-2">{errorMessage(fixDns.error)}</p>}
                </Notice>
            )}
            {fixDns.isSuccess && <Notice tone="success">✓ DNS records now point to this server.</Notice>}
            {result.certificateJobId && (
                <Card className="space-y-2 p-5">
                    <p className="font-medium">Getting a certificate…</p>
                    <p className="text-sm text-muted">The site works over HTTP meanwhile; HTTPS switches on automatically when the certificate is ready.</p>
                    <JobOutput jobId={result.certificateJobId} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['sites'] })} />
                </Card>
            )}
        </div>
    );
}

function Preview({ model, enabled }: { model: SiteModel; enabled: boolean }) {
    const [debounced, setDebounced] = useState(model);
    const key = JSON.stringify(model);

    useEffect(() => {
        const timer = setTimeout(() => setDebounced(model), 400);

        return () => clearTimeout(timer);
    }, [key]);

    const preview = useQuery({
        queryKey: ['site-preview', JSON.stringify(debounced)],
        queryFn: async () => {
            const response = await fetch('/api/sites/preview', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-TS-Request': '1' },
                body: JSON.stringify({ ...debounced, name: debounced.name || 'new-site' }),
            });

            return response.ok ? response.text() : `# Preview unavailable (${response.status})`;
        },
        enabled: enabled && debounced.domains.length > 0,
        placeholderData: (previous) => previous,
    });

    if (!enabled) return <p className="text-sm text-muted">You can view this site but not edit it.</p>;

    return (
        <pre className="max-h-[75vh] overflow-auto rounded-xl bg-slate-950 p-4 font-mono text-xs leading-relaxed text-slate-200">
            {debounced.domains.length === 0 ? '# Add a domain to see the generated nginx config.' : (preview.data ?? 'Rendering…')}
        </pre>
    );
}

function CustomConfigButton({ model, onEject }: { model: SiteModel; onEject: (config: string) => void }) {
    return (
        <Button
            type="button"
            size="sm"
            variant="secondary"
            onClick={async () => {
                if (!confirm('Switch to a hand-written config? Settings above stop applying until you switch back.')) return;

                const response = await fetch('/api/sites/preview', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json', 'X-TS-Request': '1' },
                    body: JSON.stringify({ ...model, name: model.name || 'new-site' }),
                });
                onEject(await response.text());
            }}
        >
            Write the config by hand…
        </Button>
    );
}

function DnsPanel({ siteId }: { siteId: number }) {
    const dns = useQuery({ queryKey: ['site-dns', siteId], queryFn: () => api.get<DnsCheck[]>(`/api/sites/${siteId}/dns`) });

    if (dns.isPending) return <Spinner label="Looking up DNS…" />;

    return (
        <Card className="space-y-3 p-5">
            <ErrorNote message={dns.error ? errorMessage(dns.error) : null} />
            {(dns.data ?? []).map((entry) => (
                <div key={entry.domain} className="flex flex-wrap items-center justify-between gap-2">
                    <div>
                        <p className="font-mono text-sm">{entry.domain}</p>
                        <p className="text-xs text-muted">{entry.addresses.length ? entry.addresses.join(', ') : 'Does not resolve'}</p>
                    </div>
                    {entry.pointsHere === true && <Badge tone="good">✓ Points here</Badge>}
                    {entry.pointsHere === false && <Badge tone="warning">⚠ Points elsewhere</Badge>}
                    {entry.pointsHere === null && <Badge>Unknown</Badge>}
                </div>
            ))}
            {dns.data?.some((d) => d.pointsHere === null) && (
                <p className="text-xs text-muted">
                    Set this server's public IP in <Link to="/settings" className="underline">Settings</Link> to check where domains point.
                </p>
            )}
            <Button size="sm" variant="ghost" onClick={() => void dns.refetch()}>
                Check again
            </Button>
        </Card>
    );
}

function HistoryPanel({ siteId, detail, onRestored }: { siteId: number; detail: SiteDetail; onRestored: (result: SiteApplyResponse) => void }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const [viewing, setViewing] = useState<number | null>(null);
    const content = useQuery({
        queryKey: ['site-revision', siteId, viewing],
        queryFn: () => api.get<{ content: string }>(`/api/sites/${siteId}/revisions/${viewing}`),
        enabled: viewing !== null,
    });
    const restore = useMutation({
        mutationFn: (number: number) => api.post<SiteApplyResponse>(`/api/sites/${siteId}/revisions/${number}/restore`),
        onSuccess: (result) => {
            onRestored(result);
            void queryClient.invalidateQueries({ queryKey: ['site'] });
        },
    });

    return (
        <Card className="space-y-2 p-5">
            <ErrorNote message={restore.error ? errorMessage(restore.error) : null} />
            <ul className="divide-y divide-line">
                {detail.revisions.map((revision) => (
                    <li key={revision.number} className="flex flex-wrap items-center justify-between gap-2 py-2 text-sm">
                        <div>
                            <span className="font-medium">#{revision.number}</span>{' '}
                            {revision.applied ? (revision.number === detail.lastAppliedRevision ? <Badge tone="good">live</Badge> : null) : <Badge tone="danger">rejected</Badge>}
                            <p className="text-xs text-muted">
                                {formatDateTime(revision.createdAt)}
                                {revision.createdBy && ` · ${revision.createdBy}`}
                            </p>
                        </div>
                        <div className="flex gap-1">
                            <Button size="sm" variant="ghost" onClick={() => setViewing(revision.number)}>
                                View
                            </Button>
                            {can('sites.manage') && revision.number !== detail.lastAppliedRevision && (
                                <Button size="sm" variant="ghost" disabled={restore.isPending} onClick={() => confirm(`Restore revision #${revision.number}?`) && restore.mutate(revision.number)}>
                                    Restore
                                </Button>
                            )}
                        </div>
                    </li>
                ))}
            </ul>
            <Modal open={viewing !== null} title={`Revision #${viewing}`} onClose={() => setViewing(null)}>
                <pre className="max-h-[60vh] overflow-auto rounded-lg bg-slate-950 p-3 font-mono text-xs text-slate-200">{content.data?.content ?? 'Loading…'}</pre>
                <div className="flex justify-end">
                    <Button variant="ghost" onClick={() => setViewing(null)}>
                        Close
                    </Button>
                </div>
            </Modal>
        </Card>
    );
}
