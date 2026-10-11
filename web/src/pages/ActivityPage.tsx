import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api, errorMessage, type AuditEntry } from '../api';
import { Button, ErrorNote, PageHeader, Select, Spinner, TableWrap } from '../components/ui';
import { formatDateTime } from '../format';

const areas = [
    { value: '', label: 'Everything' },
    { value: 'auth', label: 'Sign-ins' },
    { value: 'setup', label: 'Setup' },
    { value: 'firewall', label: 'Firewall' },
    { value: 'fail2ban', label: 'Brute-force protection' },
    { value: 'ssh', label: 'SSH' },
    { value: 'service', label: 'Services' },
    { value: 'packages', label: 'Updates' },
    { value: 'server', label: 'Reboots' },
];

export function ActivityPage() {
    const [area, setArea] = useState('');
    const [page, setPage] = useState(1);
    const audit = useQuery({
        queryKey: ['audit', area, page],
        queryFn: () => api.get<{ entries: AuditEntry[]; hasMore: boolean }>(`/api/audit?page=${page}${area ? `&action=${area}` : ''}`),
    });

    return (
        <div className="space-y-6">
            <PageHeader title="Activity" description="Everything that was changed through the panel, and every sign-in." />
            <Select
                value={area}
                onChange={(e) => {
                    setArea(e.target.value);
                    setPage(1);
                }}
                className="w-auto"
            >
                {areas.map((a) => (
                    <option key={a.value} value={a.value}>
                        {a.label}
                    </option>
                ))}
            </Select>
            <ErrorNote message={audit.error ? errorMessage(audit.error) : null} />
            {audit.isPending ? (
                <Spinner />
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>When</th>
                            <th>Who</th>
                            <th>What</th>
                            <th>Details</th>
                            <th>From</th>
                        </tr>
                    </thead>
                    <tbody>
                        {(audit.data?.entries ?? []).map((entry) => (
                            <tr key={entry.id}>
                                <td className="whitespace-nowrap text-slate-500">{formatDateTime(entry.at)}</td>
                                <td>{entry.username ?? 'system'}</td>
                                <td className="font-mono text-xs">{entry.action}</td>
                                <td>
                                    {entry.target && <span className="font-medium">{entry.target} </span>}
                                    <span className="text-slate-600 dark:text-slate-400">{entry.details}</span>
                                </td>
                                <td className="font-mono text-xs text-slate-500">{entry.ipAddress}</td>
                            </tr>
                        ))}
                        {audit.data?.entries.length === 0 && (
                            <tr>
                                <td colSpan={5} className="py-8 text-center text-slate-500">
                                    Nothing yet.
                                </td>
                            </tr>
                        )}
                    </tbody>
                </TableWrap>
            )}
            <div className="flex justify-between">
                <Button variant="ghost" disabled={page === 1} onClick={() => setPage(page - 1)}>
                    ← Newer
                </Button>
                <Button variant="ghost" disabled={!audit.data?.hasMore} onClick={() => setPage(page + 1)}>
                    Older →
                </Button>
            </div>
        </div>
    );
}
