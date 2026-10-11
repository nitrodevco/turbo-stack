import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type CertificateView } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, PageHeader, SelectField, Spinner, TableWrap } from '../components/ui';
import { formatDateTime } from '../format';

const kindLabels = { LetsEncrypt: "Let's Encrypt", CloudflareOrigin: 'Cloudflare Origin', SelfSigned: 'Self-signed' } as const;

export function CertificatesPage() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const certificates = useQuery({ queryKey: ['certificates'], queryFn: () => api.get<CertificateView[]>('/api/certificates') });
    const [jobId, setJobId] = useState<number | null>(null);
    const [domains, setDomains] = useState('');
    const [kind, setKind] = useState<'LetsEncrypt' | 'SelfSigned'>('LetsEncrypt');

    const request = useMutation({
        mutationFn: () => api.post<{ jobId: number }>('/api/certificates', { domains: domains.split(/[\s,]+/).filter(Boolean), kind }),
        onSuccess: (data) => {
            setJobId(data.jobId);
            setDomains('');
        },
    });
    const renew = useMutation({ mutationFn: (id: number) => api.post<{ jobId: number }>(`/api/certificates/${id}/renew`), onSuccess: (data) => setJobId(data.jobId) });
    const remove = useMutation({
        mutationFn: (id: number) => api.delete(`/api/certificates/${id}`),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['certificates'] }),
    });

    return (
        <div className="space-y-6">
            <PageHeader title="Certificates" description="HTTPS certificates installed on this server. Sites pick a matching certificate automatically; Let's Encrypt ones renew 30 days before they expire." />
            <ErrorNote message={[certificates.error, request.error, renew.error, remove.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {jobId && (
                <Card className="p-5">
                    <JobOutput jobId={jobId} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['certificates'] })} />
                </Card>
            )}
            {certificates.isPending ? (
                <Spinner />
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Domains</th>
                            <th>Type</th>
                            <th>Status</th>
                            <th>Expires</th>
                            <th />
                        </tr>
                    </thead>
                    <tbody>
                        {(certificates.data ?? []).map((certificate) => {
                            const daysLeft = certificate.notAfter ? Math.floor((new Date(certificate.notAfter).getTime() - Date.now()) / 86_400_000) : null;

                            return (
                                <tr key={certificate.id}>
                                    <td className="font-mono text-xs">{certificate.domains.join(', ')}</td>
                                    <td>{kindLabels[certificate.kind]}</td>
                                    <td>
                                        {certificate.status === 'Valid' && (daysLeft !== null && daysLeft < 14 ? <Badge tone="warning">⚠ Expires soon</Badge> : <Badge tone="good">✓ Valid</Badge>)}
                                        {certificate.status === 'Pending' && <Badge tone="info">Pending</Badge>}
                                        {certificate.status === 'Failed' && <Badge tone="danger">✕ Failed</Badge>}
                                        {certificate.lastError && <p className="mt-1 max-w-sm text-xs text-red-700 dark:text-red-300">{certificate.lastError}</p>}
                                    </td>
                                    <td className="text-slate-600 dark:text-slate-400">{certificate.notAfter ? `${formatDateTime(certificate.notAfter).split(',')[0]} (${daysLeft} d)` : '—'}</td>
                                    <td>
                                        {can('sites.manage') && (
                                            <div className="flex justify-end gap-1">
                                                {certificate.kind !== 'CloudflareOrigin' && (
                                                    <Button size="sm" variant="ghost" disabled={renew.isPending} onClick={() => renew.mutate(certificate.id)}>
                                                        Renew now
                                                    </Button>
                                                )}
                                                <Button size="sm" variant="ghost" onClick={() => confirm(`Delete the certificate for ${certificate.domains[0]}?`) && remove.mutate(certificate.id)}>
                                                    Delete
                                                </Button>
                                            </div>
                                        )}
                                    </td>
                                </tr>
                            );
                        })}
                        {certificates.data?.length === 0 && (
                            <tr>
                                <td colSpan={5} className="py-8 text-center text-slate-500">
                                    No certificates yet. They are requested automatically when you add an HTTPS site.
                                </td>
                            </tr>
                        )}
                    </tbody>
                </TableWrap>
            )}
            {can('sites.manage') && (
                <Card className="space-y-4 p-5">
                    <h2 className="font-medium">Request a certificate</h2>
                    <form
                        className="grid gap-3 sm:grid-cols-[1fr_14rem_auto] sm:items-end"
                        onSubmit={(e) => {
                            e.preventDefault();
                            request.mutate();
                        }}
                    >
                        <label className="block space-y-1.5">
                            <span className="text-sm font-medium text-slate-700 dark:text-slate-300">Domains</span>
                            <input
                                className="block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm dark:border-slate-700 dark:bg-slate-950"
                                value={domains}
                                onChange={(e) => setDomains(e.target.value)}
                                placeholder="play.myhotel.com, admin.myhotel.com"
                            />
                        </label>
                        <SelectField label="Type" value={kind} onChange={(e) => setKind(e.target.value as typeof kind)}>
                            <option value="LetsEncrypt">Let's Encrypt</option>
                            <option value="SelfSigned">Self-signed</option>
                        </SelectField>
                        <Button type="submit" disabled={!domains.trim() || request.isPending}>
                            Request
                        </Button>
                    </form>
                    <p className="text-xs text-slate-500">Let's Encrypt checks each domain over HTTP: its DNS must point to this server and port 80 must be open.</p>
                </Card>
            )}
        </div>
    );
}
