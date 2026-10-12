import { createContext, useCallback, useContext, useRef, useState, type ReactNode } from 'react';
import { api, ApiError, errorMessage } from '../api';
import { Button, ErrorNote, Field, Modal } from './ui';

type WithSudo = <T>(action: () => Promise<T>) => Promise<T>;

const SudoContext = createContext<WithSudo>((action) => action());

/** Dangerous actions answer "confirm it's you"; this asks for a code, re-authenticates and runs the action again. */
export function SudoProvider({ children }: { children: ReactNode }) {
    const [open, setOpen] = useState(false);
    const [code, setCode] = useState('');
    const [useRecovery, setUseRecovery] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [busy, setBusy] = useState(false);
    const pending = useRef<{ resolve: () => void; reject: (reason: unknown) => void } | null>(null);

    const prompt = () =>
        new Promise<void>((resolve, reject) => {
            pending.current = { resolve, reject };
            setCode('');
            setError(null);
            setOpen(true);
        });

    const withSudo = useCallback<WithSudo>(async (action) => {
        try {
            return await action();
        } catch (e) {
            if (!(e instanceof ApiError && e.status === 403 && (e.body as { sudo?: boolean } | undefined)?.sudo)) throw e;
            await prompt();
            return await action();
        }
    }, []);

    async function confirm() {
        setBusy(true);
        setError(null);
        try {
            await api.post('/api/account/sudo', useRecovery ? { recoveryCode: code } : { code });
            setOpen(false);
            pending.current?.resolve();
        } catch (e) {
            setError(errorMessage(e));
        } finally {
            setBusy(false);
        }
    }

    function cancel() {
        setOpen(false);
        pending.current?.reject(new Error('Cancelled.'));
    }

    return (
        <SudoContext.Provider value={withSudo}>
            {children}
            <Modal open={open} title="Confirm it's you" onClose={cancel}>
                <form
                    className="space-y-4"
                    onSubmit={(e) => {
                        e.preventDefault();
                        void confirm();
                    }}
                >
                    <p className="text-sm text-slate-600 dark:text-slate-400">This needs your second factor again. You won't be asked for the next 10 minutes.</p>
                    <Field
                        label={useRecovery ? 'Recovery code' : '6-digit code'}
                        value={code}
                        onChange={(e) => setCode(e.target.value)}
                        inputMode={useRecovery ? 'text' : 'numeric'}
                        autoComplete="one-time-code"
                        autoFocus
                        required
                    />
                    <ErrorNote message={error} />
                    <div className="flex items-center justify-between gap-2">
                        <button type="button" className="text-xs text-slate-500 hover:underline" onClick={() => setUseRecovery(!useRecovery)}>
                            {useRecovery ? 'Use the authenticator app' : 'Use a recovery code'}
                        </button>
                        <div className="flex gap-2">
                            <Button type="button" variant="ghost" onClick={cancel}>
                                Cancel
                            </Button>
                            <Button type="submit" disabled={busy}>
                                {busy ? 'Checking…' : 'Confirm'}
                            </Button>
                        </div>
                    </div>
                </form>
            </Modal>
        </SudoContext.Provider>
    );
}

export const useSudo = () => useContext(SudoContext);
