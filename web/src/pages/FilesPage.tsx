import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { api, ApiError, errorMessage } from '../api';
import { Button, Card, ErrorNote, Notice, PageHeader, SelectField, Spinner } from '../components/ui';
import { formatBytes, formatRelative } from '../format';

type Entry = { name: string; type: 'd' | 'f' | 'l' | 'o'; size: number; mode: string; modified: number };

const join = (dir: string, name: string) => (dir ? `${dir}/${name}` : name);

export function FilesPage() {
    const [params, setParams] = useSearchParams();
    const users = useQuery({ queryKey: ['file-users'], queryFn: () => api.get<string[]>('/api/files/users') });
    const user = params.get('user') ?? users.data?.[0] ?? '';
    const path = params.get('path') ?? '';
    const open = (next: { user?: string; path?: string }) => setParams({ user: next.user ?? user, path: next.path ?? '' });

    return (
        <div className="space-y-6">
            <PageHeader title="Files" description="An app user's home folder, opened as that user: what you change here, they own." />
            {users.isPending ? (
                <Spinner />
            ) : !users.data?.length ? (
                <Notice>There are no app users yet, or your role can't manage files.</Notice>
            ) : (
                <>
                    <div className="w-60">
                        <SelectField label="User" value={user} onChange={(e) => open({ user: e.target.value })}>
                            {users.data.map((u) => (
                                <option key={u} value={u}>
                                    {u}
                                </option>
                            ))}
                        </SelectField>
                    </div>
                    <Browser key={`${user}:${path}`} user={user} path={path} onOpen={(p) => open({ path: p })} />
                </>
            )}
        </div>
    );
}

function Browser({ user, path, onOpen }: { user: string; path: string; onOpen: (path: string) => void }) {
    const queryClient = useQueryClient();
    const listing = useQuery({
        queryKey: ['files', user, path],
        queryFn: () => api.get<{ path: string; entries: Entry[] }>(`/api/files?user=${encodeURIComponent(user)}&path=${encodeURIComponent(path)}`),
        retry: false,
    });
    const [editing, setEditing] = useState<string | null>(null);
    const [error, setError] = useState<string | null>(null);
    const upload = useRef<HTMLInputElement>(null);
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['files', user, path] });
    const run = async (action: () => Promise<unknown>) => {
        setError(null);
        try {
            await action();
            refresh();
        } catch (e) {
            setError(errorMessage(e));
        }
    };
    const crumbs = path ? path.split('/') : [];

    // Opening a file shows the editor; listing a file path (not a folder) fails, so open it as a file instead.
    if (listing.error instanceof ApiError && path && !editing) {
        return <Editor user={user} path={path} onClose={() => onOpen(crumbs.slice(0, -1).join('/'))} />;
    }

    if (editing) return <Editor user={user} path={editing} onClose={() => setEditing(null)} />;

    return (
        <Card className="space-y-3 p-4">
            <div className="flex flex-wrap items-center justify-between gap-2">
                <nav className="flex flex-wrap items-center gap-1 text-sm">
                    <button type="button" className="font-medium text-brand-700 hover:underline dark:text-brand-100" onClick={() => onOpen('')}>
                        ~{user === 'root' ? '' : ` (${user})`}
                    </button>
                    {crumbs.map((crumb, i) => (
                        <span key={i}>
                            /{' '}
                            <button type="button" className="text-brand-700 hover:underline dark:text-brand-100" onClick={() => onOpen(crumbs.slice(0, i + 1).join('/'))}>
                                {crumb}
                            </button>
                        </span>
                    ))}
                </nav>
                <div className="flex gap-2">
                    <Button size="sm" variant="ghost" onClick={() => setEditing(join(path, prompt('New file name:') ?? '') || null)}>
                        New file
                    </Button>
                    <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => {
                            const name = prompt('New folder name:');
                            if (name) void run(() => api.post('/api/files/folder', { user, path: join(path, name) }));
                        }}
                    >
                        New folder
                    </Button>
                    <Button size="sm" variant="secondary" onClick={() => upload.current?.click()}>
                        Upload
                    </Button>
                    <input
                        ref={upload}
                        type="file"
                        multiple
                        hidden
                        onChange={(e) => {
                            const chosen = [...(e.target.files ?? [])];
                            e.target.value = '';
                            void run(async () => {
                                for (const file of chosen) {
                                    const response = await fetch(`/api/files/upload?user=${encodeURIComponent(user)}&path=${encodeURIComponent(join(path, file.name))}`, {
                                        method: 'POST',
                                        headers: { 'X-TM-Request': '1', 'Content-Type': 'application/octet-stream' },
                                        body: file,
                                    });
                                    if (!response.ok) throw new Error(`Uploading ${file.name} failed (${response.status}).`);
                                }
                            });
                        }}
                    />
                </div>
            </div>
            <ErrorNote message={error ?? (listing.error ? errorMessage(listing.error) : null)} />
            {listing.isPending ? (
                <Spinner />
            ) : listing.data?.entries.length === 0 ? (
                <p className="py-6 text-center text-sm text-slate-500">This folder is empty.</p>
            ) : (
                <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                    {listing.data?.entries.map((entry) => {
                        const full = join(path, entry.name);
                        return (
                            <li key={entry.name} className="flex items-center gap-3 py-1.5">
                                <span className="w-5 text-center">{entry.type === 'd' ? '📁' : entry.type === 'l' ? '🔗' : '📄'}</span>
                                <button
                                    type="button"
                                    className="flex-1 truncate text-left hover:underline"
                                    onClick={() => (entry.type === 'd' ? onOpen(full) : setEditing(full))}
                                >
                                    {entry.name}
                                </button>
                                <span className="w-20 text-right text-xs text-slate-500">{entry.type === 'f' ? formatBytes(entry.size) : ''}</span>
                                <span className="hidden w-28 text-xs text-slate-500 sm:inline">{entry.modified ? formatRelative(new Date(entry.modified).toISOString()) : ''}</span>
                                <span className="w-10 font-mono text-xs text-slate-500">{entry.mode}</span>
                                <span className="flex gap-1">
                                    {entry.type === 'f' && (
                                        <a
                                            className="rounded px-2 py-1 text-xs hover:bg-slate-100 dark:hover:bg-slate-800"
                                            href={`/api/files/download?user=${encodeURIComponent(user)}&path=${encodeURIComponent(full)}`}
                                        >
                                            Download
                                        </a>
                                    )}
                                    <Button
                                        size="sm"
                                        variant="ghost"
                                        onClick={() => {
                                            const name = prompt('New name:', entry.name);
                                            if (name && name !== entry.name) void run(() => api.post('/api/files/move', { user, from: full, to: join(path, name) }));
                                        }}
                                    >
                                        Rename
                                    </Button>
                                    <Button
                                        size="sm"
                                        variant="ghost"
                                        onClick={() =>
                                            confirm(`Delete ${entry.name}${entry.type === 'd' ? ' and everything in it' : ''}?`) &&
                                            void run(() => api.delete(`/api/files?user=${encodeURIComponent(user)}&path=${encodeURIComponent(full)}`))
                                        }
                                    >
                                        Delete
                                    </Button>
                                </span>
                            </li>
                        );
                    })}
                </ul>
            )}
        </Card>
    );
}

