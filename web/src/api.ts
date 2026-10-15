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
            'X-TS-Request': '1',
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
    delete: <T = void>(path: string, body?: unknown) => request<T>('DELETE', path, body),
};

export function errorMessage(error: unknown) {
    return error instanceof ApiError ? error.message : error instanceof Error ? error.message : 'Something went wrong.';
}

export type SetupStatus = { claimed: boolean; productName: string };

export type Me = { username: string; role: string; remainingRecoveryCodes: number; permissions: string[]; limited?: boolean };

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

export type BackupTargetKind = 'Local' | 'S3' | 'Sftp';

export type BackupTarget = {
    id: number;
    name: string;
    kind: BackupTargetKind;
    path: string;
    endpoint: string;
    port: number;
    user: string;
    accessKeyId: string;
    region: string;
    hostKey: string | null;
    publicKey: string | null;
    checkedAt: string | null;
    lastError: string | null;
    ready: boolean;
};

export type BackupPlan = {
    id: number;
    name: string;
    targetId: number;
    databases: string[];
    folders: string[];
    panelState: boolean;
    enabled: boolean;
    timeOfDay: string;
    keepDaily: number;
    keepWeekly: number;
    keepMonthly: number;
    lastRunAt: string | null;
    lastResult: string | null;
    lastSnapshot: string | null;
};

export type BackupOverview = { targets: BackupTarget[]; plans: BackupPlan[] };

export type BackupSnapshot = { id: string; shortId: string; time: string; hostname: string; planId: string; tags: string[] };

export type BackupManifest = { planName: string; created: string; databases: string[]; folders: string[]; panelState: boolean };

export type PanelUpdate = {
    currentVersion: string;
    channel: string;
    latestVersion: string | null;
    notes: string | null;
    published: string | null;
    available: boolean;
    error: string | null;
    checkedAt: string | null;
    running: boolean;
    last: { state: string; from: string; to: string; message: string; at: string } | null;
};
export type RebootStatus = { scheduled: boolean; scheduledAtUnixMs: number; supported: boolean; message: string };

export type JobSummary = { id: number; kind: string; title: string; status: 'running' | 'succeeded' | 'failed'; startedAt: string; finishedAt: string | null; startedBy: string | null };

export type AuditEntry = { id: number; at: string; username: string | null; action: string; target: string | null; details: string | null; ipAddress: string | null };

export type TlsMode = 'None' | 'LetsEncrypt' | 'CloudflareOrigin' | 'SelfSigned';

export type RouteKind = 'Static' | 'Spa' | 'Proxy' | 'WebSocket' | 'Redirect';

export type SiteRouteModel = { path: string; kind: RouteKind; target: string; cors: boolean; cacheSeconds: number | null; snippet: string | null };

export type SiteModel = {
    name: string;
    domains: string[];
    routes: SiteRouteModel[];
    tlsMode: TlsMode;
    forceHttps: boolean;
    hsts: boolean;
    securityHeaders: boolean;
    clientMaxBodyMb: number;
    serverSnippet: string | null;
    customConfig: string | null;
    enabled: boolean;
    cloudflareDns: boolean;
    cloudflareProxied: boolean;
};

export type SiteSummary = {
    id: number;
    name: string;
    domains: string[];
    tlsMode: TlsMode;
    https: boolean;
    certificateExpires: string | null;
    enabled: boolean;
    system: boolean;
    custom: boolean;
    lastError: string | null;
    routes: { path: string; kind: RouteKind; target: string }[];
};

export type SiteDetail = {
    id: number;
    system: boolean;
    lastError: string | null;
    lastAppliedRevision: number | null;
    model: SiteModel;
    revisions: { number: number; applied: boolean; error: string | null; createdBy: string | null; createdAt: string }[];
};

export type SiteApplyResponse = { id: number; applied: boolean; error: string | null; revision: number; certificateJobId: number | null; dnsError: string | null; dnsConflicts: number };

export type DnsCheck = { domain: string; addresses: string[]; pointsHere: boolean | null };

export type CertificateView = {
    id: number;
    name: string;
    domains: string[];
    kind: 'LetsEncrypt' | 'CloudflareOrigin' | 'SelfSigned';
    status: 'Pending' | 'Valid' | 'Failed';
    notAfter: string | null;
    issuedAt: string | null;
    lastError: string | null;
    autoRenew: boolean;
};

export type ServerSettings = { publicIPv4: string | null; publicIPv6: string | null };

export type PermissionCheck = { key: string; label: string; usedFor: string; required: boolean; status: 'ok' | 'missing' | 'error' | 'unknown'; error: string | null };

export type CloudflareZone = { id: string; name: string; status: string; plan: string | null };

export type CloudflareOverview =
    | { connected: false }
    | {
          connected: true;
          token: { status: string; expiresOn: string | null; verifiedAt: string };
          checks: PermissionCheck[];
          zones: CloudflareZone[];
          realIpEnabled: boolean;
          ipRangesUpdatedAt: string | null;
          cloudflareOnlyWeb: boolean;
      };

