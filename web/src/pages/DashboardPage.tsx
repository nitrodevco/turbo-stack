import { Link } from 'react-router';
import { AlertRow, type AlertEventView } from './AlertsTab';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type MetricPoint, type MetricsHistory, type MetricsSnapshot, type SystemInfo } from '../api';
import { TimeSeriesChart } from '../components/TimeSeriesChart';
import { Badge, Card, Notice, PageHeader, Tabs } from '../components/ui';
import { formatBytes, formatDuration, formatRate } from '../format';

type Range = '1h' | '6h' | '24h' | '7d';

export function DashboardPage() {
    const [range, setRange] = useState<Range>('1h');
    const alerts = useQuery({ queryKey: ['alerts'], queryFn: () => api.get<{ open: AlertEventView[] }>('/api/alerts'), refetchInterval: 30_000 });
    const info = useQuery({ queryKey: ['system-info'], queryFn: () => api.get<SystemInfo>('/api/system/info'), retry: false });
    const current = useQuery({
        queryKey: ['metrics-current'],
        queryFn: () => api.get<MetricsSnapshot>('/api/metrics/current'),
        refetchInterval: 5_000,
        retry: false,
    });
    const history = useQuery({
        queryKey: ['metrics-history', range],
        queryFn: () => api.get<MetricsHistory>(`/api/metrics/history?range=${range}`),
        refetchInterval: range === '1h' ? 10_000 : 60_000,
    });

    const points = history.data?.points ?? [];

    return (
        <div className="space-y-6">
            <PageHeader title="Dashboard" />
            {alerts.data?.open.map((a) => (
                <Link key={a.id} to="/updates?tab=alerts" className="block">
                    <AlertRow alert={a} />
                </Link>
            ))}
            {info.isError && (
                <Notice tone="warning">
                    <p className="font-medium">System agent unavailable</p>
                    <p className="mt-1">
                        {errorMessage(info.error)} Check <code>systemctl status ts-agent</code> on the server.
                    </p>
                </Notice>
            )}
            {info.data && <ServerCard info={info.data} />}
            {current.data && current.data.timestampUnixMs > 0 && <LiveTiles metrics={current.data} />}

            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="text-lg font-semibold">History</h2>
                <Tabs<Range>
                    tabs={[
                        { id: '1h', label: '1 hour' },
                        { id: '6h', label: '6 hours' },
                        { id: '24h', label: '24 hours' },
                        { id: '7d', label: '7 days' },
                    ]}
                    value={range}
                    onChange={setRange}
                />
            </div>
            <div className="grid gap-4 xl:grid-cols-2">
                <ChartCard title="CPU usage">
                    <TimeSeriesChart<MetricPoint>
                        label="CPU usage over time, percent"
                        points={points}
                        yMax={100}
                        formatValue={(v) => `${Math.round(v)}%`}
                        series={[{ key: 'cpu', label: 'CPU', color: 'var(--series-1)', value: (p) => p.cpuPercent }]}
                    />
                </ChartCard>
                <ChartCard title="Memory used">
                    <TimeSeriesChart<MetricPoint>
                        label="Memory used over time"
                        points={points}
                        yMax={points.at(-1)?.memoryTotalBytes || undefined}
                        formatValue={formatBytes}
                        series={[{ key: 'memory', label: 'Memory', color: 'var(--series-1)', value: (p) => p.memoryUsedBytes }]}
                    />
                </ChartCard>
                <ChartCard title="Network traffic">
                    <TimeSeriesChart<MetricPoint>
                        label="Network traffic in and out over time"
                        points={points}
                        formatValue={formatRate}
                        series={[
                            { key: 'rx', label: 'In', color: 'var(--series-1)', value: (p) => p.netRxBytesPerSec },
                            { key: 'tx', label: 'Out', color: 'var(--series-2)', value: (p) => p.netTxBytesPerSec },
                        ]}
                    />
                </ChartCard>
                <ChartCard title="Load average (1 min)">
                    <TimeSeriesChart<MetricPoint>
                        label="One-minute load average over time"
                        points={points}
                        formatValue={(v) => v.toFixed(1)}
                        series={[{ key: 'load', label: 'Load', color: 'var(--series-1)', value: (p) => p.load1 }]}
                    />
                </ChartCard>
            </div>
        </div>
    );
}

