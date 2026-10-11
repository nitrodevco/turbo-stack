import { useQueryClient } from '@tanstack/react-query';
import { NavLink, Outlet, useNavigate } from 'react-router';
import { api, type Me } from '../api';
import { PendingFirewallBanner } from './PendingFirewallBanner';
import { Logo } from './ui';

/** Sections arrive milestone by milestone; unbuilt ones are listed so the shape of the panel is visible. */
const navigation: { label: string; to?: string; permission?: string }[] = [
    { label: 'Dashboard', to: '/', permission: 'system.view' },
    { label: 'Hotel' },
    { label: 'Sites' },
    { label: 'Apps & deploys' },
    { label: 'Databases' },
    { label: 'Services', to: '/services', permission: 'services.view' },
    { label: 'Logs', to: '/logs', permission: 'logs.view' },
    { label: 'Firewall', to: '/firewall', permission: 'firewall.view' },
    { label: 'SSH access', to: '/ssh', permission: 'ssh.view' },
    { label: 'Updates', to: '/updates', permission: 'packages.view' },
    { label: 'Cloudflare' },
    { label: 'Activity', to: '/activity', permission: 'audit.view' },
];

export function AppShell({ me }: { me: Me }) {
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    async function logout() {
        await api.post('/api/auth/logout');
        queryClient.clear();
        void navigate('/login', { replace: true });
    }

    return (
        <div className="flex min-h-full flex-col md:flex-row">
            <aside className="border-b border-slate-200 bg-white px-4 py-4 md:w-60 md:shrink-0 md:border-r md:border-b-0 dark:border-slate-800 dark:bg-slate-900">
                <Logo />
                <nav className="mt-6 flex gap-1 overflow-x-auto md:flex-col">
                    {navigation.filter((item) => !item.permission || me.permissions.includes(item.permission)).map((item) =>
                        item.to ? (
                            <NavLink
                                key={item.label}
                                to={item.to}
                                end
                                className={({ isActive }) =>
                                    `rounded-lg px-3 py-2 text-sm font-medium whitespace-nowrap ${isActive ? 'bg-brand-50 text-brand-700 dark:bg-brand-600/20 dark:text-brand-100' : 'text-slate-700 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800'}`
                                }
                            >
                                {item.label}
                            </NavLink>
                        ) : (
                            <span key={item.label} className="flex items-center justify-between gap-2 rounded-lg px-3 py-2 text-sm whitespace-nowrap text-slate-400 dark:text-slate-600">
                                {item.label}
                                <span className="text-[10px] uppercase tracking-wide">soon</span>
                            </span>
                        ),
                    )}
                </nav>
            </aside>
            <div className="flex min-w-0 flex-1 flex-col">
                <header className="flex items-center justify-end gap-3 border-b border-slate-200 px-6 py-3 text-sm dark:border-slate-800">
                    {me.remainingRecoveryCodes <= 3 && (
                        <span className="text-amber-700 dark:text-amber-400">{me.remainingRecoveryCodes} recovery codes left</span>
                    )}
                    <span className="text-slate-600 dark:text-slate-400">
                        {me.username} · {me.role}
                    </span>
                    <button onClick={() => void logout()} className="rounded-lg px-3 py-1.5 font-medium hover:bg-slate-100 dark:hover:bg-slate-800">
                        Sign out
                    </button>
                </header>
                {me.permissions.includes('firewall.view') && <PendingFirewallBanner />}
                <main className="flex-1 px-4 py-6 sm:px-6">
                    <Outlet />
                </main>
            </div>
        </div>
    );
}