export type ZoneSetting = { setting: string; label: string; current: string | null; recommended: string; ok: boolean; error: string | null };

export type DnsActionView = { kind: 'ok' | 'create' | 'update' | 'conflict' | 'delete'; type: string; name: string; content: string | null; proxied: boolean; existing: string | null; reason: string | null };

export type PanelAddressView = { domain: string | null; siteId: number | null; verified: boolean; verifyUrl: string | null; setupClosed: boolean; onPanelDomain: boolean };

export type DatabaseEngineSlug = 'mysql' | 'mariadb' | 'postgres';

export type DatabaseEngineView = {
    engine: DatabaseEngineSlug;
    label: string;
    installed: boolean;
    running: boolean;
    version: string;
    service: string;
    port: number;
    blockedReason: string;
    candidateVersion: string;
    recommended: boolean;
    note: string;
};

export type DatabaseInfo = { name: string; sizeBytes: number; tables: number };

export type DatabaseUserView = { name: string; grants: { database: string; level: 'all' | 'readonly' }[]; passwordStored: boolean };

export type DatabaseCredentials = { engine: DatabaseEngineSlug; database: string; user: string; password: string; host: string; port: number };

export type DatabaseBackup = { id: string; engine: DatabaseEngineSlug | ''; database: string; sizeBytes: number; createdUnixMs: number; import: boolean; fileName: string };

export type BackupScheduleView = { id: number; engine: DatabaseEngineSlug; database: string; enabled: boolean; timeOfDay: string; retentionCount: number; lastRunAt: string | null; lastResult: string | null };

export type RepositoryView = {
    id: number;
    name: string;
    systemUser: string;
    owner: string;
    repositoryName: string;
    fullName: string;
    branch: string;
    isPrivate: boolean;
    publicKey: string | null;
    keyFingerprint: string | null;
    submodules: boolean;
    submoduleKeys: { repository: string; publicKey: string | null; fingerprint: string | null; installedOnGitHub: boolean }[];
    deployKeyInstalledOnGitHub: boolean;
    webhookInstalledOnGitHub: boolean;
    /** Only in the response to adding a repository or setting it up on GitHub. */
    gitHubSetup: GitHubSetupStep[] | null;
    lastSyncedAt: string | null;
    lastCommit: string | null;
    lastCommitMessage: string | null;
    lastError: string | null;
    webhookUrl: string;
    deployKeysUrl: string;
    webhooksUrl: string;
};

export type GitHubSetupStep = { label: string; status: 'ok' | 'failed'; detail: string | null };

export type GitHubAccountView = {
    connected: boolean;
    login: string | null;
    name: string | null;
    avatarUrl: string | null;
    scopes: string | null;
    connectedBy: string | null;
    connectedAt: string | null;
    createTokenUrl: string;
};

export type GitHubRepositoryChoice = {
    fullName: string;
    owner: string;
    name: string;
    private: boolean;
    description: string | null;
    defaultBranch: string;
    pushedAt: string | null;
    admin: boolean;
};

export type WebhookDeliveryView = { id: string; receivedAt: string; event: string; ref: string | null; result: string };

export type AppSummary = {
    id: number;
    name: string;
    systemUser: string;
    repository: string;
    branch: string;
    currentRelease: string | null;
    autoDeploy: boolean;
    hasService: boolean;
    staticPath: string | null;
    /** /home/<user>/apps/<name>: its releases, and the files a site may serve. */
    appDirectory: string;
    servicePort: number | null;
    /** Ports reserved for the app on 127.0.0.1, by the recipe's name for them. */
    ports: Record<string, number>;
    lastRelease: { name: string; status: ReleaseStatus; startedAt: string; commitMessage: string | null } | null;
};

export type ReleaseStatus = 'building' | 'active' | 'inactive' | 'failed';

export type AppSettings = {
    id: number;
    name: string;
    systemUser: string;
    repositoryId: number;
    repository: string;
    workingDirectory: string;
    buildScript: string;
    runtimes: string[];
    sharedPaths: { path: string; directory: boolean }[];
    staticOutput: string | null;
    serviceCommand: string | null;
    servicePort: number | null;
    healthPath: string | null;
    memoryMaxMb: number;
    stopTimeoutSeconds: number;
    keepReleases: number;
    autoDeploy: boolean;
    deployPaths: string[];
    purgeCloudflare: boolean;
    currentRelease: string | null;
    staticPath: string | null;
    unit: string | null;
    serviceDirectory: string | null;
    console: boolean;
    recipeId: string | null;
    recipeInputs: Record<string, string>;
    ports: Record<string, number>;
};

