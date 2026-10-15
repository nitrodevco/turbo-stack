import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useRef, useState } from 'react';
import {
    api,
    ApiError,
    errorMessage,
    type BackupScheduleView,
    type DatabaseBackup,
    type DatabaseCredentials,
    type DatabaseEngineSlug,
    type DatabaseEngineView,
    type DatabaseInfo,
    type DatabaseUserView,
} from '../api';
import { usePermissions } from '../auth';
import { JobOutput } from '../components/JobOutput';
import { Badge, Button, Card, ErrorNote, Field, Modal, Notice, PageHeader, Select, SelectField, Spinner, TableWrap, Tabs } from '../components/ui';
import { formatBytes, formatDateTime } from '../format';

export function DatabasesPage() {
    const engines = useQuery({ queryKey: ['db-engines'], queryFn: () => api.get<DatabaseEngineView[]>('/api/databases/engines') });
    const [selected, setSelected] = useState<DatabaseEngineSlug | null>(null);
    // Open on an installed engine, else on the one recommended for this server.
    const current = engines.data?.find((e) => e.engine === (selected ?? engines.data.find((x) => x.installed)?.engine ?? engines.data.find((x) => x.recommended)?.engine ?? 'mysql'));

    return (
        <div className="space-y-6">
            <PageHeader title="Databases" description="MySQL, MariaDB and PostgreSQL on this server. They only listen locally; apps on this server connect to 127.0.0.1." />
            <ErrorNote message={engines.error ? errorMessage(engines.error) : null} />
            {engines.data && current && (
                <>
                    <Tabs<DatabaseEngineSlug>
                        tabs={engines.data.map((e) => ({ id: e.engine, label: `${e.label}${e.installed ? '' : e.recommended ? ' (recommended)' : ' (not installed)'}` }))}
                        value={current.engine}
                        onChange={setSelected}
                    />
                    {current.installed ? <EngineView engine={current} /> : <InstallCard engine={current} />}
                </>
            )}
            {engines.isPending && <Spinner />}
        </div>
    );
}

function InstallCard({ engine }: { engine: DatabaseEngineView }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const install = useMutation({ mutationFn: () => api.post<{ jobId: number }>(`/api/databases/${engine.engine}/install`) });

    return (
        <Card className="space-y-4 p-5">
            <p>
                <strong>{engine.label}</strong> {engine.candidateVersion && <span className="text-muted">{engine.candidateVersion.split('-')[0]}</span>} is not installed.
                {engine.recommended && ' This is the database to use for Turbo on this server.'}
            </p>
            {engine.recommended && <Badge tone="good">✓ Recommended</Badge>}
            {engine.note && <Notice tone={engine.engine === 'postgres' ? 'info' : 'warning'}>{engine.note}</Notice>}
            {engine.blockedReason ? (
                <Notice tone="info">{engine.blockedReason}</Notice>
            ) : (
                can('databases.manage') &&
                !install.data && (
                    <Button disabled={install.isPending} onClick={() => install.mutate()}>
                        Install {engine.label}
                    </Button>
                )
            )}
            <ErrorNote message={install.error ? errorMessage(install.error) : null} />
            {install.data && <JobOutput jobId={install.data.jobId} onEnd={() => void queryClient.invalidateQueries({ queryKey: ['db-engines'] })} />}
        </Card>
    );
}

