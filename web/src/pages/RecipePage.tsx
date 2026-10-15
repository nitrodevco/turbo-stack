import { useMutation, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import {
    api,
    errorMessage,
    type CloudflareOverview,
    type RecipePlanView,
    type RecipeRequest,
    type RecipeResultView,
    type RecipeView,
    type RepositoryView,
    type TlsMode,
} from '../api';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField, Spinner } from '../components/ui';

const categories: { id: RecipeView['category']; title: string; description: string }[] = [
    { id: 'turbo', title: 'Turbo', description: 'The emulator and its web apps (turbo-cloud).' },
    { id: 'nitro', title: 'Nitro', description: 'The client, the imager and the game assets.' },
    { id: 'general', title: 'Anything else', description: 'Generic apps from any repository.' },
];

const suggestedNames: Record<string, string> = {
    'turbo-emulator': 'turbo',
    'turbo-admin': 'admin',
    'turbo-web': 'web',
    'nitro-client': 'client',
    'nitro-imager': 'imager',
    'static-assets': 'assets',
};

export function RecipePage() {
    const [params, setParams] = useSearchParams();
    const recipes = useQuery({ queryKey: ['recipes'], queryFn: () => api.get<RecipeView[]>('/api/recipes') });
    const selected = recipes.data?.find((r) => r.id === params.get('recipe'));

    return (
        <div className="space-y-6">
            <PageHeader
                title={selected ? selected.name : 'New app from a recipe'}
                description={selected ? selected.description : 'A recipe knows how a project is built and run: answer a few questions, check the preview, and it sets up the repository, database, service and site.'}
                actions={
                    selected ? (
                        <Button variant="secondary" onClick={() => setParams({})}>
                            All recipes
                        </Button>
                    ) : (
                        <Link to="/apps" className="text-sm text-brand-700 hover:underline dark:text-brand-100">
                            Set one up by hand instead
                        </Link>
                    )
                }
            />
            <ErrorNote message={recipes.error ? errorMessage(recipes.error) : null} />
            {recipes.isPending && <Spinner />}
            {recipes.data && !selected && <RecipeList recipes={recipes.data} onPick={(id) => setParams({ recipe: id })} />}
            {selected && <RecipeForm key={selected.id} recipe={selected} />}
        </div>
    );
}

function RecipeList({ recipes, onPick }: { recipes: RecipeView[]; onPick: (id: string) => void }) {
    return (
        <div className="space-y-8">
            {categories.map((category) => (
                <section key={category.id} className="space-y-3">
                    <div>
                        <h2 className="font-medium">{category.title}</h2>
                        <p className="text-sm text-muted">{category.description}</p>
                    </div>
                    <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
                        {recipes
                            .filter((r) => r.category === category.id)
                            .map((recipe) => (
                                <button
                                    key={recipe.id}
                                    type="button"
                                    onClick={() => onPick(recipe.id)}
                                    className="rounded-xl border border-slate-200 bg-white p-4 text-left transition hover:border-brand-400 hover:shadow-sm dark:border-slate-800 dark:bg-slate-900 dark:hover:border-brand-500"
                                >
                                    <p className="font-medium">{recipe.name}</p>
                                    <p className="mt-1 text-sm text-muted">{recipe.summary}</p>
                                    <div className="mt-3 flex flex-wrap gap-1.5">
                                        {recipe.hasService && <Badge tone="info">service</Badge>}
                                        {recipe.site?.directory && <Badge>uploaded files</Badge>}
                                        {recipe.databaseEngines && <Badge>database</Badge>}
                                        {recipe.runtimes.map((r) => (
                                            <Badge key={r}>{r}</Badge>
                                        ))}
                                    </div>
                                </button>
                            ))}
                    </div>
                </section>
            ))}
        </div>
    );
}

