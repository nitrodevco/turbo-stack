import { useQueryClient } from '@tanstack/react-query';
import QRCode from 'qrcode';
import { type FormEvent, useEffect, useRef, useState } from 'react';
import { useNavigate, useSearchParams } from 'react-router';
import { api, ApiError } from '../api';
import { Button, Card, CenteredPage, ErrorNote, Field } from '../components/ui';

type Step = 'token' | 'owner' | 'totp' | 'recovery';

const steps: { id: Step; label: string }[] = [
    { id: 'token', label: 'Verify' },
    { id: 'owner', label: 'Account' },
    { id: 'totp', label: 'Two-factor' },
    { id: 'recovery', label: 'Recovery' },
];

export function SetupPage() {
    const [params] = useSearchParams();
    const [step, setStep] = useState<Step>('token');
    const [enrollment, setEnrollment] = useState<{ secret: string; otpauthUri: string } | null>(null);
    const [recoveryCodes, setRecoveryCodes] = useState<string[]>([]);

    return (
        <CenteredPage>
            <StepIndicator current={step} />
            {step === 'token' && <TokenStep initialToken={params.get('token') ?? ''} onDone={() => setStep('owner')} />}
            {step === 'owner' && (
                <OwnerStep
                    onDone={(result) => {
                        setEnrollment(result);
                        setStep('totp');
                    }}
                />
            )}
            {step === 'totp' && enrollment && (
                <TotpStep
                    enrollment={enrollment}
                    onBack={() => setStep('owner')}
                    onDone={(codes) => {
                        setRecoveryCodes(codes);
                        setStep('recovery');
                    }}
                />
            )}
            {step === 'recovery' && <RecoveryStep codes={recoveryCodes} />}
        </CenteredPage>
    );
}

function StepIndicator({ current }: { current: Step }) {
    const currentIndex = steps.findIndex((s) => s.id === current);

    return (
        <ol className="flex gap-2 text-xs font-medium">
            {steps.map((s, i) => (
                <li key={s.id} className="flex-1 space-y-1.5">
                    <div className={`h-1 rounded-full ${i <= currentIndex ? 'bg-brand-600' : 'bg-slate-200 dark:bg-slate-800'}`} />
                    <span className={i === currentIndex ? 'text-slate-900 dark:text-slate-100' : 'text-slate-500'}>{s.label}</span>
                </li>
            ))}
        </ol>
    );
}

function useSubmit() {
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);

    async function run(action: () => Promise<void>) {
        setBusy(true);
        setError(null);

        try {
            await action();
        } catch (e) {
            setError(e instanceof ApiError ? e.message : 'Something went wrong. Check that the panel is running.');
        } finally {
            setBusy(false);
        }
    }

    return { error, busy, run };
}

function TokenStep({ initialToken, onDone }: { initialToken: string; onDone: () => void }) {
    const [token, setToken] = useState(initialToken);
    const { error, busy, run } = useSubmit();
    const autoSubmitted = useRef(false);

    const claim = (value: string) => run(async () => {
        await api.post('/api/setup/claim', { token: value.trim() });
        onDone();
    });

    useEffect(() => {
        // The link printed by the installer carries the token; use it straight away.
        if (initialToken && !autoSubmitted.current) {
            autoSubmitted.current = true;
            void claim(initialToken);
        }
    }, [initialToken]);

    return (
        <Card className="space-y-5">
            <div className="space-y-1">
                <h1 className="text-xl font-semibold">Welcome — let's set up your server</h1>
                <p className="text-sm text-slate-600 dark:text-slate-400">
                    Enter the setup token the install script printed. It proves you have access to the server.
                </p>
            </div>
            <form
                className="space-y-4"
                onSubmit={(e: FormEvent) => {
                    e.preventDefault();
                    void claim(token);
                }}
            >
                <Field label="Setup token" value={token} onChange={(e) => setToken(e.target.value)} autoComplete="off" spellCheck={false} required />
                <ErrorNote message={error} />
                <p className="text-xs text-slate-500">
                    Lost it? Run <code className="rounded bg-slate-100 px-1 py-0.5 dark:bg-slate-800">sudo tm-setup-token</code> on the server to get a new link.
                </p>
                <Button type="submit" disabled={busy || !token.trim()} className="w-full">
                    {busy ? 'Checking…' : 'Continue'}
                </Button>
            </form>
        </Card>
    );
}