function EngineView({ engine }: { engine: DatabaseEngineView }) {
    const slug = engine.engine;
    const databases = useQuery({ queryKey: ['dbs', slug], queryFn: () => api.get<DatabaseInfo[]>(`/api/databases/${slug}/databases`) });
    const users = useQuery({ queryKey: ['db-users', slug], queryFn: () => api.get<DatabaseUserView[]>(`/api/databases/${slug}/users`) });
    const [credentials, setCredentials] = useState<DatabaseCredentials | { user: string; password: string } | null>(null);
    const [jobId, setJobId] = useState<number | null>(null);
    const queryClient = useQueryClient();

    return (
        <div className="space-y-6">
            <div className="flex flex-wrap items-center gap-3 text-sm">
                {engine.running ? <Badge tone="good">● Running</Badge> : <Badge tone="danger">✕ Stopped</Badge>}
                <span className="text-muted">
                    {engine.label} {engine.version} · 127.0.0.1:{engine.port} · {engine.service}
                </span>
            </div>
            {engine.note && engine.engine !== 'postgres' && <Notice tone="warning">⚠ {engine.note}</Notice>}
            <ErrorNote message={databases.error ? errorMessage(databases.error) : users.error ? errorMessage(users.error) : null} />
            {jobId && (
                <Card className="p-5">
                    <JobOutput
                        jobId={jobId}
                        onEnd={() => {
                            void queryClient.invalidateQueries({ queryKey: ['dbs', slug] });
                            void queryClient.invalidateQueries({ queryKey: ['db-backups'] });
                        }}
                    />
                </Card>
            )}
            <DatabasesSection engine={engine} databases={databases.data} users={users.data ?? []} onCredentials={setCredentials} onJob={setJobId} />
            <UsersSection engine={engine} users={users.data} databases={databases.data ?? []} onCredentials={setCredentials} />
            <BackupsSection engine={engine} databases={databases.data ?? []} onJob={setJobId} />
            <ConnectHelp engine={engine} />
            {credentials && <CredentialsModal engine={engine} credentials={credentials} onClose={() => setCredentials(null)} />}
        </div>
    );
}

