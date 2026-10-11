import { useQuery } from '@tanstack/react-query';
import { api, ApiError, type SystemInfo } from '../api';
import { Card } from '../components/ui';

export function DashboardPage() {
    const info = useQuery({
        queryKey: ['system-info'],
        queryFn: () => api.get<SystemInfo>('/api/system/info'),
        refetchInterval: 10_000,
        retry: false,
    });

    return (
        <div className="space-y-6">
            <h1 className="text-2xl font-semibold tracking-tight">Dashboard</h1>
            {info.isError && (
                <Card className="border-amber-300 dark:border-amber-800">
                    <p className="font-medium">System agent unavailable</p>
                    <p className="mt-1 text-sm text-slate-600 dark:text-slate-400">
                        {info.error instanceof ApiError ? info.error.message : 'Could not reach the panel.'} Check{' '}
                        <code className="rounded bg-slate-100 px-1 dark:bg-slate-800">systemctl status tm-agent</code> on the server.
                    </p>
                </Card>
            )}
            {info.data && <ServerOverview info={info.data} />}
        </div>
    );
}

function ServerOverview({ info }: { info: SystemInfo }) {
    const memoryUsed = info.memoryTotalBytes - info.memoryAvailableBytes;
    const diskUsed = info.diskTotalBytes - info.diskFreeBytes;

    return (
        <>
            <Card>
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                    <div>
                        <p className="text-lg font-semibold">{info.hostname}</p>
                        <p className="text-sm text-slate-600 dark:text-slate-400">
                            {info.osName} · kernel {info.kernel} · {info.architecture}
                        </p>
                    </div>
                    <div className="flex gap-2 text-xs">
                        {info.container && (
                            <span className="rounded-full bg-amber-100 px-2.5 py-1 font-medium text-amber-800 dark:bg-amber-950 dark:text-amber-300">
                                Container ({info.container}) — some features are skipped
                            </span>
                        )}
                        <span className="rounded-full bg-emerald-100 px-2.5 py-1 font-medium text-emerald-800 dark:bg-emerald-950 dark:text-emerald-300">
                            Up {formatDuration(info.uptimeSeconds)}
                        </span>
                    </div>
                </div>
            </Card>
            <div className="grid gap-4 sm:grid-cols-3">
                <Meter label="CPU load (1m)" value={`${info.loadAverage1M.toFixed(2)}`} detail={`${info.cpuCount} cores`} ratio={info.loadAverage1M / Math.max(info.cpuCount, 1)} />
                <Meter label="Memory" value={formatBytes(memoryUsed)} detail={`of ${formatBytes(info.memoryTotalBytes)}`} ratio={memoryUsed / Math.max(info.memoryTotalBytes, 1)} />
                <Meter label="Disk (/)" value={formatBytes(diskUsed)} detail={`of ${formatBytes(info.diskTotalBytes)}`} ratio={diskUsed / Math.max(info.diskTotalBytes, 1)} />
            </div>
        </>
    );
}

function Meter({ label, value, detail, ratio }: { label: string; value: string; detail: string; ratio: number }) {
    const percent = Math.min(Math.max(ratio, 0), 1) * 100;
    const color = percent > 90 ? 'bg-red-500' : percent > 75 ? 'bg-amber-500' : 'bg-brand-600';

    return (
        <Card className="space-y-3">
            <p className="text-sm text-slate-600 dark:text-slate-400">{label}</p>
            <p className="text-2xl font-semibold tabular-nums">
                {value} <span className="text-sm font-normal text-slate-500">{detail}</span>
            </p>
            <div className="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-slate-800">
                <div className={`h-full rounded-full ${color}`} style={{ width: `${percent}%` }} />
            </div>
        </Card>
    );
}

function formatBytes(bytes: number) {
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let value = bytes;
    let unit = 0;

    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }

    return `${value.toFixed(value >= 10 || unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function formatDuration(seconds: number) {
    const days = Math.floor(seconds / 86400);
    const hours = Math.floor((seconds % 86400) / 3600);
    const minutes = Math.floor((seconds % 3600) / 60);

    return days > 0 ? `${days}d ${hours}h` : hours > 0 ? `${hours}h ${minutes}m` : `${minutes}m`;
}