function Editor({ user, path, onClose }: { user: string; path: string; onClose: () => void }) {
    const file = useQuery({
        queryKey: ['file', user, path],
        queryFn: () =>
            api.get<{ content: string | null; binary: boolean; size: number }>(`/api/files/content?user=${encodeURIComponent(user)}&path=${encodeURIComponent(path)}`),
        retry: false,
    });
    const [text, setText] = useState<string | null>(null);
    const content = text ?? file.data?.content ?? '';
    const isNew = file.error instanceof ApiError;
    const save = useMutation({ mutationFn: () => api.put('/api/files/content', { user, path, content }) });

    return (
        <Card className="space-y-3 p-4">
            <div className="flex items-center justify-between gap-2">
                <p className="truncate font-mono text-sm">
                    ~/{path} {isNew && <span className="text-slate-500">(new)</span>}
                </p>
                <div className="flex gap-2">
                    <Button size="sm" variant="ghost" onClick={onClose}>
                        Close
                    </Button>
                    {!file.data?.binary && (
                        <Button size="sm" onClick={() => save.mutate()} disabled={save.isPending}>
                            {save.isSuccess && text === null ? '✓ Saved' : 'Save'}
                        </Button>
                    )}
                </div>
            </div>
            {file.isPending ? (
                <Spinner />
            ) : file.data?.binary ? (
                <Notice>
                    This isn't a text file ({formatBytes(file.data.size)}).{' '}
                    <a className="underline" href={`/api/files/download?user=${encodeURIComponent(user)}&path=${encodeURIComponent(path)}`}>
                        Download it
                    </a>
                    .
                </Notice>
            ) : (
                <textarea
                    className="h-[28rem] w-full rounded-lg border border-slate-300 bg-white p-3 font-mono text-xs dark:border-slate-700 dark:bg-slate-950"
                    value={content}
                    spellCheck={false}
                    onChange={(e) => setText(e.target.value)}
                    onKeyDown={(e) => {
                        if ((e.ctrlKey || e.metaKey) && e.key === 's') {
                            e.preventDefault();
                            save.mutate();
                        }
                    }}
                />
            )}
            <ErrorNote message={save.error ? errorMessage(save.error) : file.error && !isNew ? errorMessage(file.error) : null} />
        </Card>
    );
}
