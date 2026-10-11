export class ApiError extends Error {
    constructor(
        public readonly status: number,
        message: string,
        public readonly body?: unknown,
    ) {
        super(message);
    }
}

type ProblemDetails = { title?: string; detail?: string; errors?: Record<string, string[]>; error?: string };

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await fetch(path, {
        method,
        credentials: 'same-origin',
        headers: {
            // Required by the panel on every state-changing call (CSRF defence).
            'X-TM-Request': '1',
            ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
        },
        body: body !== undefined ? JSON.stringify(body) : undefined,
    });

    if (!response.ok) {
        const { message, parsed } = await readError(response);
        throw new ApiError(response.status, message, parsed);
    }

    if (response.status === 204) {
        return undefined as T;
    }

    return (await response.json()) as T;
}

async function readError(response: Response): Promise<{ message: string; parsed?: unknown }> {
    try {
        const problem = (await response.json()) as ProblemDetails;
        const firstValidationError = problem.errors ? Object.values(problem.errors).flat()[0] : undefined;

        return { message: firstValidationError ?? problem.detail ?? problem.error ?? problem.title ?? response.statusText, parsed: problem };
    } catch {
        return { message: response.status === 429 ? 'Too many attempts. Wait a minute and try again.' : response.statusText || `Request failed (${response.status})` };
    }
}

export const api = {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T = void>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
    put: <T = void>(path: string, body: unknown) => request<T>('PUT', path, body),
    delete: <T = void>(path: string) => request<T>('DELETE', path),
};

export function errorMessage(error: unknown) {
    return error instanceof ApiError ? error.message : error instanceof Error ? error.message : 'Something went wrong.';
}

export type SetupStatus = { claimed: boolean; productName: string };

export type Me = { username: string; role: string; remainingRecoveryCodes: number; permissions: string[] };

export type SystemInfo = {
    hostname: string;
    osName: string;
    osVersionId: string;
    kernel: string;
    architecture: string;
    uptimeSeconds: number;
    cpuCount: number;
    memoryTotalBytes: number;
    memoryAvailableBytes: number;
    diskTotalBytes: number;
    diskFreeBytes: number;
    loadAverage1M: number;
    container: string;
};

export type MetricsSnapshot = {
    timestampUnixMs: number;
    cpuPercent: number;
    cpuCount: number;
    load1M: number;
    load5M: number;
    load15M: number;
    memoryTotalBytes: number;
    memoryAvailableBytes: number;
    swapTotalBytes: number;
    swapFreeBytes: number;
    disks: { mount: string; fsType: string; totalBytes: number; freeBytes: number }[];
    netRxBytesPerSec: number;
    netTxBytesPerSec: number;
    uptimeSeconds: number;
};

export type MetricPoint = {
    at: number;
    cpuPercent: number;
    load1: number;
    memoryUsedBytes: number;
    memoryTotalBytes: number;
    swapUsedBytes: number;
    diskUsedBytes: number;
    diskTotalBytes: number;
    netRxBytesPerSec: number;
    netTxBytesPerSec: number;
};

export type MetricsHistory = { range: string; bucketMs: number; points: MetricPoint[] };

export type ServiceUnit = {
    name: string;
    description: string;
    loadState: string;
    activeState: string;
    subState: string;
    unitFileState: string;
    mainPid: number;
    memoryBytes: number;
    cpuUsageNsec: number;
    activeSinceUnixMs: number;
    restarts: number;
};

export type JournalEntry = {
    timestampUsec: number;
    unit: string;
    identifier: string;
    priority: number;
    message: string;
    pid: number;
    cursor: string;
};

export type FirewallRuleView = {
    id: number;
    key: string;
    name: string;
    action: 'allow' | 'deny' | 'reject' | 'limit';
    protocol: 'tcp' | 'udp' | 'any';
    ports: string;
    sourceKind: 'Any' | 'Address' | 'IpList';
    sourceAddress: string | null;
    ipListId: number | null;
    ipListName: string | null;
    enabled: boolean;
    system: boolean;
    status: 'applied' | 'not-applied' | 'disabled';
};

export type PendingChange = { id: string; revertAt: number; summary: string };

export type FirewallOverview = {
    settings: { enabled: boolean; defaultIncoming: string };
    actual: { available: boolean; enabled: boolean; defaultIncoming: string; error: string };
    rules: FirewallRuleView[];
    external: { action: string; protocol: string; ports: string; source: string; direction: string; comment: string; ipv6Only: boolean }[];
    inSync: boolean;
    pending: PendingChange | null;
};

export type FirewallCommitResult = { changed: boolean; reasons: string[]; pending: PendingChange | null };

export type IpListView = { id: number; name: string; description: string | null; entries: { source: string; note: string | null }[]; usedBy: string[] };

export type ExposureItem = {
    protocol: string;
    address: string;
    port: number;
    process: string;
    exposure: 'local' | 'blocked' | 'restricted' | 'public';
    risk: 'ok' | 'warning' | 'danger';
    note: string;
};

export type Fail2banJail = {
    name: string;
    bannedIps: string[];
    currentlyFailed: number;
    totalFailed: number;
    totalBanned: number;
    banTimeSeconds: number;
    findTimeSeconds: number;
    maxRetry: number;
    ignoreIps: string[];
};

export type Fail2banStatus = { available: boolean; jails: Fail2banJail[]; error: string };

export type LoginUser = { name: string; uid: number; home: string; sudo: boolean; keyCount: number };

export type AuthorizedKey = { type: string; fingerprint: string; comment: string; options: string };

export type SshdSettings = { passwordAuthentication: boolean; permitRootLogin: string; pubkeyAuthentication: boolean; ports: number[] };

export type UpgradablePackages = {
    packages: { name: string; currentVersion: string; candidateVersion: string; source: string; security: boolean }[];
    rebootRequired: boolean;
    rebootRequiredPackages: string[];
    listsUpdatedUnixMs: number;
};

export type RebootStatus = { scheduled: boolean; scheduledAtUnixMs: number; supported: boolean; message: string };

export type JobSummary = { id: number; kind: string; title: string; status: 'running' | 'succeeded' | 'failed'; startedAt: string; finishedAt: string | null; startedBy: string | null };

export type AuditEntry = { id: number; at: string; username: string | null; action: string; target: string | null; details: string | null; ipAddress: string | null };