function DatabasesSection({
    engine,
    databases,
    users,
    onCredentials,
    onJob,
}: {
    engine: DatabaseEngineView;
    databases: DatabaseInfo[] | undefined;
    users: DatabaseUserView[];
    onCredentials: (c: DatabaseCredentials) => void;
    onJob: (id: number) => void;
}) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const slug = engine.engine;
    const [name, setName] = useState('');
    const [withUser, setWithUser] = useState(true);
    const [scheduleFor, setScheduleFor] = useState<string | null>(null);
    const refresh = () => {
        void queryClient.invalidateQueries({ queryKey: ['dbs', slug] });
        void queryClient.invalidateQueries({ queryKey: ['db-users', slug] });
        void queryClient.invalidateQueries({ queryKey: ['db-backups'] });
    };
    const create = useMutation({
        mutationFn: () => api.post<{ credentials: DatabaseCredentials | null }>(`/api/databases/${slug}/databases`, { name, user: withUser ? name : null }),
        onSuccess: (data) => {
            setName('');
            refresh();
            if (data.credentials) onCredentials(data.credentials);
        },
    });
    const drop = useMutation({ mutationFn: (database: string) => api.delete<{ finalBackup: string | null }>(`/api/databases/${slug}/databases/${database}`), onSuccess: refresh });
    const backup = useMutation({ mutationFn: (database: string) => api.post<{ jobId: number }>(`/api/databases/${slug}/databases/${database}/backup`), onSuccess: (d) => onJob(d.jobId) });

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">Databases</h2>
            <ErrorNote message={[create.error, drop.error, backup.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {drop.data?.finalBackup && <Notice tone="info">Dropped. A final backup was kept: {drop.data.finalBackup}</Notice>}
            {!databases ? (
                <Spinner />
            ) : databases.length === 0 ? (
                <p className="text-sm text-muted">No databases yet.</p>
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Database</th>
                            <th className="text-right">Size</th>
                            <th>Users with access</th>
                            <th />
                        </tr>
                    </thead>
                    <tbody>
                        {databases.map((database) => (
                            <tr key={database.name}>
                                <td>
                                    <p className="font-medium">{database.name}</p>
                                    {database.tables >= 0 && <p className="text-xs text-muted">{database.tables} tables</p>}
                                </td>
                                <td className="text-right tabular-nums">{formatBytes(database.sizeBytes)}</td>
                                <td>
                                    {users
                                        .filter((u) => u.grants.some((g) => g.database === database.name))
                                        .map((u) => (
                                            <span key={u.name} className="mr-2">
                                                <Badge>
                                                    {u.name}
                                                    {u.grants.find((g) => g.database === database.name)?.level === 'readonly' && ' (read-only)'}
                                                </Badge>
                                            </span>
                                        ))}
                                </td>
                                <td>
                                    {can('databases.manage') && (
                                        <div className="flex justify-end gap-1">
                                            <Button size="sm" variant="ghost" disabled={backup.isPending} onClick={() => backup.mutate(database.name)}>
                                                Back up
                                            </Button>
                                            <Button size="sm" variant="ghost" onClick={() => setScheduleFor(database.name)}>
                                                Schedule
                                            </Button>
                                            <Button
                                                size="sm"
                                                variant="ghost"
                                                disabled={drop.isPending}
                                                onClick={() => prompt(`This deletes "${database.name}" and all its data (a final backup is kept). Type the name to confirm:`) === database.name && drop.mutate(database.name)}
                                            >
                                                Delete
                                            </Button>
                                        </div>
                                    )}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </TableWrap>
            )}
            {can('databases.manage') && (
                <form
                    className="flex flex-wrap items-end gap-3"
                    onSubmit={(e) => {
                        e.preventDefault();
                        create.mutate();
                    }}
                >
                    <div className="w-56">
                        <Field label="New database" value={name} onChange={(e) => setName(e.target.value.toLowerCase())} placeholder="turbo" pattern="[a-z][a-z0-9_]{0,31}" title="Lowercase letters, digits and underscores" required />
                    </div>
                    <label className="flex items-center gap-2 pb-2 text-sm">
                        <input type="checkbox" className="size-4 accent-brand-600" checked={withUser} onChange={(e) => setWithUser(e.target.checked)} />
                        Also create a user "{name || 'name'}" with full access
                    </label>
                    <Button type="submit" disabled={!name || create.isPending}>
                        {create.isPending ? 'Creating…' : 'Create'}
                    </Button>
                </form>
            )}
            {scheduleFor && <ScheduleModal engine={slug} database={scheduleFor} onClose={() => setScheduleFor(null)} />}
        </Card>
    );
}

function UsersSection({
    engine,
    users,
    databases,
    onCredentials,
}: {
    engine: DatabaseEngineView;
    users: DatabaseUserView[] | undefined;
    databases: DatabaseInfo[];
    onCredentials: (c: { user: string; password: string }) => void;
}) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const slug = engine.engine;
    const [editing, setEditing] = useState<DatabaseUserView | null>(null);
    const [newUser, setNewUser] = useState('');
    const refresh = () => void queryClient.invalidateQueries({ queryKey: ['db-users', slug] });
    const reveal = useMutation({ mutationFn: (user: string) => api.get<{ user: string; password: string }>(`/api/databases/${slug}/users/${user}/password`), onSuccess: onCredentials });
    const reset = useMutation({
        mutationFn: (user: string) => api.put<{ user: string; password: string }>(`/api/databases/${slug}/users/${user}/password`, {}),
        onSuccess: (data) => {
            refresh();
            onCredentials(data);
        },
    });
    const create = useMutation({
        mutationFn: () => api.post<{ user: string; password: string }>(`/api/databases/${slug}/users`, { name: newUser }),
        onSuccess: (data) => {
            setNewUser('');
            refresh();
            onCredentials(data);
        },
    });
    const drop = useMutation({ mutationFn: (user: string) => api.delete(`/api/databases/${slug}/users/${user}`), onSuccess: refresh });

    return (
        <Card className="space-y-4 p-5">
            <h2 className="font-medium">Users</h2>
            <ErrorNote message={[reveal.error, reset.error, create.error, drop.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {!users ? (
                <Spinner />
            ) : users.length === 0 ? (
                <p className="text-sm text-muted">No users yet.</p>
            ) : (
                <ul className="divide-y divide-line">
                    {users.map((user) => (
                        <li key={user.name} className="flex flex-wrap items-center justify-between gap-2 py-2.5">
                            <div>
                                <p className="font-medium">{user.name}</p>
                                <p className="text-xs text-muted">
                                    {user.grants.length === 0 ? 'No database access' : user.grants.map((g) => `${g.database}${g.level === 'readonly' ? ' (read-only)' : ''}`).join(', ')}
                                </p>
                            </div>
                            {can('databases.manage') && (
                                <div className="flex gap-1">
                                    {user.passwordStored && (
                                        <Button size="sm" variant="ghost" disabled={reveal.isPending} onClick={() => reveal.mutate(user.name)}>
                                            Show password
                                        </Button>
                                    )}
                                    <Button size="sm" variant="ghost" disabled={reset.isPending} onClick={() => confirm(`Give ${user.name} a new random password? Apps using the old one stop connecting until updated.`) && reset.mutate(user.name)}>
                                        New password
                                    </Button>
                                    <Button size="sm" variant="ghost" onClick={() => setEditing(user)}>
                                        Access
                                    </Button>
                                    <Button size="sm" variant="ghost" disabled={drop.isPending} onClick={() => confirm(`Delete user ${user.name}?`) && drop.mutate(user.name)}>
                                        Delete
                                    </Button>
                                </div>
                            )}
                        </li>
                    ))}
                </ul>
            )}
            {can('databases.manage') && (
                <form
                    className="flex flex-wrap items-end gap-3"
                    onSubmit={(e) => {
                        e.preventDefault();
                        create.mutate();
                    }}
                >
                    <div className="w-56">
                        <Field label="New user" value={newUser} onChange={(e) => setNewUser(e.target.value.toLowerCase())} pattern="[a-z][a-z0-9_]{0,31}" required />
                    </div>
                    <Button type="submit" variant="secondary" disabled={!newUser || create.isPending}>
                        Create user
                    </Button>
                </form>
            )}
            {editing && <AccessModal engine={engine} user={editing} databases={databases} onClose={() => { setEditing(null); refresh(); }} />}
        </Card>
    );
}

function AccessModal({ engine, user, databases, onClose }: { engine: DatabaseEngineView; user: DatabaseUserView; databases: DatabaseInfo[]; onClose: () => void }) {
    const [levels, setLevels] = useState<Record<string, string>>(() =>
        Object.fromEntries(databases.map((d) => [d.name, user.grants.find((g) => g.database === d.name)?.level ?? 'none'])),
    );
    const save = useMutation({
        mutationFn: async () => {
            for (const database of databases) {
                const before = user.grants.find((g) => g.database === database.name)?.level ?? 'none';

                if (levels[database.name] !== before) {
                    await api.put(`/api/databases/${engine.engine}/users/${user.name}/grants`, { database: database.name, level: levels[database.name] });
                }
            }
        },
        onSuccess: onClose,
    });

    return (
        <Modal open title={`Access for ${user.name}`} onClose={onClose}>
            <div className="space-y-3">
                {databases.map((database) => (
                    <div key={database.name} className="flex items-center justify-between gap-3">
                        <span className="font-medium">{database.name}</span>
                        <Select className="w-48" value={levels[database.name]} onChange={(e) => setLevels({ ...levels, [database.name]: e.target.value })}>
                            <option value="none">No access</option>
                            {engine.engine !== 'postgres' && <option value="readonly">Read-only</option>}
                            <option value="all">Full access</option>
                        </Select>
                    </div>
                ))}
                {databases.length === 0 && <p className="text-sm text-muted">Create a database first.</p>}
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button disabled={save.isPending} onClick={() => save.mutate()}>
                        Save
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

function BackupsSection({ engine, databases, onJob }: { engine: DatabaseEngineView; databases: DatabaseInfo[]; onJob: (id: number) => void }) {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const slug = engine.engine;
    const backups = useQuery({ queryKey: ['db-backups', slug], queryFn: () => api.get<DatabaseBackup[]>(`/api/databases/backups?engine=${slug}`) });
    const [restoring, setRestoring] = useState<DatabaseBackup | null>(null);
    const fileInput = useRef<HTMLInputElement>(null);
    const remove = useMutation({
        mutationFn: (id: string) => api.delete(`/api/databases/backups?id=${encodeURIComponent(id)}`),
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['db-backups'] }),
    });
    const upload = useMutation({
        mutationFn: async (file: File) => {
            const response = await fetch(`/api/databases/backups/upload?name=${encodeURIComponent(file.name)}`, {
                method: 'POST',
                headers: { 'X-TS-Request': '1', 'Content-Type': 'application/octet-stream' },
                body: file,
            });

            if (!response.ok) throw new ApiError(response.status, response.status === 413 ? 'The file is too large to upload here.' : `Upload failed (${response.status}).`);
        },
        onSuccess: () => queryClient.invalidateQueries({ queryKey: ['db-backups'] }),
    });

    return (
        <Card className="space-y-4 p-5">
            <div className="flex flex-wrap items-center justify-between gap-3">
                <h2 className="font-medium">Backups and imports</h2>
                {can('databases.manage') && (
                    <>
                        <input ref={fileInput} type="file" accept=".sql,.gz" className="hidden" onChange={(e) => e.target.files?.[0] && upload.mutate(e.target.files[0])} />
                        <Button size="sm" variant="secondary" disabled={upload.isPending} onClick={() => fileInput.current?.click()}>
                            {upload.isPending ? 'Uploading…' : 'Upload .sql / .sql.gz'}
                        </Button>
                    </>
                )}
            </div>
            <ErrorNote message={[backups.error, remove.error, upload.error].filter(Boolean).map(errorMessage)[0] ?? null} />
            {(backups.data ?? []).length === 0 ? (
                <p className="text-sm text-muted">No backups yet.</p>
            ) : (
                <TableWrap>
                    <tbody>
                        {backups.data!.map((backup) => (
                            <tr key={backup.id}>
                                <td>
                                    <p className="font-mono text-xs">{backup.fileName}</p>
                                    <p className="text-xs text-muted">{backup.import ? 'Uploaded import' : backup.database}</p>
                                </td>
                                <td className="text-right tabular-nums">{formatBytes(backup.sizeBytes)}</td>
                                <td className="text-muted">{formatDateTime(backup.createdUnixMs)}</td>
                                <td>
                                    <div className="flex justify-end gap-1">
                                        <a className="rounded-lg px-2.5 py-1 text-xs font-medium text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800" href={`/api/databases/backups/download?id=${encodeURIComponent(backup.id)}`}>
                                            Download
                                        </a>
                                        {can('databases.manage') && (
                                            <>
                                                <Button size="sm" variant="ghost" onClick={() => setRestoring(backup)}>
                                                    Restore…
                                                </Button>
                                                <Button size="sm" variant="ghost" onClick={() => confirm(`Delete ${backup.fileName}?`) && remove.mutate(backup.id)}>
                                                    Delete
                                                </Button>
                                            </>
                                        )}
                                    </div>
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </TableWrap>
            )}
            {restoring && <RestoreModal engine={engine} backup={restoring} databases={databases} onClose={() => setRestoring(null)} onJob={onJob} />}
        </Card>
    );
}

function RestoreModal({ engine, backup, databases, onClose, onJob }: { engine: DatabaseEngineView; backup: DatabaseBackup; databases: DatabaseInfo[]; onClose: () => void; onJob: (id: number) => void }) {
    const [database, setDatabase] = useState(backup.database || databases[0]?.name || '');
    const restore = useMutation({
        mutationFn: () => api.post<{ jobId: number }>(`/api/databases/${engine.engine}/restore`, { backupId: backup.id, database }),
        onSuccess: (data) => {
            onJob(data.jobId);
            onClose();
        },
    });

    return (
        <Modal open title={`Restore ${backup.fileName}`} onClose={onClose}>
            <div className="space-y-4">
                <SelectField label="Into database" value={database} onChange={(e) => setDatabase(e.target.value)}>
                    {databases.map((d) => (
                        <option key={d.name} value={d.name}>
                            {d.name}
                        </option>
                    ))}
                </SelectField>
                <Notice tone="warning">⚠ Everything currently in "{database}" is replaced by the backup. Stop apps using it first.</Notice>
                <ErrorNote message={restore.error ? errorMessage(restore.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button variant="danger" disabled={!database || restore.isPending} onClick={() => restore.mutate()}>
                        Replace and restore
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

function ScheduleModal({ engine, database, onClose }: { engine: DatabaseEngineSlug; database: string; onClose: () => void }) {
    const queryClient = useQueryClient();
    const schedules = useQuery({ queryKey: ['db-schedules'], queryFn: () => api.get<BackupScheduleView[]>('/api/databases/schedules') });
    const existing = schedules.data?.find((s) => s.engine === engine && s.database === database);
    const [form, setForm] = useState<{ enabled: boolean; timeOfDay: string; retentionCount: number } | null>(null);
    const value = form ?? { enabled: existing?.enabled ?? true, timeOfDay: existing?.timeOfDay ?? '03:00', retentionCount: existing?.retentionCount ?? 7 };
    const save = useMutation({
        mutationFn: () => api.put(`/api/databases/${engine}/databases/${database}/schedule`, value),
        onSuccess: () => {
            void queryClient.invalidateQueries({ queryKey: ['db-schedules'] });
            onClose();
        },
    });

    return (
        <Modal open title={`Automatic backups: ${database}`} onClose={onClose}>
            <div className="space-y-4">
                <label className="flex items-center gap-2 text-sm">
                    <input type="checkbox" className="size-4 accent-brand-600" checked={value.enabled} onChange={(e) => setForm({ ...value, enabled: e.target.checked })} />
                    Back up every day
                </label>
                <div className="grid grid-cols-2 gap-3">
                    <Field label="At (server time)" type="time" value={value.timeOfDay} onChange={(e) => setForm({ ...value, timeOfDay: e.target.value })} />
                    <Field label="Keep the newest" type="number" min={1} max={365} value={value.retentionCount} onChange={(e) => setForm({ ...value, retentionCount: Number(e.target.value) })} />
                </div>
                {existing?.lastRunAt && (
                    <p className="text-xs text-muted">
                        Last run {formatDateTime(existing.lastRunAt)}: {existing.lastResult}
                    </p>
                )}
                <ErrorNote message={save.error ? errorMessage(save.error) : null} />
                <div className="flex justify-end gap-2">
                    <Button variant="ghost" onClick={onClose}>
                        Cancel
                    </Button>
                    <Button disabled={save.isPending} onClick={() => save.mutate()}>
                        Save
                    </Button>
                </div>
            </div>
        </Modal>
    );
}

function CredentialsModal({ engine, credentials, onClose }: { engine: DatabaseEngineView; credentials: DatabaseCredentials | { user: string; password: string }; onClose: () => void }) {
    const database = 'database' in credentials ? credentials.database : '';
    const scheme = engine.engine === 'postgres' ? 'postgresql' : 'mysql';
    const url = `${scheme}://${credentials.user}:${credentials.password}@127.0.0.1:${engine.port}/${database}`;
    const rows: [string, string][] = [
        ['Host', '127.0.0.1'],
        ['Port', String(engine.port)],
        ...(database ? ([['Database', database]] as [string, string][]) : []),
        ['User', credentials.user],
        ['Password', credentials.password],
        ['URL', url],
    ];

    return (
        <Modal open title="Connection details" onClose={onClose}>
            <div className="space-y-4">
                <dl className="space-y-2 text-sm">
                    {rows.map(([label, value]) => (
                        <div key={label} className="grid grid-cols-[6rem_1fr_auto] items-center gap-2">
                            <dt className="text-muted">{label}</dt>
                            <dd className="truncate font-mono text-xs">{value}</dd>
                            <Button size="sm" variant="ghost" onClick={() => void navigator.clipboard.writeText(value)}>
                                Copy
                            </Button>
                        </div>
                    ))}
                </dl>
                <p className="text-xs text-muted">The panel keeps this password encrypted, so you can look it up again later.</p>
                <div className="flex justify-end">
                    <Button onClick={onClose}>Done</Button>
                </div>
            </div>
        </Modal>
    );
}

function ConnectHelp({ engine }: { engine: DatabaseEngineView }) {
    const local = engine.engine === 'postgres' ? 5433 : 3307;

    return (
        <Card className="p-5">
            <details>
                <summary className="cursor-pointer font-medium">Connect from your computer (HeidiSQL, DBeaver, Navicat…)</summary>
                <div className="mt-3 space-y-2 text-sm text-slate-700 dark:text-slate-300">
                    <p>The database isn't open to the internet. Use an SSH tunnel, which works with any SSH key that can log in to this server:</p>
                    <pre className="overflow-x-auto rounded-lg bg-slate-950 p-3 font-mono text-xs text-slate-200">
                        ssh -N -L {local}:127.0.0.1:{engine.port} you@{location.hostname}
                    </pre>
                    <p>
                        Then connect your client to <code>127.0.0.1:{local}</code> with the database user's name and password. Most clients can also open the tunnel for you
                        ("SSH tunnel" settings).
                    </p>
                </div>
            </details>
        </Card>
    );
}