function OwnerStep({ onDone }: { onDone: (result: { secret: string; otpauthUri: string }) => void }) {
    const [username, setUsername] = useState('');
    const [password, setPassword] = useState('');
    const [confirm, setConfirm] = useState('');
    const { error, busy, run } = useSubmit();
    const mismatch = confirm.length > 0 && password !== confirm;

    return (
        <Card className="space-y-5">
            <div className="space-y-1">
                <h1 className="text-xl font-semibold">Create the owner account</h1>
                <p className="text-sm text-slate-600 dark:text-slate-400">The owner has full control of this server. You can invite others later.</p>
            </div>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    void run(async () => onDone(await api.post('/api/setup/owner', { username, password })));
                }}
            >
                <Field label="Username" value={username} onChange={(e) => setUsername(e.target.value.toLowerCase())} autoComplete="username" required />
                <Field
                    label="Password"
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="new-password"
                    hint="At least 12 characters. A passphrase works well."
                    minLength={12}
                    required
                />
                <Field label="Confirm password" type="password" value={confirm} onChange={(e) => setConfirm(e.target.value)} autoComplete="new-password" required />
                <ErrorNote message={mismatch ? 'Passwords do not match.' : error} />
                <Button type="submit" disabled={busy || mismatch || !username || !password} className="w-full">
                    {busy ? 'Creating…' : 'Continue'}
                </Button>
            </form>
        </Card>
    );
}

function TotpStep({
    enrollment,
    onBack,
    onDone,
}: {
    enrollment: { secret: string; otpauthUri: string };
    onBack: () => void;
    onDone: (codes: string[]) => void;
}) {
    const [qr, setQr] = useState<string>();
    const [code, setCode] = useState('');
    const { error, busy, run } = useSubmit();

    useEffect(() => {
        void QRCode.toDataURL(enrollment.otpauthUri, { margin: 1, width: 200 }).then(setQr);
    }, [enrollment.otpauthUri]);

    return (
        <Card className="space-y-5">
            <div className="space-y-1">
                <h1 className="text-xl font-semibold">Turn on two-factor authentication</h1>
                <p className="text-sm text-slate-600 dark:text-slate-400">
                    Scan this with an authenticator app (1Password, Bitwarden, Google Authenticator…), then enter the 6-digit code.
                </p>
            </div>
            <div className="flex flex-col items-center gap-3">
                {qr && <img src={qr} alt="Authenticator QR code" className="size-48 rounded-lg bg-white p-2" />}
                <details className="w-full text-center text-xs text-slate-500">
                    <summary className="cursor-pointer">Can't scan? Enter the key manually</summary>
                    <code className="mt-2 block break-all rounded bg-slate-100 p-2 font-mono text-sm dark:bg-slate-800">{enrollment.secret}</code>
                </details>
            </div>
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    void run(async () => {
                        const result = await api.post<{ recoveryCodes: string[] }>('/api/setup/owner/confirm', { code });
                        onDone(result.recoveryCodes);
                    });
                }}
            >
                <Field label="6-digit code" value={code} onChange={(e) => setCode(e.target.value)} inputMode="numeric" autoComplete="one-time-code" maxLength={7} required />
                <ErrorNote message={error} />
                <div className="flex gap-2">
                    <Button type="button" variant="ghost" onClick={onBack}>
                        Back
                    </Button>
                    <Button type="submit" disabled={busy || code.replace(/\D/g, '').length !== 6} className="flex-1">
                        {busy ? 'Verifying…' : 'Verify and finish'}
                    </Button>
                </div>
            </form>
        </Card>
    );
}

function RecoveryStep({ codes }: { codes: string[] }) {
    const [saved, setSaved] = useState(false);
    const navigate = useNavigate();
    const queryClient = useQueryClient();
    const text = codes.join('\n');

    function download() {
        const url = URL.createObjectURL(new Blob([`Turbo Manager recovery codes (${location.host})\n\n${text}\n`], { type: 'text/plain' }));
        const link = Object.assign(document.createElement('a'), { href: url, download: 'turbo-manager-recovery-codes.txt' });
        link.click();
        URL.revokeObjectURL(url);
    }

    return (
        <Card className="space-y-5">
            <div className="space-y-1">
                <h1 className="text-xl font-semibold">Save your recovery codes</h1>
                <p className="text-sm text-slate-600 dark:text-slate-400">
                    Each code works once if you lose your authenticator. They won't be shown again.
                </p>
            </div>
            <ul className="grid grid-cols-2 gap-2 rounded-lg bg-slate-100 p-4 font-mono text-sm dark:bg-slate-800">
                {codes.map((c) => (
                    <li key={c}>{c}</li>
                ))}
            </ul>
            <div className="flex gap-2">
                <Button type="button" variant="ghost" onClick={() => void navigator.clipboard.writeText(text)}>
                    Copy
                </Button>
                <Button type="button" variant="ghost" onClick={download}>
                    Download
                </Button>
            </div>
            <label className="flex items-center gap-2 text-sm">
                <input type="checkbox" checked={saved} onChange={(e) => setSaved(e.target.checked)} className="size-4 accent-brand-600" />
                I've stored these somewhere safe
            </label>
            <Button
                className="w-full"
                disabled={!saved}
                onClick={async () => {
                    await queryClient.invalidateQueries();
                    void navigate('/', { replace: true });
                }}
            >
                Go to dashboard
            </Button>
        </Card>
    );
}