export type ReleaseView = {
    name: string;
    commit: string | null;
    commitMessage: string | null;
    commitAuthor: string | null;
    status: ReleaseStatus;
    trigger: string;
    triggeredBy: string | null;
    jobId: number | null;
    startedAt: string;
    finishedAt: string | null;
    error: string | null;
};

export type RuntimeView = { name: string; installed: boolean; version: string; path: string };

export type RecipeInput = { key: string; label: string; help: string; kind: 'text' | 'url' | 'bool' | 'number'; default: string; placeholder: string; required: boolean };

export type RecipeView = {
    id: string;
    name: string;
    summary: string;
    description: string;
    category: 'turbo' | 'nitro' | 'general';
    source: { repository: string; branch: string; submodules: boolean } | null;
    inputs: RecipeInput[];
    databaseEngines: DatabaseEngineSlug[] | null;
    ports: { name: string; default: number; label: string }[];
    hasApp: boolean;
    hasService: boolean;
    runtimes: string[];
    site: { required: boolean; requiresTls: boolean; directory: boolean } | null;
};

export type RecipeRequest = {
    name: string;
    systemUser: string;
    repositoryId?: number | null;
    repository?: string;
    branch?: string;
    private?: boolean;
    privateSubmodules?: string[];
    domains: string[];
    tlsMode: TlsMode;
    cloudflareDns: boolean;
    databaseEngine?: string | null;
    inputs: Record<string, string>;
};

export type RecipePlanView = {
    recipe: string;
    name: string;
    systemUser: string;
    repository: { id: number | null; name: string; fullName: string; branch: string; submodules: boolean; private: boolean; reused: boolean } | null;
    database: { engine: DatabaseEngineSlug; name: string; user: string } | null;
    ports: Record<string, number>;
    app: {
        workingDirectory: string;
        serviceDirectory: string | null;
        runtimes: string[];
        build: string;
        staticOutput: string | null;
        sharedPaths: { path: string; directory: boolean }[];
        deployPaths: string[];
        service: { command: string; port: number | null; healthPath: string | null; memoryMaxMb: number; stopTimeoutSeconds: number } | null;
        environment: Record<string, string>;
    } | null;
    site: { name: string; domains: string[]; tlsMode: TlsMode; routes: { path: string; kind: string; target: string }[] } | null;
    siteDirectory: string | null;
    notes: string[];
};

export type RecipeResultView = {
    appId: number | null;
    siteId: number | null;
    repositoryId: number | null;
    repositoryCreated: boolean;
    deployKeys: { repository: string; publicKey: string | null }[] | null;
    site: { applied: boolean; error: string | null; certificateJobId: number | null; dnsError: string | null } | null;
    notes: string[];
};

export type PresetRoleView = {
    role: string;
    recipe: string;
    host: string;
    label: string;
    optional: boolean;
    source: { repository: string; branch: string; submodules: boolean } | null;
    requiresTls: boolean;
    exports: string[];
};

export type PresetView = {
    id: string;
    name: string;
    summary: string;
    description: string;
    settings: RecipeInput[];
    roles: PresetRoleView[];
    databaseEngines: DatabaseEngineSlug[] | null;
};

export type StackSourceView = { default: string; repository: string; branch: string; private: boolean; privateSubmodules: string[] };

export type StackRequest = {
    name?: string;
    preset?: string;
    systemUser?: string;
    domain?: string;
    tlsMode: TlsMode;
    cloudflareDns?: boolean;
    hosts?: Record<string, string>;
    settings?: Record<string, string>;
    overrides?: Record<string, string>;
    sources?: StackSourceView[];
    databaseEngine?: string | null;
    isolated?: boolean;
};

export type StackRoleView = {
    role: string;
    label: string;
    recipe: string;
    optional: boolean;
    included: boolean;
    domain: string | null;
    app: {
        id: number;
        name: string;
        repository: string;
        currentRelease: string | null;
        ports: Record<string, number>;
        lastRelease: { name: string; status: ReleaseStatus; error: string | null } | null;
        service: string | null;
    } | null;
    site: { id: number; name: string; applied: boolean; lastError: string | null } | null;
    wiring: { input: string; label: string; from: string; value: string; pinned: boolean }[];
};

export type StackView = {
    id: number;
    name: string;
    preset: string;
    presetName: string;
    domain: string;
    systemUser: string;
    tlsMode: TlsMode;
    cloudflareDns: boolean;
    hosts: Record<string, string>;
    settings: Record<string, string>;
    overrides: Record<string, string>;
    sources: StackSourceView[];
    databaseEngine: string | null;
    installJobId: number | null;
    roles: StackRoleView[];
};

export type MemberChangeView = {
    role: string;
    name: string;
    environment: { key: string; from: string | null; to: string | null; pinned: boolean }[];
    site: 'none' | 'update' | 'create' | 'hand-edited';
    domainsFrom: string[];
    domainsTo: string[];
    action: 'none' | 'reload' | 'restart' | 'redeploy';
};
