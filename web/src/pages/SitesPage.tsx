import { useQuery } from '@tanstack/react-query';
import { Link } from 'react-router';
import { api, errorMessage, type SiteSummary } from '../api';
import { usePermissions } from '../auth';
import { Badge, ErrorNote, Notice, PageHeader, Spinner, TableWrap } from '../components/ui';
import { formatDateTime } from '../format';

const kindLabels = { Static: 'Files', Spa: 'Single-page app', Proxy: 'Proxy', WebSocket: 'WebSocket', Redirect: 'Redirect' } as const;

export function SitesPage() {
    const can = usePermissions();
    const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<SiteSummary[]>('/api/sites') });

    return (
        <div className="space-y-6">
            <PageHeader
                title="Sites"
                description="Domains served by nginx on this server. Each site is generated from its settings; you can still add custom directives."
                actions={
                    can('sites.manage') && (
                        <Link to="/sites/new" className="rounded-lg bg-brand-600 px-4 py-2 text-sm font-medium text-white hover:bg-brand-700">
                            Add site
                        </Link>
                    )
                }
            />
            <ErrorNote message={sites.error ? errorMessage(sites.error) : null} />
            {sites.isPending ? (
                <Spinner />
            ) : sites.data?.length === 0 ? (
                <Notice>No sites yet. Add one to serve a domain from this server.</Notice>
            ) : (
                <TableWrap>
                    <thead>
                        <tr>
                            <th>Site</th>
                            <th>Serves</th>
                            <th>HTTPS</th>
                            <th>Status</th>
                        </tr>
                    </thead>
                    <tbody>
                        {(sites.data ?? []).map((site) => (
                            <tr key={site.id}>
                                <td>
                                    <Link to={`/sites/${site.id}`} className="font-medium text-brand-700 hover:underline dark:text-brand-100">
                                        {site.domains[0]}
                                    </Link>
                                    <p className="text-xs text-muted">
                                        {site.name}
                                        {site.domains.length > 1 && ` · +${site.domains.length - 1} more`}
                                        {site.system && ' · panel'}
                                    </p>
                                </td>
                                <td className="text-muted">
                                    {site.custom
                                        ? 'Custom config'
                                        : site.routes.map((r) => (
                                              <span key={r.path} className="mr-3 whitespace-nowrap">
                                                  <code className="text-xs">{r.path}</code> {kindLabels[r.kind]}
                                              </span>
                                          ))}
                                </td>
                                <td>
                                    {site.tlsMode === 'None' ? (
                                        <Badge>HTTP only</Badge>
                                    ) : site.https ? (
                                        <Badge tone="good" >🔒 until {formatDateTime(site.certificateExpires!).split(',')[0]}</Badge>
                                    ) : (
                                        <Badge tone="warning">⚠ Waiting for certificate</Badge>
                                    )}
                                </td>
                                <td>
                                    {site.lastError ? (
                                        <Badge tone="danger">✕ Not applied</Badge>
                                    ) : site.enabled ? (
                                        <Badge tone="good">● Live</Badge>
                                    ) : (
                                        <Badge>Disabled</Badge>
                                    )}
                                </td>
                            </tr>
                        ))}
                    </tbody>
                </TableWrap>
            )}
        </div>
    );
}
