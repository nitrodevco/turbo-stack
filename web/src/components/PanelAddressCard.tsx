import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type PanelAddressView } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from './JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Notice } from './ui';

type SetResult = { applied: boolean; error: string | null; certificateJobId: number | null; dnsError: string | null; dnsConflicts: number };

/**
 * Moves the panel to its own domain in four steps: create the site, check it from the browser,
 * continue on the new address, then close the setup address.
 */
export function PanelAddressCard() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const address = useQuery({
        queryKey: ['panel-address'],
        queryFn: () => api.get<PanelAddressView>('/api/panel-address'),
        refetchInterval: (query) => (query.state.data?.domain && !query.state.data.verified ? 3_000 : false),
    });
    const [domain, setDomain] = useState('');
    const set = useMutation({
        mutationFn: () => api.put<SetResult>('/api/panel-address', { domain }),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['panel-address'] }),
    });
    const handover = useMutation({
        mutationFn: () => api.post<{ url: string }>('/api/panel-address/handover'),
        onSuccess: (data) => window.location.assign(data.url),
    });
    const close = useMutation({
        mutationFn: () => api.post('/api/panel-address/close-setup'),
        onSettled: () => {
            void queryClient.invalidateQueries({ queryKey: ['panel-address'] });
            void queryClient.invalidateQueries({ queryKey: ['firewall'] });
        },
    });

    const data = address.data;
    const error = [address.error, set.error, handover.error, close.error].filter(Boolean).map(errorMessage)[0] ?? null;

    return (
        <Card className="space-y-4 p-5">
            <div>
                <h2 className="font-medium">Panel address</h2>
                <p className="text-sm text-muted">
                    Give the panel its own domain (e.g. panel.myhotel.com), ideally behind Cloudflare, then close the temporary setup address (port 8443).
                </p>
            </div>
            <ErrorNote message={error} />

            {data?.domain && (
                <ol className="space-y-3 text-sm">
                    <Step done label={`Site created for ${data.domain}`} />
                    <Step done={data.verified} label="Checked from your browser">
                        {!data.verified && data.verifyUrl && (
                            <span className="block">
                                <a href={data.verifyUrl} target="_blank" rel="noreferrer" className="font-medium text-brand-700 underline dark:text-brand-100">
                                    Open https://{data.domain} ↗
                                </a>{' '}
                                — when the page says it works, this step turns green. DNS and certificates can take a few minutes.
                            </span>
                        )}
                    </Step>
                    <Step done={data.onPanelDomain} label="Using the panel on its new address">
                        {data.verified && !data.onPanelDomain && (
                            <Button size="sm" className="mt-1" disabled={handover.isPending} onClick={() => handover.mutate()}>
                                Continue on https://{data.domain}
                            </Button>
                        )}
                    </Step>
                    <Step done={data.setupClosed} label="Setup address (https://server-ip:8443) closed">
                        {data.onPanelDomain && !data.setupClosed && can('firewall.manage') && (
                            <Button size="sm" variant="secondary" className="mt-1" disabled={close.isPending} onClick={() => confirm('Close https://<server-ip>:8443? If the domain ever breaks, run "sudo ts-panel-recover" on the server.') && close.mutate()}>
                                Close the setup address
                            </Button>
                        )}
                    </Step>
                </ol>
            )}

            {set.data && <SetOutcome result={set.data} />}

            {can('sites.manage') && (
                <form
                    className="flex flex-wrap items-end gap-2"
                    onSubmit={(e) => {
                        e.preventDefault();
                        set.mutate();
                    }}
                >
                    <div className="min-w-64 flex-1">
                        <Field label={data?.domain ? 'Change domain' : 'Domain'} value={domain} onChange={(e) => setDomain(e.target.value)} placeholder="panel.myhotel.com" />
                    </div>
                    <Button type="submit" disabled={!domain.trim() || set.isPending}>
                        {set.isPending ? 'Setting up…' : 'Use this domain'}
                    </Button>
                </form>
            )}
        </Card>
    );
}

function Step({ done, label, children }: { done?: boolean; label: string; children?: React.ReactNode }) {
    return (
        <li className="flex gap-3">
            {done ? <Badge tone="good">✓</Badge> : <Badge>…</Badge>}
            <div>
                <span className={done ? '' : 'font-medium'}>{label}</span>
                {children}
            </div>
        </li>
    );
}

function SetOutcome({ result }: { result: SetResult }) {
    if (!result.applied) return <Notice tone="danger">nginx rejected the panel site: {result.error}</Notice>;

    return (
        <div className="space-y-2">
            {result.dnsError && <Notice tone="warning">⚠ DNS was not updated automatically: {result.dnsError} Point the domain at this server yourself.</Notice>}
            {result.dnsConflicts > 0 && <Notice tone="warning">⚠ An existing DNS record for this name points elsewhere. Fix it on the Sites page.</Notice>}
            {result.certificateJobId && <JobOutput jobId={result.certificateJobId} />}
        </div>
    );
}
