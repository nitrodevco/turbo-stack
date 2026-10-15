import { useMutation } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { api, errorMessage, type JournalEntry } from '../api';
import { Button, Card, ErrorNote } from './ui';

/** The service's live output with a prompt that types into its console (stdin), AMP-style. */
export function AppConsole({ appId, appName, canType }: { appId: number; appName: string; canType: boolean }) {
    const [lines, setLines] = useState<JournalEntry[]>([]);
    const [line, setLine] = useState('');
    const [history, setHistory] = useState<string[]>([]);
    const [historyIndex, setHistoryIndex] = useState(-1);
    const bottom = useRef<HTMLDivElement>(null);
    const send = useMutation({
        mutationFn: (command: string) => api.post(`/api/apps/${appId}/console`, { line: command }),
        onSuccess: (_, command) => {
            setHistory((h) => [command, ...h.filter((c) => c !== command)].slice(0, 50));
            setHistoryIndex(-1);
            setLine('');
        },
    });

    useEffect(() => {
        setLines([]);
        const source = new EventSource(`/api/logs/stream?unit=ts-app-${appName}.service&limit=200`);
        source.addEventListener('entry', (event) => {
            const entry = JSON.parse((event as MessageEvent).data) as JournalEntry;
            setLines((current) => [...current.slice(-999), entry]);
        });

        return () => source.close();
    }, [appName]);

    useEffect(() => {
        bottom.current?.scrollIntoView({ block: 'nearest' });
    }, [lines]);

    return (
        <Card className="space-y-3 p-5">
            <h2 className="font-medium">Console</h2>
            <div className="h-80 overflow-y-auto rounded-lg bg-slate-950 p-3 font-mono text-xs leading-relaxed text-slate-100">
                {lines.map((entry) => (
                    <div key={entry.cursor} className={entry.priority <= 3 ? 'text-red-300' : entry.priority === 4 ? 'text-amber-200' : ''}>
                        <span className="text-slate-500">{new Date(entry.timestampUsec / 1000).toLocaleTimeString()} </span>
                        {entry.message}
                    </div>
                ))}
                <div ref={bottom} />
            </div>
            {canType && (
                <form
                    className="flex gap-2"
                    onSubmit={(e) => {
                        e.preventDefault();
                        if (line.trim()) send.mutate(line.trim());
                    }}
                >
                    <input
                        className="flex-1 rounded-lg border border-slate-300 bg-white px-3 py-2 font-mono text-sm dark:border-slate-700 dark:bg-slate-900"
                        value={line}
                        onChange={(e) => setLine(e.target.value)}
                        onKeyDown={(e) => {
                            if (e.key === 'ArrowUp' && history.length > 0) {
                                const next = Math.min(historyIndex + 1, history.length - 1);
                                setHistoryIndex(next);
                                setLine(history[next]);
                                e.preventDefault();
                            } else if (e.key === 'ArrowDown') {
                                const next = historyIndex - 1;
                                setHistoryIndex(Math.max(next, -1));
                                setLine(next >= 0 ? history[next] : '');
                                e.preventDefault();
                            }
                        }}
                        placeholder="Type a command (↑ for history)"
                        maxLength={1000}
                        aria-label="Console command"
                    />
                    <Button type="submit" disabled={send.isPending || !line.trim()}>
                        Send
                    </Button>
                </form>
            )}
            <ErrorNote message={send.error ? errorMessage(send.error) : null} />
        </Card>
    );
}
