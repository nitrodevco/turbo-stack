import { useMutation, useQuery } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { useNavigate } from 'react-router';
import { api, errorMessage, type CloudflareOverview, type DatabaseEngineView, type PresetView, type StackRequest, type StackSourceView, type TlsMode } from '../api';
import { Badge, Button, Card, ErrorNote, Field, Notice, PageHeader, SelectField, Spinner } from '../components/ui';

const steps = ['Basics', 'Domains', 'Sources', 'Database', 'Hotel', 'Review'] as const;
type Step = (typeof steps)[number];

const engineNames: Record<string, string> = { mysql: 'MySQL', mariadb: 'MariaDB', postgres: 'PostgreSQL' };

export function HotelWizardPage() {
    const presets = useQuery({ queryKey: ['presets'], queryFn: () => api.get<PresetView[]>('/api/presets') });
    const preset = presets.data?.[0];

    return (
        <div className="space-y-6">
            <PageHeader
                title="Set up a hotel"
                description="Answer a few questions; the panel then installs the runtimes and database, sets up every part of the hotel on its own subdomain, wires them together and deploys them."
            />
            <ErrorNote message={presets.error ? errorMessage(presets.error) : null} />
            {preset ? <Wizard preset={preset} /> : <Spinner />}
        </div>
    );
}