function RecipeForm({ recipe }: { recipe: RecipeView }) {
    const repositories = useQuery({ queryKey: ['repositories'], queryFn: () => api.get<RepositoryView[]>('/api/repositories'), enabled: recipe.hasApp });
    const cloudflare = useQuery({ queryKey: ['cloudflare'], queryFn: () => api.get<CloudflareOverview>('/api/cloudflare') });
    const [name, setName] = useState(suggestedNames[recipe.id] ?? recipe.id);
    const [systemUser, setSystemUser] = useState('hotel');
    const [repositoryChoice, setRepositoryChoice] = useState<'auto' | number>('auto');
    const [repository, setRepository] = useState(recipe.source?.repository ?? '');
    const [branch, setBranch] = useState(recipe.source?.branch ?? 'main');
    const [isPrivate, setPrivate] = useState(false);
    const [privateSubmodules, setPrivateSubmodules] = useState('');
    const [domains, setDomains] = useState('');
    const [tlsMode, setTlsMode] = useState<TlsMode>('LetsEncrypt');
    const [cloudflareDns, setCloudflareDns] = useState(false);
    const [databaseEngine, setDatabaseEngine] = useState('');
    const [inputs, setInputs] = useState<Record<string, string>>(() => Object.fromEntries(recipe.inputs.map((i) => [i.key, i.default])));
    const [result, setResult] = useState<RecipeResultView | null>(null);

    const userRepositories = (repositories.data ?? []).filter((r) => r.systemUser === systemUser);
    const reusable = userRepositories.find((r) => r.fullName.toLowerCase() === repository.toLowerCase() && r.branch === branch);

    const request: RecipeRequest = useMemo(
        () => ({
            name,
            systemUser,
            repositoryId: repositoryChoice === 'auto' ? null : repositoryChoice,
            repository,
            branch,
            private: isPrivate,
            privateSubmodules: privateSubmodules.split(/[\s,]+/).filter(Boolean),
            domains: domains.split(/[\s,]+/).filter(Boolean),
            tlsMode,
            cloudflareDns,
            databaseEngine: databaseEngine || null,
            inputs,
        }),
        [name, systemUser, repositoryChoice, repository, branch, isPrivate, privateSubmodules, domains, tlsMode, cloudflareDns, databaseEngine, inputs],
    );

    const preview = useMutation({ mutationFn: () => api.post<RecipePlanView>(`/api/recipes/${recipe.id}/preview`, request) });
    const create = useMutation({ mutationFn: () => api.post<RecipeResultView>(`/api/recipes/${recipe.id}`, request), onSuccess: setResult });

    if (result) {
        return <RecipeDone recipe={recipe} result={result} />;
    }

    return (
        <div className="grid gap-6 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    create.mutate();
                }}
            >
                <Card className="space-y-4 p-5">
                    <div className="grid gap-3 sm:grid-cols-2">
                        <Field label="Name" value={name} onChange={(e) => setName(e.target.value.toLowerCase())} pattern="[a-z0-9][a-z0-9-]{0,39}" required hint="The app's (and site's) name." />
                        <Field
                            label="Linux user"
                            value={systemUser}
                            onChange={(e) => setSystemUser(e.target.value.toLowerCase())}
                            pattern="[a-z][a-z0-9-]{1,31}"
                            required
                            hint="Runs the app. Apps of one hotel usually share a user."
                        />
                    </div>
                    {recipe.hasApp && (
                        <div className="space-y-3">
                            {userRepositories.length > 0 && (
                                <SelectField
                                    label="Repository"
                                    value={String(repositoryChoice)}
                                    onChange={(e) => setRepositoryChoice(e.target.value === 'auto' ? 'auto' : Number(e.target.value))}
                                >
                                    <option value="auto">{reusable ? `${reusable.fullName} (${reusable.branch}), already on this server` : 'A new repository (below)'}</option>
                                    {userRepositories
                                        .filter((r) => r !== reusable)
                                        .map((r) => (
                                            <option key={r.id} value={r.id}>
                                                {r.fullName} ({r.branch})
                                            </option>
                                        ))}
                                </SelectField>
                            )}
                            {repositoryChoice === 'auto' && !reusable && (
                                <div className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
                                    <div className="grid gap-3 sm:grid-cols-[2fr_1fr]">
                                        <Field label="GitHub repository" value={repository} onChange={(e) => setRepository(e.target.value)} placeholder="owner/name" required />
                                        <Field label="Branch" value={branch} onChange={(e) => setBranch(e.target.value)} required />
                                    </div>
                                    <Check label="Private repository (the panel makes a deploy key for you to add on GitHub)" checked={isPrivate} onChange={setPrivate} />
                                    {isPrivate && recipe.source?.submodules && (
                                        <Field
                                            label="Private submodules"
                                            value={privateSubmodules}
                                            onChange={(e) => setPrivateSubmodules(e.target.value)}
                                            placeholder="owner/turbo-admin, owner/turbo-web"
                                            hint="Each gets its own deploy key. Leave empty if they're public."
                                        />
                                    )}
                                </div>
                            )}
                        </div>
                    )}
                </Card>

                {recipe.site && (
                    <Card className="space-y-3 p-5">
                        <Field
                            label={recipe.site.required ? 'Domain' : 'Domain (optional)'}
                            value={domains}
                            onChange={(e) => setDomains(e.target.value)}
                            placeholder={`${suggestedNames[recipe.id] ?? name}.example.com`}
                            required={recipe.site.required}
                            hint={recipe.site.required ? 'Point its DNS at this server. Several domains: separate them with spaces.' : 'Leave empty to only run it on this server.'}
                        />
                        <SelectField label="HTTPS" value={tlsMode} onChange={(e) => setTlsMode(e.target.value as TlsMode)}>
                            <option value="LetsEncrypt">Let's Encrypt (free, automatic renewal)</option>
                            <option value="CloudflareOrigin">Cloudflare Origin certificate (proxied through Cloudflare)</option>
                            <option value="SelfSigned">Self-signed (testing, or behind a proxy)</option>
                            <option value="None" disabled={recipe.site.requiresTls}>
                                None (HTTP only){recipe.site.requiresTls ? ', not possible: this app needs HTTPS' : ''}
                            </option>
                        </SelectField>
                        {cloudflare.data?.connected && <Check label="Create the DNS records on Cloudflare" checked={cloudflareDns} onChange={setCloudflareDns} />}
                    </Card>
                )}

                {(recipe.databaseEngines || recipe.inputs.length > 0) && (
                    <Card className="space-y-3 p-5">
                        {recipe.databaseEngines && (
                            <SelectField
                                label="Database"
                                value={databaseEngine}
                                onChange={(e) => setDatabaseEngine(e.target.value)}
                                hint="A database and user are created for the app; the password goes straight into its settings."
                            >
                                <option value="">Whichever is installed</option>
                                {recipe.databaseEngines.map((engine) => (
                                    <option key={engine} value={engine}>
                                        {engine === 'mysql' ? 'MySQL' : engine === 'mariadb' ? 'MariaDB' : 'PostgreSQL'}
                                    </option>
                                ))}
                            </SelectField>
                        )}
                        {recipe.inputs.map((input) =>
                            input.kind === 'bool' ? (
                                <Check key={input.key} label={input.label} checked={inputs[input.key] === 'true'} onChange={(v) => setInputs({ ...inputs, [input.key]: v ? 'true' : '' })} />
                            ) : (
                                <Field
                                    key={input.key}
                                    label={input.required ? input.label : `${input.label} (optional)`}
                                    type={input.kind === 'number' ? 'number' : 'text'}
                                    value={inputs[input.key] ?? ''}
                                    onChange={(e) => setInputs({ ...inputs, [input.key]: e.target.value })}
                                    placeholder={input.placeholder}
                                    required={input.required}
                                    hint={input.help || undefined}
                                />
                            ),
                        )}
                    </Card>
                )}

                <ErrorNote message={create.error ? errorMessage(create.error) : null} />
                <div className="flex gap-2">
                    <Button type="button" variant="secondary" onClick={() => preview.mutate()} disabled={preview.isPending}>
                        {preview.isPending ? 'Checking…' : 'Preview'}
                    </Button>
                    <Button type="submit" disabled={create.isPending}>
                        {create.isPending ? 'Creating…' : 'Create'}
                    </Button>
                </div>
            </form>

            <div className="space-y-3">
                <ErrorNote message={preview.error ? errorMessage(preview.error) : null} />
                {preview.data ? (
                    <PlanView plan={preview.data} />
                ) : (
                    <Notice>Preview shows everything this will create (secrets are generated when you click Create) without changing anything.</Notice>
                )}
            </div>
        </div>
    );
}

