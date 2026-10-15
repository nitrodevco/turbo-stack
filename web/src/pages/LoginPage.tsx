import { useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useNavigate } from 'react-router';
import { api, ApiError } from '../api';
import { Button, Card, CenteredPage, ErrorNote, Field } from '../components/ui';
import { passkeysSupported, signInWithPasskey } from '../passkeys';

export function LoginPage() {
    const [stage, setStage] = useState<'password' | 'mfa'>('password');
    const [useRecovery, setUseRecovery] = useState(false);
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [code, setCode] = useState('');
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    async function submit(action: () => Promise<void>) {
        setBusy(true);
        setError(null);

        try {
            await action();
        } catch (e) {
            if (e instanceof ApiError && stage === 'mfa' && e.message.includes('expired')) {
                setStage('password');
            }

            // A cancelled passkey prompt is a DOMException; its message says what happened.
            setError(e instanceof ApiError || e instanceof DOMException ? e.message : 'Something went wrong.');
        } finally {
            setBusy(false);
        }
    }

    return (
        <CenteredPage>
            <Card className="space-y-5">
                <h1 className="text-xl font-semibold">{stage === 'password' ? 'Sign in' : 'Two-factor authentication'}</h1>
                {stage === 'password' ? (
                    <form
                        className="space-y-4"
                        onSubmit={(e) => {
                            e.preventDefault();
                            void submit(async () => {
                                await api.post('/api/auth/login', { username, password });
                                setStage('mfa');
                            });
                        }}
                    >
                        <Field label="Username" value={username} onChange={(e) => setUsername(e.target.value)} autoComplete="username" required />
                        <Field label="Password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="current-password" required />
                        <ErrorNote message={error} />
                        <Button type="submit" disabled={busy} className="w-full">
                            {busy ? 'Signing in…' : 'Continue'}
                        </Button>
                        {passkeysSupported() && (
                            <Button
                                type="button"
                                variant="secondary"
                                disabled={busy}
                                className="w-full"
                                onClick={() =>
                                    void submit(async () => {
                                        await signInWithPasskey();
                                        await queryClient.invalidateQueries();
                                        void navigate('/', { replace: true });
                                    })
                                }
                            >
                                Sign in with a passkey
                            </Button>
                        )}
                    </form>
                ) : (
                    <form
                        className="space-y-4"
                        onSubmit={(e) => {
                            e.preventDefault();
                            void submit(async () => {
                                await api.post('/api/auth/mfa', useRecovery ? { recoveryCode: code } : { code });
                                await queryClient.invalidateQueries();
                                void navigate('/', { replace: true });
                            });
                        }}
                    >
                        <Field
                            key={useRecovery ? 'recovery' : 'totp'}
                            label={useRecovery ? 'Recovery code' : '6-digit code from your authenticator'}
                            value={code}
                            onChange={(e) => setCode(e.target.value)}
                            inputMode={useRecovery ? 'text' : 'numeric'}
                            autoComplete="one-time-code"
                            autoFocus
                            required
                        />
                        <ErrorNote message={error} />
                        <Button type="submit" disabled={busy} className="w-full">
                            {busy ? 'Verifying…' : 'Sign in'}
                        </Button>
                        <button
                            type="button"
                            className="w-full text-center text-xs text-muted hover:underline"
                            onClick={() => {
                                setUseRecovery(!useRecovery);
                                setCode('');
                            }}
                        >
                            {useRecovery ? 'Use authenticator code instead' : 'Use a recovery code'}
                        </button>
                    </form>
                )}
            </Card>
        </CenteredPage>
    );
}