function ChartCard({ title, children }: { title: string; children: React.ReactNode }) {
    return (
        <Card className="space-y-3 p-5">
            <h3 className="text-sm font-medium text-muted">{title}</h3>
            {children}
        </Card>
    );
}

function ServerCard({ info }: { info: SystemInfo }) {
    return (
        <Card>
            <div className="flex flex-wrap items-baseline justify-between gap-2">
                <div>
                    <p className="text-lg font-semibold">{info.hostname}</p>
                    <p className="text-sm text-muted">
                        {info.osName} · kernel {info.kernel} · {info.architecture} · {info.cpuCount} cores
                    </p>
                </div>
                <div className="flex flex-wrap gap-2">
                    {info.container && <Badge tone="warning">Container ({info.container}) — some features are skipped</Badge>}
                    <Badge tone="good">Up {formatDuration(info.uptimeSeconds)}</Badge>
                </div>
            </div>
        </Card>
    );
}

function LiveTiles({ metrics }: { metrics: MetricsSnapshot }) {
    const memoryUsed = metrics.memoryTotalBytes - metrics.memoryAvailableBytes;
    const swapUsed = metrics.swapTotalBytes - metrics.swapFreeBytes;

    return (
        <div className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4">
            <Meter label="CPU" value={`${Math.round(metrics.cpuPercent)}%`} detail={`load ${metrics.load1M.toFixed(2)}`} ratio={metrics.cpuPercent / 100} />
            <Meter
                label="Memory"
                value={formatBytes(memoryUsed)}
                detail={`of ${formatBytes(metrics.memoryTotalBytes)}${metrics.swapTotalBytes > 0 ? ` · swap ${formatBytes(swapUsed)}` : ''}`}
                ratio={memoryUsed / Math.max(1, metrics.memoryTotalBytes)}
            />
            {metrics.disks.slice(0, 1).map((disk) => (
                <Meter
                    key={disk.mount}
                    label={`Disk (${disk.mount})`}
                    value={formatBytes(disk.totalBytes - disk.freeBytes)}
                    detail={`of ${formatBytes(disk.totalBytes)}`}
                    ratio={(disk.totalBytes - disk.freeBytes) / Math.max(1, disk.totalBytes)}
                />
            ))}
            <Card className="space-y-1 p-5">
                <p className="text-sm text-muted">Network</p>
                <p className="text-lg font-semibold tabular-nums">↓ {formatRate(metrics.netRxBytesPerSec)}</p>
                <p className="text-lg font-semibold tabular-nums">↑ {formatRate(metrics.netTxBytesPerSec)}</p>
            </Card>
        </div>
    );
}

function Meter({ label, value, detail, ratio }: { label: string; value: string; detail: string; ratio: number }) {
    const percent = Math.min(Math.max(ratio, 0), 1) * 100;
    const status = percent > 90 ? { color: 'bg-bad', text: 'Critical' } : percent > 75 ? { color: 'bg-warn', text: 'High' } : null;

    return (
        <Card className="space-y-3 p-5">
            <div className="flex items-center justify-between">
                <p className="text-sm text-muted">{label}</p>
                {status && <Badge tone={percent > 90 ? 'danger' : 'warning'}>⚠ {status.text}</Badge>}
            </div>
            <p className="text-2xl font-semibold tabular-nums">
                {value} <span className="text-sm font-normal text-muted">{detail}</span>
            </p>
            <div className="h-2 overflow-hidden rounded-full bg-subtle" role="meter" aria-valuenow={Math.round(percent)} aria-valuemin={0} aria-valuemax={100} aria-label={label}>
                <div className={`h-full rounded-full ${status?.color ?? 'bg-brand-600'}`} style={{ width: `${percent}%` }} />
            </div>
        </Card>
    );
}
