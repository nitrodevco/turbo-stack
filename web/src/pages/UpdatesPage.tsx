import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type JobSummary, type RebootStatus, type UpgradablePackages } from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { PanelUpdateCard } from '../components/PanelUpdateCard';
import { Badge, Button, Card, ErrorNote, Notice, PageHeader, Spinner, TableWrap } from '../components/ui';
import { formatDateTime, formatRelative } from '../format';

export function UpdatesPage() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const packages = useQuery({ queryKey: ['packages'], queryFn: () => api.get<UpgradablePackages>('/api/packages') });
    const recentJobs = useQuery({ queryKey: ['jobs', 'packages'], queryFn: () => api.get<JobSummary[]>('/api/jobs?kind=packages') });
    const runningJob = recentJobs.data?.find((j) => j.status === 'running');
    const [jobId, setJobId] = useState<number | null>(null);
    const activeJob = jobId ?? runningJob?.id ?? null;

    const refresh = useMutation({
        mutationFn: () => api.get<UpgradablePackages>('/api/packages?refresh=true'),
        onSuccess: (data) => queryClient.setQueryData(['packages'], data),
    });
    const upgrade = useMutation({
        mutationFn: (body: { mode: string; names?: string[] }) => api.post<{ jobId: number }>('/api/packages/upgrade', body),
        onSuccess: (data) => setJobId(data.jobId),
    });

    const list = packages.data?.packages ?? [];
    const security = list.filter((p) => p.security).length;

    return (
        <div className="space-y-6">
            <PageHeader
                title="Updates"
                description="This panel, and the operating system packages from Ubuntu. Security updates are also installed automatically every day."
                actions={
                    <Button variant="secondary" disabled={refresh.isPending} onClick={() => refresh.mutate()}>
                        {refresh.isPending ? 'Checking…' : 'Check for updates'}
                    </Button>
                }
            />
            <PanelUpdateCard />
            <RebootCard required={packages.data?.rebootRequired ?? false} packages={packages.data?.rebootRequiredPackages ?? []} />
            <ErrorNote message={[packages.error, refresh.error, upgrade.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {activeJob && (
                <Card className="space-y-3 p-5">
                    <h2 className="font-medium">Installing updates</h2>
                    <JobOutput
                        jobId={activeJob}
                        onEnd={() => {
                            void queryClient.invalidateQueries({ queryKey: ['packages'] });
                            void queryClient.invalidateQueries({ queryKey: ['jobs'] });
                        }}
                    />
                </Card>
            )}
            {packages.isPending ? (
                <Spinner />
            ) : (
                <Card className="space-y-4 p-5">
                    <div className="flex flex-wrap items-center justify-between gap-3">
                        <div>
                            <p className="font-medium">
                                {list.length === 0 ? 'Everything is up to date.' : `${list.length} updates available`}
                                {security > 0 && <span className="ml-2"><Badge tone="warning">⚠ {security} security</Badge></span>}
                            </p>
                            {packages.data && packages.data.listsUpdatedUnixMs > 0 && (
                                <p className="text-xs text-muted">Package lists updated {formatRelative(packages.data.listsUpdatedUnixMs)}</p>
                            )}
                        </div>
                        {can('packages.manage') && list.length > 0 && (
                            <div className="flex gap-2">
                                {security > 0 && (
                                    <Button variant="secondary" disabled={upgrade.isPending || !!runningJob} onClick={() => upgrade.mutate({ mode: 'security' })}>
                                        Install security updates
                                    </Button>
                                )}
                                <Button disabled={upgrade.isPending || !!runningJob} onClick={() => confirm(`Update all ${list.length} packages?`) && upgrade.mutate({ mode: 'all' })}>
                                    Update all
                                </Button>
                            </div>
                        )}
                    </div>
                    {list.length > 0 && (
                        <TableWrap>
                            <thead>
                                <tr>
                                    <th>Package</th>
                                    <th>Installed</th>
                                    <th>Available</th>
                                    <th />
                                </tr>
                            </thead>
                            <tbody>
                                {list.map((p) => (
                                    <tr key={p.name}>
                                        <td className="font-medium">
                                            {p.name} {p.security && <Badge tone="warning">security</Badge>}
                                        </td>
                                        <td className="font-mono text-xs text-muted">{p.currentVersion}</td>
                                        <td className="font-mono text-xs">{p.candidateVersion}</td>
                                        <td className="text-right">
                                            {can('packages.manage') && (
                                                <Button size="sm" variant="ghost" disabled={upgrade.isPending || !!runningJob} onClick={() => upgrade.mutate({ mode: 'selected', names: [p.name] })}>
                                                    Update
                                                </Button>
                                            )}
                                        </td>
                                    </tr>
                                ))}
                            </tbody>
                        </TableWrap>
                    )}
                </Card>
            )}
            {(recentJobs.data?.length ?? 0) > 0 && (
                <div className="space-y-2">
                    <h2 className="text-sm font-medium text-muted">Recent update runs</h2>
                    <TableWrap>
                        <tbody>
                            {recentJobs.data!.map((job) => (
                                <tr key={job.id} className="cursor-pointer hover:bg-slate-50 dark:hover:bg-slate-800/50" onClick={() => setJobId(job.id)}>
                                    <td>{job.title}</td>
                                    <td>{job.status === 'succeeded' ? <Badge tone="good">✓ succeeded</Badge> : job.status === 'failed' ? <Badge tone="danger">✕ failed</Badge> : <Badge tone="info">running</Badge>}</td>
                                    <td className="text-muted">{formatDateTime(job.startedAt)}</td>
                                    <td className="text-muted">{job.startedBy}</td>
                                </tr>
                            ))}
                        </tbody>
                    </TableWrap>
                </div>
            )}
        </div>
    );
}

function RebootCard({ required, packages }: { required: boolean; packages: string[] }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const status = useQuery({ queryKey: ['reboot'], queryFn: () => api.get<RebootStatus>('/api/server/reboot') });
    const schedule = useMutation({
        mutationFn: (body: { delayMinutes: number; cancel: boolean }) => api.post<RebootStatus>('/api/server/reboot', body),
        onSuccess: (data) => queryClient.setQueryData(['reboot'], data),
    });

    if (!status.data || (!required && !status.data.scheduled)) return null;

    return (
        <Notice tone={status.data.scheduled ? 'info' : 'warning'}>
            <div className="flex flex-wrap items-center justify-between gap-3">
                <div>
                    {status.data.scheduled ? (
                        <p>Reboot scheduled for {formatDateTime(status.data.scheduledAtUnixMs)}.</p>
                    ) : (
                        <p>
                            ⚠ A reboot is needed to finish installing updates{packages.length > 0 && ` (${packages.slice(0, 4).join(', ')}${packages.length > 4 ? '…' : ''})`}.
                        </p>
                    )}
                    {!status.data.supported && <p className="text-xs">{status.data.message}</p>}
                    {schedule.error && <p className="text-xs">{errorMessage(schedule.error)}</p>}
                </div>
                {can('server.reboot') && status.data.supported && (
                    <div className="flex gap-2">
                        {status.data.scheduled ? (
                            <Button size="sm" variant="secondary" onClick={() => schedule.mutate({ delayMinutes: 0, cancel: true })}>
                                Cancel reboot
                            </Button>
                        ) : (
                            <>
                                <Button size="sm" variant="secondary" onClick={() => schedule.mutate({ delayMinutes: 60, cancel: false })}>
                                    Reboot in 1 hour
                                </Button>
                                <Button size="sm" variant="danger" onClick={() => confirm('Reboot the server now? Everything on it goes offline for a minute or two.') && schedule.mutate({ delayMinutes: 0, cancel: false })}>
                                    Reboot now
                                </Button>
                            </>
                        )}
                    </div>
                )}
            </div>
        </Notice>
    );
}
