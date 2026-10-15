import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { api, errorMessage, type FirewallOverview } from '../api';
import { Button } from './ui';

/**
 * Shown on every page while a risky firewall change waits for confirmation. Confirming sends a
 * fresh request, which proves the admin can still reach the panel; otherwise the change reverts.
 */
export function PendingFirewallBanner() {
    const queryClient = useQueryClient();
    const [now, setNow] = useState(() => Date.now());
    const [reverted, setReverted] = useState<string | null>(null);
    const previous = useRef<string | null>(null);
    const overview = useQuery({
        queryKey: ['firewall'],
        queryFn: () => api.get<FirewallOverview>('/api/firewall'),
        refetchInterval: (query) => (query.state.data?.pending ? 2_000 : 30_000),
        retry: false,
    });
    const confirm = useMutation({
        mutationFn: (id: string) => api.post<{ status: string }>(`/api/firewall/changes/${id}/confirm`),
        onSettled: () => queryClient.invalidateQueries({ queryKey: ['firewall'] }),
    });

    const pending = overview.data?.pending ?? null;

    useEffect(() => {
        if (!pending) return;

        const timer = setInterval(() => setNow(Date.now()), 500);

        return () => clearInterval(timer);
    }, [pending]);

    useEffect(() => {
        // A pending change that disappears without being confirmed here was reverted.
        if (previous.current && !pending && !confirm.isSuccess) {
            setReverted(previous.current);
        }

        previous.current = pending?.summary ?? null;
    }, [pending, confirm.isSuccess]);

    if (reverted) {
        return (
            <div role="status" className="flex flex-wrap items-center justify-between gap-3 border-b border-accent/40 bg-accent-soft px-3 py-3 text-sm text-ink sm:px-4 lg:px-6">
                <span>
                    The firewall change "{reverted}" was not confirmed and has been reverted.
                </span>
                <Button size="sm" variant="ghost" onClick={() => setReverted(null)}>
                    Dismiss
                </Button>
            </div>
        );
    }

    if (!pending) return null;

    const seconds = Math.max(0, Math.ceil((pending.revertAt - now) / 1000));

    return (
        <div role="alert" className="flex flex-wrap items-center justify-between gap-3 border-b border-warn-line bg-warn-soft px-3 py-3 text-sm text-warn sm:px-4 lg:px-6">
            <span>
                <strong>Firewall change applied: </strong>
                {pending.summary}. If you can still use the panel, keep it. It reverts automatically in <strong className="tabular-nums">{seconds}s</strong>.
            </span>
            <div className="flex items-center gap-2">
                {confirm.error && <span className="text-bad">{errorMessage(confirm.error)}</span>}
                <Button size="sm" disabled={confirm.isPending} onClick={() => confirm.mutate(pending.id)}>
                    Keep this change
                </Button>
            </div>
        </div>
    );
}