function Wizard({ preset }: { preset: PresetView }) {
    const navigate = useNavigate();
    const cloudflare = useQuery({ queryKey: ['cloudflare'], queryFn: () => api.get<CloudflareOverview>('/api/cloudflare') });
    const engines = useQuery({ queryKey: ['database-engines'], queryFn: () => api.get<DatabaseEngineView[]>('/api/databases/engines') });
    const [step, setStep] = useState<Step>('Basics');
    const [domain, setDomain] = useState('');
    const [name, setName] = useState('hotel');
    const [tlsMode, setTlsMode] = useState<TlsMode>('LetsEncrypt');
    const [cloudflareDns, setCloudflareDns] = useState(false);
    const [hostEdits, setHostEdits] = useState<Record<string, string>>({});
    const [assetsHere, setAssetsHere] = useState(true);
    const [externalAssets, setExternalAssets] = useState({ url: '', badgeUrl: '' });
    const [sources, setSources] = useState<StackSourceView[]>(() =>
        [...new Map(preset.roles.filter((r) => r.source).map((r) => [r.source!.repository, r.source!])).values()].map((s) => ({
            default: s.repository,
            repository: s.repository,
            branch: s.branch,
            private: false,
            privateSubmodules: [],
        })),
    );
    const [databaseEngine, setDatabaseEngine] = useState('');
    const [settings, setSettings] = useState<Record<string, string>>(() => Object.fromEntries(preset.settings.map((s) => [s.key, s.default])));

    const hosts = useMemo(() => {
        const result: Record<string, string> = {};
        for (const role of preset.roles) {
            result[role.role] = hostEdits[role.role] ?? (domain ? (role.host === '@' ? domain : `${role.host}.${domain}`) : '');
        }
        if (!assetsHere) result.assets = '';
        return result;
    }, [preset, hostEdits, domain, assetsHere]);

    const request: StackRequest = {
        name,
        preset: preset.id,
        systemUser: name,
        domain,
        tlsMode,
        cloudflareDns,
        hosts,
        settings: {
            ...settings,
            ...(assetsHere ? {} : { 'assets.url': externalAssets.url, 'assets.badgeUrl': externalAssets.badgeUrl || `${externalAssets.url.replace(/\/$/, '')}/images/badges/%badgename%.gif` }),
        },
        sources,
        databaseEngine: databaseEngine || null,
    };

    const create = useMutation({
        mutationFn: async () => {
            const { id } = await api.post<{ id: number }>('/api/stacks', request);
            await api.post(`/api/stacks/${id}/install`);
            return id;
        },
        onSuccess: (id) => void navigate(`/hotel/${id}`),
    });

    const index = steps.indexOf(step);
    const needsTls = preset.roles.some((r) => r.requiresTls);
    const recommended = engines.data?.find((e) => e.recommended && preset.databaseEngines?.includes(e.engine));

    return (
        <div className="grid gap-6 lg:grid-cols-[12rem_minmax(0,1fr)]">
            <ol className="space-y-1 text-sm">
                {steps.map((s, i) => (
                    <li key={s}>
                        <button
                            type="button"
                            disabled={i > index}
                            onClick={() => setStep(s)}
                            className={`w-full rounded-lg px-3 py-2 text-left ${s === step ? 'bg-brand-50 font-medium text-brand-700 dark:bg-brand-950 dark:text-brand-100' : 'text-slate-600 disabled:opacity-50 dark:text-slate-400'}`}
                        >
                            {i + 1}. {s}
                        </button>
                    </li>
                ))}
            </ol>

            <form
                className="space-y-4"
                onSubmit={(e) => {
                    e.preventDefault();
                    if (step === 'Review') create.mutate();
                    else setStep(steps[index + 1]);
                }}
            >
                {step === 'Basics' && (
                    <Card className="space-y-4 p-5">
                        <Field
                            label="The hotel's domain"
                            value={domain}
                            onChange={(e) => setDomain(e.target.value.trim().toLowerCase())}
                            placeholder="myhotel.com"
                            required
                            hint="The website goes here; the other parts get subdomains (play., ws., admin., ...). You can change each one next."
                        />
                        <Field
                            label="Short name"
                            value={name}
                            onChange={(e) => setName(e.target.value.toLowerCase())}
                            pattern="[a-z][a-z0-9-]{0,19}"
                            required
                            hint={`Names the apps (${name}-emulator, ${name}-client, ...), the database and the Linux user they run as.`}
                        />
                        <SelectField label="HTTPS" value={tlsMode} onChange={(e) => setTlsMode(e.target.value as TlsMode)}>
                            <option value="LetsEncrypt">Let's Encrypt (free; the domains must point at this server)</option>
                            <option value="CloudflareOrigin" disabled={!cloudflare.data?.connected}>
                                Cloudflare Origin certificate{cloudflare.data?.connected ? ' (proxied through Cloudflare)' : ' (connect Cloudflare first)'}
                            </option>
                            <option value="SelfSigned">Self-signed (testing)</option>
                            <option value="None" disabled={needsTls}>
                                None{needsTls ? ' (not possible: the admin panel needs HTTPS)' : ''}
                            </option>
                        </SelectField>
                        {cloudflare.data?.connected && <Check label="Create the DNS records on Cloudflare" checked={cloudflareDns} onChange={setCloudflareDns} />}
                    </Card>
                )}

                {step === 'Domains' && (
                    <Card className="space-y-3 p-5">
                        {preset.roles.map((role) => (
                            <div key={role.role} className="grid items-end gap-2 sm:grid-cols-[10rem_1fr]">
                                <span className="pb-2 text-sm font-medium">{role.label}</span>
                                {role.optional && !assetsHere && role.role === 'assets' ? (
                                    <span className="pb-2 text-sm text-slate-500">An existing host (below)</span>
                                ) : (
                                    <Field label="" aria-label={role.label} value={hosts[role.role]} onChange={(e) => setHostEdits({ ...hostEdits, [role.role]: e.target.value.trim().toLowerCase() })} required />
                                )}
                            </div>
                        ))}
                        <Check
                            label="Host the game assets (bundles, images, gamedata) on this server; you upload them over SFTP"
                            checked={assetsHere}
                            onChange={setAssetsHere}
                        />
                        {!assetsHere && (
                            <div className="grid gap-3 sm:grid-cols-2">
                                <Field label="Assets address" value={externalAssets.url} onChange={(e) => setExternalAssets({ ...externalAssets, url: e.target.value })} placeholder="https://assets.example.com" required />
                                <Field
                                    label="Badge images (optional)"
                                    value={externalAssets.badgeUrl}
                                    onChange={(e) => setExternalAssets({ ...externalAssets, badgeUrl: e.target.value })}
                                    placeholder=".../images/badges/%badgename%.gif"
                                />
                            </div>
                        )}
                    </Card>
                )}

                {step === 'Sources' && (
                    <Card className="space-y-4 p-5">
                        {sources.map((source, i) => {
                            const set = (patch: Partial<StackSourceView>) => setSources(sources.map((s, j) => (j === i ? { ...s, ...patch } : s)));
                            const withSubmodules = preset.roles.some((r) => r.source?.repository === source.default && r.source.submodules);
                            return (
                                <div key={source.default} className="space-y-3 rounded-lg border border-slate-200 p-3 dark:border-slate-800">
                                    <div className="grid gap-3 sm:grid-cols-[2fr_1fr]">
                                        <Field label={`Repository (${preset.roles.filter((r) => r.source?.repository === source.default).map((r) => r.label).join(', ')})`} value={source.repository} onChange={(e) => set({ repository: e.target.value })} required />
                                        <Field label="Branch" value={source.branch} onChange={(e) => set({ branch: e.target.value })} required />
                                    </div>
                                    <Check label="Private (the panel makes a deploy key for you to add on GitHub)" checked={source.private} onChange={(v) => set({ private: v })} />
                                    {source.private && withSubmodules && (
                                        <Field
                                            label="Private submodules"
                                            value={source.privateSubmodules.join(', ')}
                                            onChange={(e) => set({ privateSubmodules: e.target.value.split(/[\s,]+/).filter(Boolean) })}
                                            placeholder="owner/turbo-admin, owner/turbo-web"
                                        />
                                    )}
                                </div>
                            );
                        })}
                    </Card>
                )}

                {step === 'Database' && (
                    <Card className="space-y-3 p-5">
                        <SelectField label="Database" value={databaseEngine} onChange={(e) => setDatabaseEngine(e.target.value)}>
                            <option value="">Recommended for this server{recommended ? ` (${engineNames[recommended.engine]})` : ''}</option>
                            {preset.databaseEngines?.map((engine) => (
                                <option key={engine} value={engine}>
                                    {engineNames[engine]}
                                    {engines.data?.find((e) => e.engine === engine)?.installed ? ' (installed)' : ''}
                                </option>
                            ))}
                        </SelectField>
                        {recommended?.note && <p className="text-sm text-slate-500">{recommended.note}</p>}
                        <p className="text-sm text-slate-500">A database and user are created for the emulator; the password goes straight into its settings.</p>
                    </Card>
                )}

                {step === 'Hotel' && (
                    <Card className="space-y-3 p-5">
                        {preset.settings.map((setting) => (
                            <Field
                                key={setting.key}
                                label={setting.label}
                                value={settings[setting.key] ?? ''}
                                onChange={(e) => setSettings({ ...settings, [setting.key]: e.target.value })}
                                required={setting.required}
                                hint={setting.help || undefined}
                            />
                        ))}
                    </Card>
                )}

                {step === 'Review' && (
                    <Card className="space-y-4 p-5 text-sm">
                        <table className="w-full">
                            <tbody>
                                {preset.roles.map((role) => (
                                    <tr key={role.role} className="border-b border-slate-100 last:border-0 dark:border-slate-800">
                                        <td className="py-1.5 pr-4 text-slate-500">{role.label}</td>
                                        <td className="py-1.5">{hosts[role.role] || <span className="text-slate-500">{externalAssets.url} (existing)</span>}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                        <p>
                            <Badge>{tlsMode === 'LetsEncrypt' ? "Let's Encrypt" : tlsMode}</Badge> <Badge>{databaseEngine ? engineNames[databaseEngine] : `${recommended ? engineNames[recommended.engine] : 'database'} (recommended)`}</Badge>{' '}
                            {sources.map((s) => (
                                <Badge key={s.default}>
                                    {s.repository} ({s.branch}){s.private ? ' · private' : ''}
                                </Badge>
                            ))}
                        </p>
                        <Notice>
                            Installing takes a few minutes, mostly building the emulator and the client. You can follow it on the next page.
                            {sources.some((s) => s.private) && ' The install pauses for you to add the deploy keys on GitHub, then you continue it.'}
                        </Notice>
                    </Card>
                )}

                <ErrorNote message={create.error ? errorMessage(create.error) : null} />
                <div className="flex gap-2">
                    {index > 0 && (
                        <Button type="button" variant="secondary" onClick={() => setStep(steps[index - 1])}>
                            Back
                        </Button>
                    )}
                    <Button type="submit" disabled={create.isPending}>
                        {step === 'Review' ? (create.isPending ? 'Starting…' : 'Create and install') : 'Next'}
                    </Button>
                </div>
            </form>
        </div>
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