function PlanView({ plan }: { plan: RecipePlanView }) {
    return (
        <Card className="space-y-4 p-5 text-sm">
            <h2 className="font-medium">What will be created</h2>
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5">
                <dt className="text-muted">Linux user</dt>
                <dd>{plan.systemUser}</dd>
                {plan.repository && (
                    <>
                        <dt className="text-muted">Repository</dt>
                        <dd>
                            {plan.repository.fullName} ({plan.repository.branch}){plan.repository.reused ? ', already on this server' : ', new'}
                            {plan.repository.submodules && ', with submodules'}
                            {plan.repository.private && ', private (deploy key)'}
                        </dd>
                    </>
                )}
                {plan.database && (
                    <>
                        <dt className="text-muted">Database</dt>
                        <dd>
                            {plan.database.engine} database <code>{plan.database.name}</code> with user <code>{plan.database.user}</code>
                        </dd>
                    </>
                )}
                {Object.keys(plan.ports).length > 0 && (
                    <>
                        <dt className="text-muted">Ports</dt>
                        <dd>
                            {Object.entries(plan.ports)
                                .map(([name, port]) => `${name} ${port}`)
                                .join(' · ')}{' '}
                            <span className="text-muted">(127.0.0.1 only)</span>
                        </dd>
                    </>
                )}
                {plan.siteDirectory && (
                    <>
                        <dt className="text-muted">Folder</dt>
                        <dd>
                            <code>{plan.siteDirectory}</code>
                        </dd>
                    </>
                )}
                {plan.site && (
                    <>
                        <dt className="text-muted">Site</dt>
                        <dd>
                            {plan.site.domains.join(', ')}
                            <ul className="mt-1 space-y-0.5 text-xs text-muted">
                                {plan.site.routes.map((route) => (
                                    <li key={route.path}>
                                        <code>{route.path}</code> → {route.kind.toLowerCase()} <code className="break-all">{route.target}</code>
                                    </li>
                                ))}
                            </ul>
                        </dd>
                    </>
                )}
            </dl>
            {plan.app && (
                <div className="space-y-3">
                    {plan.app.build && (
                        <div>
                            <p className="mb-1 text-muted">
                                Build{plan.app.runtimes.length > 0 && ` (with ${plan.app.runtimes.join(' + ')})`}
                                {plan.app.workingDirectory && ` in ${plan.app.workingDirectory}/`}
                            </p>
                            <pre className="max-h-48 overflow-auto rounded-lg bg-slate-950 p-3 text-xs text-slate-100">{plan.app.build}</pre>
                        </div>
                    )}
                    {plan.app.staticOutput && (
                        <p>
                            <span className="text-muted">Serves</span> <code>{plan.app.staticOutput}/</code>
                        </p>
                    )}
                    {plan.app.service && (
                        <p>
                            <span className="text-muted">Runs</span> <code>{plan.app.service.command}</code>
                            {plan.app.serviceDirectory && ` in ${plan.app.serviceDirectory}/`}
                            {plan.app.service.port && `, checked on port ${plan.app.service.port}${plan.app.service.healthPath ?? ''}`}
                            {plan.app.service.memoryMaxMb > 0 && `, memory limit ${plan.app.service.memoryMaxMb} MB`}
                        </p>
                    )}
                    {plan.app.sharedPaths.length > 0 && (
                        <p>
                            <span className="text-muted">Kept between deploys</span> {plan.app.sharedPaths.map((p) => p.path).join(', ')}
                        </p>
                    )}
                    {Object.keys(plan.app.environment).length > 0 && (
                        <details>
                            <summary className="cursor-pointer text-muted">Environment ({Object.keys(plan.app.environment).length} variables)</summary>
                            <div className="mt-2 max-h-72 overflow-auto rounded-lg border border-line">
                                <table className="w-full text-xs">
                                    <tbody>
                                        {Object.entries(plan.app.environment).map(([key, value]) => (
                                            <tr key={key} className="border-b border-slate-100 last:border-0 dark:border-slate-800">
                                                <td className="px-2 py-1 align-top font-mono">{key}</td>
                                                <td className="break-all px-2 py-1 font-mono text-muted">{value}</td>
                                            </tr>
                                        ))}
                                    </tbody>
                                </table>
                            </div>
                        </details>
                    )}
                </div>
            )}
            {plan.notes.length > 0 && (
                <ul className="list-disc space-y-1 pl-5 text-muted">
                    {plan.notes.map((note) => (
                        <li key={note}>{note}</li>
                    ))}
                </ul>
            )}
        </Card>
    );
}

