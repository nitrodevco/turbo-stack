import { useEffect, useRef, useState } from 'react';
import { Badge } from './ui';

/** Streams a background job's output (Server-Sent Events) and reports when it ends. */
export function JobOutput({ jobId, onEnd }: { jobId: number; onEnd?: (status: string) => void }) {
    const [lines, setLines] = useState<string[]>([]);
    const [status, setStatus] = useState('running');
    const bottomRef = useRef<HTMLDivElement>(null);
    const onEndRef = useRef(onEnd);
    onEndRef.current = onEnd;

    useEffect(() => {
        setLines([]);
        setStatus('running');
        const source = new EventSource(`/api/jobs/${jobId}/stream`);
        source.addEventListener('line', (event) => setLines((current) => [...current, (event as MessageEvent).data as string]));
        source.addEventListener('end', (event) => {
            const result = (event as MessageEvent).data as string;
            setStatus(result);
            source.close();
            onEndRef.current?.(result);
        });

        return () => source.close();
    }, [jobId]);

    useEffect(() => {
        bottomRef.current?.scrollIntoView({ block: 'end' });
    }, [lines.length]);

    return (
        <div className="space-y-2">
            <div className="flex items-center gap-2 text-sm">
                {status === 'running' && <Badge tone="info">Running…</Badge>}
                {status === 'succeeded' && <Badge tone="good">✓ Succeeded</Badge>}
                {status === 'failed' && <Badge tone="danger">✕ Failed</Badge>}
            </div>
            <div className="max-h-96 overflow-auto rounded-lg bg-slate-950 p-3 font-mono text-xs leading-relaxed whitespace-pre-wrap text-slate-200">
                {lines.join('\n') || 'Starting…'}
                <div ref={bottomRef} />
            </div>
        </div>
    );
}
