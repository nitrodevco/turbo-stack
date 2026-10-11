import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { api, errorMessage, type ServerSettings } from '../api';
import { usePermissions } from '../auth';
import { Button, Card, ErrorNote, Field, Notice, PageHeader } from '../components/ui';

export function SettingsPage() {
    const can = usePermissions();
    const queryClient = useQueryClient();
    const settings = useQuery({ queryKey: ['settings', 'server'], queryFn: () => api.get<ServerSettings>('/api/settings/server') });
    const [form, setForm] = useState<ServerSettings>({ publicIPv4: '', publicIPv6: '' });

    useEffect(() => {
        if (settings.data) setForm({ publicIPv4: settings.data.publicIPv4 ?? '', publicIPv6: settings.data.publicIPv6 ?? '' });
    }, [settings.data]);

    const save = useMutation({
        mutationFn: () => api.put<ServerSettings>('/api/settings/server', form),
        onSuccess: (data) => queryClient.setQueryData(['settings', 'server'], data),
    });
    const detect = useMutation({
        mutationFn: () => api.post<ServerSettings>('/api/settings/server/detect-ip'),
        onSuccess: (data) => setForm({ publicIPv4: data.publicIPv4 ?? form.publicIPv4, publicIPv6: data.publicIPv6 ?? form.publicIPv6 }),
    });

    return (
        <div className="space-y-6">
            <PageHeader title="Settings" />
            <Card className="space-y-4 p-5">
                <div>
                    <h2 className="font-medium">Public address</h2>
                    <p className="text-sm text-slate-500">The addresses the internet reaches this server on. Used to check DNS and, with Cloudflare, to create DNS records.</p>
                </div>
                <form
                    className="space-y-4"
                    onSubmit={(e) => {
                        e.preventDefault();
                        save.mutate();
                    }}
                >
                    <fieldset disabled={!can('sites.manage')} className="grid gap-4 sm:grid-cols-2">
                        <Field label="IPv4" value={form.publicIPv4 ?? ''} onChange={(e) => setForm({ ...form, publicIPv4: e.target.value })} placeholder="203.0.113.10" />
                        <Field label="IPv6 (optional)" value={form.publicIPv6 ?? ''} onChange={(e) => setForm({ ...form, publicIPv6: e.target.value })} placeholder="2001:db8::10" />
                    </fieldset>
                    <ErrorNote message={save.error ? errorMessage(save.error) : detect.error ? errorMessage(detect.error) : null} />
                    {save.isSuccess && <Notice tone="success">✓ Saved.</Notice>}
                    {can('sites.manage') && (
                        <div className="flex gap-2">
                            <Button type="submit" disabled={save.isPending}>
                                Save
                            </Button>
                            <Button type="button" variant="secondary" disabled={detect.isPending} onClick={() => detect.mutate()}>
                                {detect.isPending ? 'Detecting…' : 'Detect automatically'}
                            </Button>
                        </div>
                    )}
                </form>
            </Card>
        </div>
    );
}
