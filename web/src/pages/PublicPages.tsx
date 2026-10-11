import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { api, errorMessage } from '../api';
import { Card, CenteredPage, ErrorNote, Notice } from '../components/ui';

/** Opened on the panel's new domain: proves DNS, TLS and the proxy work end to end. */
export function AddressCheckPage() {
    const [params] = useSearchParams();
    const [state, setState] = useState<'checking' | 'ok' | 'failed'>('checking');
    const [error, setError] = useState<string | null>(null);
    const sent = useRef(false);

    useEffect(() => {
        if (sent.current) return;
        sent.current = true;

        api.post('/api/panel-address/verify', { code: params.get('code') })
            .then(() => setState('ok'))
            .catch((e: unknown) => {
                setState('failed');
                setError(errorMessage(e));
            });
    }, [params]);

    return (
        <CenteredPage>
            <Card className="space-y-3">
                {state === 'checking' && <p>Checking this address…</p>}
                {state === 'ok' && (
                    <Notice tone="success">
                        <p className="font-medium">✓ This address works.</p>
                        <p className="mt-1">Go back to the setup tab and continue there. You can close this tab.</p>
                    </Notice>
                )}
                {state === 'failed' && <ErrorNote message={error} />}
            </Card>
        </CenteredPage>
    );
}

/** Signs in on the new address with a one-time link created on the old one. */
export function HandoverPage() {
    const [params] = useSearchParams();
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const [error, setError] = useState<string | null>(null);
    const sent = useRef(false);

    useEffect(() => {
        if (sent.current) return;
        sent.current = true;

        api.post('/api/auth/handover', { token: params.get('token') })
            .then(async () => {
                await queryClient.invalidateQueries();
                void navigate('/settings', { replace: true });
            })
            .catch((e: unknown) => setError(errorMessage(e)));
    }, [params, navigate, queryClient]);

    return (
        <CenteredPage>
            <Card className="space-y-3">
                {error ? (
                    <>
                        <ErrorNote message={error} />
                        <a href="/login" className="text-sm text-brand-700 underline dark:text-brand-100">
                            Sign in
                        </a>
                    </>
                ) : (
                    <p>Signing you in…</p>
                )}
            </Card>
        </CenteredPage>
    );
}
