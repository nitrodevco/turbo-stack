export class ApiError extends Error {
    constructor(
        public readonly status: number,
        message: string,
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
        throw new ApiError(response.status, await readError(response));
    }

    if (response.status === 204) {
        return undefined as T;
    }

    return (await response.json()) as T;
}

async function readError(response: Response): Promise<string> {
    try {
        const problem = (await response.json()) as ProblemDetails;
        const firstValidationError = problem.errors ? Object.values(problem.errors).flat()[0] : undefined;

        return firstValidationError ?? problem.detail ?? problem.error ?? problem.title ?? response.statusText;
    } catch {
        return response.status === 429 ? 'Too many attempts. Wait a minute and try again.' : response.statusText;
    }
}

export const api = {
    get: <T>(path: string) => request<T>('GET', path),
    post: <T = void>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
};

export type SetupStatus = { claimed: boolean; productName: string };

export type Me = { username: string; role: string; remainingRecoveryCodes: number };

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
