import { useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link } from 'react-router';
import { api, errorMessage } from '../api';
import { Badge, Card, ErrorNote, Input, PageHeader, Spinner } from '../components/ui';

type Schema = { type?: string | string[]; $ref?: string; items?: Schema; properties?: Record<string, Schema>; required?: string[]; enum?: unknown[]; format?: string };
type Parameter = { name: string; in: string; required?: boolean; schema?: Schema };
type Operation = { description?: string; parameters?: Parameter[]; requestBody?: { content?: Record<string, { schema?: Schema }> } };
type OpenApi = {
    info: { title: string; version: string; description?: string };
    paths: Record<string, Record<string, Operation>>;
    components?: { schemas?: Record<string, Schema> };
};

const methodTones: Record<string, 'good' | 'info' | 'warning' | 'danger' | 'neutral'> = { get: 'info', post: 'good', put: 'warning', patch: 'warning', delete: 'danger' };

/** The panel's own API, from its OpenAPI document: what each endpoint takes and the permission it needs. */
export function ApiPage() {
    const doc = useQuery({ queryKey: ['openapi'], queryFn: () => api.get<OpenApi>('/api/openapi.json'), staleTime: Infinity });
    const [filter, setFilter] = useState('');

    const groups = useMemo(() => {
        const result = new Map<string, { method: string; path: string; op: Operation }[]>();

        for (const [path, methods] of Object.entries(doc.data?.paths ?? {})) {
            for (const [method, op] of Object.entries(methods)) {
                const text = `${method} ${path} ${op.description ?? ''}`.toLowerCase();
                if (filter && !text.includes(filter.toLowerCase())) continue;
                const area = path.split('/')[2] ?? '';
                result.set(area, [...(result.get(area) ?? []), { method, path, op }]);
            }
        }

        return [...result.entries()].sort(([a], [b]) => a.localeCompare(b));
    }, [doc.data, filter]);

    return (
        <div className="space-y-6">
            <PageHeader
                title="API"
                description="Everything the panel does is an HTTP API. Scripts and CI use it with an API token."
                actions={
                    <a className="text-sm underline" href="/api/openapi.json" download="openapi.json">
                        OpenAPI document
                    </a>
                }
            />
            <Card className="space-y-2 p-5 text-sm">
                <p>
                    Create a token on your <Link to="/account" className="underline">Account</Link> page, give it only the permissions it needs, and send it with every
                    request:
                </p>
                <pre className="overflow-x-auto rounded bg-slate-100 p-3 font-mono text-xs dark:bg-slate-900">
                    {`curl -H "Authorization: Bearer ts_…" ${window.location.origin}/api/apps`}
                </pre>
                <p className="text-muted">
                    Requests that change something from a browser also need the header <code>X-TS-Request: 1</code>; with a token it isn't needed.
                </p>
            </Card>
            <Input placeholder="Filter: deploy, /api/sites, backups.manage…" value={filter} onChange={(e) => setFilter(e.target.value)} aria-label="Filter" />
            <ErrorNote message={doc.error ? errorMessage(doc.error) : null} />
            {doc.isPending ? (
                <Spinner />
            ) : (
                groups.map(([area, operations]) => (
                    <Card key={area} className="space-y-1 p-5">
                        <h2 className="mb-2 font-medium">{area}</h2>
                        {operations.map(({ method, path, op }) => (
                            <OperationRow key={`${method} ${path}`} method={method} path={path} op={op} schemas={doc.data?.components?.schemas ?? {}} />
                        ))}
                    </Card>
                ))
            )}
        </div>
    );
}

function OperationRow({ method, path, op, schemas }: { method: string; path: string; op: Operation; schemas: Record<string, Schema> }) {
    const [open, setOpen] = useState(false);
    const body = Object.values(op.requestBody?.content ?? {})[0]?.schema;

    return (
        <div className="border-b border-slate-100 py-1.5 last:border-0 dark:border-slate-800">
            <button type="button" className="flex w-full flex-wrap items-center gap-2 text-left" onClick={() => setOpen(!open)}>
                <Badge tone={methodTones[method] ?? 'neutral'}>{method.toUpperCase()}</Badge>
                <code className="text-sm">{path}</code>
                {op.description && <span className="text-xs text-muted">{op.description.replace(/`/g, '')}</span>}
            </button>
            {open && (
                <div className="mt-2 space-y-2 pl-2 text-xs">
                    {(op.parameters ?? []).length > 0 && (
                        <p>
                            Parameters:{' '}
                            {op.parameters!.map((p) => (
                                <code key={p.name} className="mr-2">
                                    {p.name}
                                    {p.required ? '' : '?'} ({p.in}, {typeName(p.schema, schemas)})
                                </code>
                            ))}
                        </p>
                    )}
                    {body && (
                        <div>
                            <p>Body:</p>
                            <pre className="overflow-x-auto rounded bg-slate-100 p-2 dark:bg-slate-900">{describe(body, schemas, 0)}</pre>
                        </div>
                    )}
                    {!body && (op.parameters ?? []).length === 0 && <p className="text-muted">No parameters.</p>}
                </div>
            )}
        </div>
    );
}

function resolve(schema: Schema | undefined, schemas: Record<string, Schema>): Schema | undefined {
    return schema?.$ref ? schemas[schema.$ref.split('/').pop()!] : schema;
}

function typeName(schema: Schema | undefined, schemas: Record<string, Schema>): string {
    const s = resolve(schema, schemas);
    if (!s) return 'any';
    if (s.enum) return s.enum.map(String).join(' | ');
    const type = Array.isArray(s.type) ? s.type.filter((t) => t !== 'null').join(' | ') : s.type;
    if (type === 'array') return `${typeName(s.items, schemas)}[]`;
    return type ?? (schema?.$ref?.split('/').pop() ?? 'object');
}

/** A request body as an indented field list (two levels deep is plenty here). */
function describe(schema: Schema, schemas: Record<string, Schema>, depth: number): string {
    const s = resolve(schema, schemas);
    if (!s?.properties || depth > 1) return typeName(schema, schemas);
    const pad = '  '.repeat(depth);

    return Object.entries(s.properties)
        .map(([name, field]) => {
            const nested = resolve(field, schemas)?.properties && depth < 1 ? `\n${describe(field, schemas, depth + 1)}` : '';
            return `${pad}${name}${s.required?.includes(name) ? '' : '?'}: ${typeName(field, schemas)}${nested}`;
        })
        .join('\n');
}
