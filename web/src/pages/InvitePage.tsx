import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, errorMessage } from '../api';
import { Button, Card, CenteredPage, ErrorNote, Field, Spinner } from '../components/ui';
import { RecoveryStep, TotpStep } from './SetupPage';

/** /invite?token=…: a new member sets their password and authenticator. */
export function InvitePage() {
    const [params] = useSearchParams();
    const token = params.get('token') ?? '';
    const invite = useQuery({ queryKey: ['invite', token], queryFn: () => api.get<{ username: string; role: string; invitedBy: string | null }>(`/api/invite/${token}`), retry: false });
    const [password, setPassword] = useState('');
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [enrollment, setEnrollment] = useState<{ secret: string; otpauthUri: string } | null>(null);
    const [codes, setCodes] = useState<string[] | null>(null);

    if (invite.isPending) return <CenteredPage><Spinner /></CenteredPage>;
    if (invite.error) return <CenteredPage><Card className="p-6"><ErrorNote message={errorMessage(invite.error)} /></Card></CenteredPage>;
    if (codes) return <CenteredPage><RecoveryStep codes={codes} /></CenteredPage>;

    if (enrollment) {
        return (
            <CenteredPage>
                <TotpStep
                    enrollment={enrollment}
                    confirm={async (code) => (await api.post<{ recoveryCodes: string[] }>('/api/invite/confirm', { token, code })).recoveryCodes}
                    onBack={() => setEnrollment(null)}
                    onDone={setCodes}
                />
            </CenteredPage>
        );
    }

    return (
        <CenteredPage>
            <Card className="space-y-5">
                <div className="space-y-1">
                    <h1 className="text-xl font-semibold">Welcome, {invite.data.username}</h1>
                    <p className="text-sm text-muted">
                        {invite.data.invitedBy ? `${invite.data.invitedBy} invited you` : 'You were invited'} as {invite.data.role}. Choose a password, then set up your authenticator app.
                    </p>
                </div>
                <form
                    className="space-y-4"
                    onSubmit={async (e) => {
                        e.preventDefault();
                        setBusy(true);
                        setError(null);
                        try {
                            setEnrollment(await api.post<{ secret: string; otpauthUri: string }>('/api/invite/password', { token, password }));
                        } catch (err) {
                            setError(errorMessage(err));
                        } finally {
                            setBusy(false);
                        }
                    }}
                >
                    <Field label="Password" type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" minLength={12} required hint="At least 12 characters." />
                    <ErrorNote message={error} />
                    <Button type="submit" disabled={busy} className="w-full">
                        Continue
                    </Button>
                </form>
            </Card>
        </CenteredPage>
    );
}
