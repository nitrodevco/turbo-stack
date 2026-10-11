import { useMutation, useQueryClient } from '@tanstack/react-query';
import { api, ApiError, type FirewallCommitResult } from './api';

type LockoutBody = { requiresAcknowledgement?: boolean; detail?: string; reasons?: string[] };

/**
 * Runs a firewall change. If the panel says it would block the admin's own IP, asks for explicit
 * confirmation and retries with acknowledgeLockout. Risky changes come back with a pending
 * confirmation, which the global banner picks up.
 */
export function useFirewallCommit<TInput>(send: (input: TInput, acknowledgeLockout: boolean) => Promise<FirewallCommitResult>) {
    const queryClient = useQueryClient();

    return useMutation({
        mutationFn: async (input: TInput) => {
            try {
                return await send(input, false);
            } catch (error) {
                const body = error instanceof ApiError ? (error.body as LockoutBody | undefined) : undefined;

                if (body?.requiresAcknowledgement) {
                    const reasons = (body.reasons ?? []).map((r) => `• ${r}`).join('\n');

                    if (confirm(`${body.detail}\n\n${reasons}\n\nApply it anyway? It reverts automatically unless you confirm afterwards.`)) {
                        return await send(input, true);
                    }

                    return null;
                }

                throw error;
            }
        },
        onSettled: () => queryClient.invalidateQueries({ queryKey: ['firewall'] }),
    });
}

export const firewallApi = {
    deleteRule: (id: number, acknowledgeLockout: boolean) =>
        api.delete<FirewallCommitResult>(`/api/firewall/rules/${id}?acknowledgeLockout=${acknowledgeLockout}`),
};