function RecipeDone({ recipe, result }: { recipe: RecipeView; result: RecipeResultView }) {
    const navigate = useNavigate();
    // Keys the panel already added through the connected GitHub account come back without a public key.
    const pendingKeys = result.repositoryCreated ? (result.deployKeys ?? []).filter((k) => k.publicKey) : [];
    const keysNeeded = pendingKeys.length > 0;
    const deploy = useMutation({
        mutationFn: () => api.post(`/api/apps/${result.appId}/deploy`),
        onSuccess: () => void navigate(`/apps/${result.appId}`),
    });

    return (
        <Card className="space-y-4 p-5">
            <Notice tone="success">{recipe.name} is set up.</Notice>
            {result.site?.error && <Notice tone="warning">The site was saved but not applied: {result.site.error}</Notice>}
            {result.site?.dnsError && <Notice tone="warning">DNS: {result.site.dnsError}</Notice>}
            {keysNeeded && (
                <div className="space-y-2">
                    <p className="text-sm">
                        Add {pendingKeys.length > 1 ? 'these deploy keys' : 'this deploy key'} on GitHub (repository → Settings → Deploy keys, read-only) before the first
                        deploy:
                    </p>
                    {pendingKeys.map((key) => (
                        <div key={key.repository}>
                            <p className="text-xs font-medium">
                                <a className="text-brand-700 hover:underline dark:text-brand-100" href={`https://github.com/${key.repository}/settings/keys`} target="_blank" rel="noreferrer">
                                    {key.repository}
                                </a>
                            </p>
                            <code className="block break-all rounded-lg bg-slate-100 p-2 text-xs dark:bg-slate-800">{key.publicKey}</code>
                        </div>
                    ))}
                </div>
            )}
            {result.notes.length > 0 && (
                <ul className="list-disc space-y-1 pl-5 text-sm text-muted">
                    {result.notes.map((note) => (
                        <li key={note}>{note}</li>
                    ))}
                </ul>
            )}
            <ErrorNote message={deploy.error ? errorMessage(deploy.error) : null} />
            <div className="flex flex-wrap gap-2">
                {result.appId && (
                    <>
                        <Button onClick={() => deploy.mutate()} disabled={deploy.isPending}>
                            {keysNeeded ? 'Deploy (after adding the keys)' : 'Deploy now'}
                        </Button>
                        <Button variant="secondary" onClick={() => void navigate(`/apps/${result.appId}`)}>
                            Open the app
                        </Button>
                    </>
                )}
                {result.siteId && (
                    <Button variant="secondary" onClick={() => void navigate(`/sites/${result.siteId}`)}>
                        Open the site
                    </Button>
                )}
            </div>
        </Card>
    );
}

function Check({ label, checked, onChange }: { label: string; checked: boolean; onChange: (value: boolean) => void }) {
    return (
        <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" className="size-4 accent-brand-600" checked={checked} onChange={(e) => onChange(e.target.checked)} />
            {label}
        </label>
    );
}
