import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage } from '../api';
import { usePermissions } from '../auth';
import { Badge, Button, Card, ErrorNote, Field, Notice, SelectField } from './ui';

type SftpAccount = { name: string; folder: string; mounted: boolean; password: boolean; keys: number };

/** SFTP-only logins locked into one folder of an app user's home (e.g. to upload game assets with FileZilla). */
export function SftpAccounts() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const accounts = useQuery({ queryKey: ['sftp'], queryFn: () => api.get<SftpAccount[]>('/api/sftp') });
    const users = useQuery({ queryKey: ['file-users'], queryFn: () => api.get<string[]>('/api/files/users'), enabled: can('files.manage') });
    const [shown, setShown] = useState<{ name: string; password: string } | null>(null);
    const [form, setForm] = useState({ name: '', user: '', path: 'sites/assets' });
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['sftp'] });

    const create = useMutation({
        mutationFn: () => api.post<{ password: string | null }>('/api/sftp', { ...form, user: form.user || users.data?.[0], password: true }),
        onSuccess: (data) => {
            if (data.password) setShown({ name: form.name, password: data.password });
            setForm({ ...form, name: '' });
            refresh();
        },
    });
    const reset = useMutation({
        mutationFn: (name: string) => api.post<{ password: string }>(`/api/sftp/${name}/password`),
        onSuccess: (data, name) => setShown({ name, password: data.password }),
    });
    const remove = useMutation({ mutationFn: (name: string) => api.delete(`/api/sftp/${name}`), onSuccess: refresh });
    const keys = useMutation({
        mutationFn: ({ name, value }: { name: string; value: string }) => api.put(`/api/sftp/${name}/keys`, { keys: value.split('\n') }),
        onSuccess: refresh,
    });

    return (
        <Card className="space-y-4 p-5">
            <div>
                <h2 className="font-medium">SFTP accounts</h2>
                <p className="text-sm text-slate-500">
                    Upload-only logins (FileZilla, WinSCP, sftp): locked into one folder, no shell. Connect to this server on port 22.
                </p>
            </div>
            {shown && (
                <Notice tone="success">
                    Password for <span className="font-medium">{shown.name}</span> (shown once): <code className="select-all">{shown.password}</code>
                </Notice>
            )}
            <ErrorNote message={[accounts.error, create.error, reset.error, remove.error, keys.error].find(Boolean) ? errorMessage([accounts.error, create.error, reset.error, remove.error, keys.error].find(Boolean)) : null} />
            <ul className="divide-y divide-slate-100 text-sm dark:divide-slate-800">
                {accounts.data?.map((a) => (
                    <li key={a.name} className="space-y-1 py-2">
                        <div className="flex items-center justify-between gap-2">
                            <span>
                                <span className="font-medium">{a.name}</span> → <code className="text-xs">{a.folder}</code>{' '}
                                {!a.mounted && <Badge tone="danger">folder not mounted</Badge>} {a.password && <Badge>password</Badge>} {a.keys > 0 && <Badge>{a.keys} key(s)</Badge>}
                            </span>
                            {can('ssh.manage') && (
                                <span className="whitespace-nowrap">
                                    <Button size="sm" variant="ghost" onClick={() => reset.mutate(a.name)}>
                                        New password
                                    </Button>
                                    <Button
                                        size="sm"
                                        variant="ghost"
                                        onClick={() => {
                                            const value = prompt('Public keys, one per line (replaces the current ones):');
                                            if (value !== null) keys.mutate({ name: a.name, value });
                                        }}
                                    >
                                        Keys
                                    </Button>
                                    <Button size="sm" variant="ghost" onClick={() => confirm(`Delete the SFTP account ${a.name}? The files stay.`) && remove.mutate(a.name)}>
                                        Delete
                                    </Button>
                                </span>
                            )}
                        </div>
                    </li>
                ))}
            </ul>
            {can('ssh.manage') && (users.data?.length ?? 0) > 0 && (
                <form
                    className="grid gap-3 border-t border-slate-100 pt-4 sm:grid-cols-[1fr_1fr_2fr_auto] sm:items-end dark:border-slate-800"
                    onSubmit={(e) => {
                        e.preventDefault();
                        create.mutate();
                    }}
                >
                    <Field label="Login" value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value.toLowerCase() })} placeholder="assets-upload" required />
                    <SelectField label="Folder of" value={form.user || users.data![0]} onChange={(e) => setForm({ ...form, user: e.target.value })}>
                        {users.data!.map((u) => (
                            <option key={u} value={u}>
                                {u}
                            </option>
                        ))}
                    </SelectField>
                    <Field label="Folder (in their home)" value={form.path} onChange={(e) => setForm({ ...form, path: e.target.value })} required />
                    <Button type="submit" disabled={create.isPending}>
                        Create
                    </Button>
                </form>
            )}
        </Card>
    );
}
