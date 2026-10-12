import '@xterm/xterm/css/xterm.css';
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, errorMessage } from '../api';
import { useSudo } from '../components/Sudo';
import { Button, Card, ErrorNote, Notice, PageHeader, SelectField, Spinner } from '../components/ui';

export function TerminalPage() {
    const [params, setParams] = useSearchParams();
    const users = useQuery({ queryKey: ['terminal-users'], queryFn: () => api.get<{ users: string[]; root: boolean }>('/api/terminal/users') });
    const [choice, setChoice] = useState(params.get('user') ?? '');
    const [session, setSession] = useState<{ user: string; key: number } | null>(null);
    const [error, setError] = useState<string | null>(null);
    const withSudo = useSudo();

    const options = [...(users.data?.users ?? []), ...(users.data?.root ? ['root'] : [])];
    const selected = choice || options[0] || '';

    async function connect() {
        setError(null);
        try {
            if (selected === 'root') await withSudo(() => api.post('/api/terminal/root-check'));
            setParams({ user: selected }, { replace: true });
            setSession({ user: selected, key: Date.now() });
        } catch (e) {
            if (!(e instanceof Error && e.message === 'Cancelled.')) setError(errorMessage(e));
        }
    }

    return (
        <div className="space-y-6">
            <PageHeader
                title="Terminal"
                description="A shell on the server as an app's Linux user (it can only touch that user's files). The owner can also open a root shell."
            />
            {users.isPending ? (
                <Spinner />
            ) : options.length === 0 ? (
                <Notice>There are no app users yet, or your role can't open a terminal.</Notice>
            ) : (
                <Card className="flex flex-wrap items-end gap-3 p-4">
                    <div className="w-60">
                        <SelectField label="Open a shell as" value={selected} onChange={(e) => setChoice(e.target.value)}>
                            {options.map((u) => (
                                <option key={u} value={u}>
                                    {u === 'root' ? 'root (whole server)' : u}
                                </option>
                            ))}
                        </SelectField>
                    </div>
                    <Button onClick={() => void connect()}>{session ? 'Reconnect' : 'Connect'}</Button>
                    {selected === 'root' && <span className="text-sm text-amber-700 dark:text-amber-400">Root can change anything on the server. Everything you open is in the activity log.</span>}
                </Card>
            )}
            <ErrorNote message={error} />
            {session && <TerminalView key={session.key} user={session.user} />}
        </div>
    );
}

function TerminalView({ user }: { user: string }) {
    const host = useRef<HTMLDivElement>(null);
    const [status, setStatus] = useState<'connecting' | 'open' | 'closed'>('connecting');

    useEffect(() => {
        const terminal = new Terminal({ cursorBlink: true, fontSize: 13, fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, monospace', theme: { background: '#020617' } });
        const fit = new FitAddon();
        terminal.loadAddon(fit);
        terminal.open(host.current!);
        fit.fit();

        const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
        const socket = new WebSocket(`${protocol}://${location.host}/api/terminal?user=${encodeURIComponent(user)}&cols=${terminal.cols}&rows=${terminal.rows}`);
        socket.binaryType = 'arraybuffer';
        const encoder = new TextEncoder();

        socket.onopen = () => {
            setStatus('open');
            terminal.focus();
        };
        socket.onmessage = (event) => {
            if (typeof event.data === 'string') {
                const message = JSON.parse(event.data) as { exit?: number };
                if (message.exit !== undefined) terminal.write(`\r\n\x1b[90m[the shell ended (${message.exit})]\x1b[0m\r\n`);
            } else {
                terminal.write(new Uint8Array(event.data as ArrayBuffer));
            }
        };
        socket.onclose = () => setStatus('closed');

        const input = terminal.onData((data) => socket.readyState === WebSocket.OPEN && socket.send(encoder.encode(data)));
        const resize = terminal.onResize(({ cols, rows }) => socket.readyState === WebSocket.OPEN && socket.send(JSON.stringify({ resize: { cols, rows } })));
        const onWindowResize = () => fit.fit();
        window.addEventListener('resize', onWindowResize);

        return () => {
            window.removeEventListener('resize', onWindowResize);
            input.dispose();
            resize.dispose();
            socket.close();
            terminal.dispose();
        };
    }, [user]);

    return (
        <Card className="space-y-2 p-3">
            <p className="text-xs text-slate-500">
                {user} · {status === 'connecting' ? 'connecting…' : status === 'open' ? 'connected' : 'disconnected'}
            </p>
            <div ref={host} className="h-[32rem] overflow-hidden rounded-lg bg-slate-950 p-2" />
        </Card>
    );
}
