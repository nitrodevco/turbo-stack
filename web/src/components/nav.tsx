import { Archive, Bell, Braces, Building2, Cloud, Cog, Database, FolderOpen, Gauge, History, KeyRound, LockKeyhole, PackageCheck, Rocket, ScrollText, Shield, SlidersHorizontal, SquareTerminal, Users } from 'lucide-react';
import type { ReactNode } from 'react';
import { useLocation } from 'react-router';
import type { Me } from '../api';

/** One place in the navigation: where it goes, who may open it, and a line about it for the phone's menu. */
export type NavEntry = {
    label: string;
    to: string;
    icon: ReactNode;
    /** Shown to whoever has any of these. */
    permission?: string | string[];
    hint: string;
    /** Other paths that belong to it (a repository's page is under Apps). */
    also?: string[];
};

export type NavSection = { label: string | null; entries: NavEntry[] };

const SECTIONS: NavSection[] = [
    {
        label: null,
        entries: [
            { label: 'Dashboard', to: '/', icon: <Gauge />, permission: 'system.view', hint: 'The server now, and how it runs' },
            { label: 'Hotel', to: '/hotel', icon: <Building2 />, permission: 'apps.view', hint: 'Your hotels and their parts' },
        ],
    },
    {
        label: 'Web',
        entries: [
            {
                label: 'Apps & sites',
                to: '/apps',
                icon: <Rocket />,
                permission: ['apps.view', 'sites.view'],
                hint: 'Deploys from GitHub, and the domains that serve them',
                also: ['/sites', '/repositories'],
            },
            { label: 'Certificates', to: '/certificates', icon: <LockKeyhole />, permission: 'sites.view', hint: 'HTTPS certificates' },
            { label: 'Cloudflare', to: '/cloudflare', icon: <Cloud />, permission: 'sites.view', hint: 'DNS and proxying' },
        ],
    },
    {
        label: 'Data',
        entries: [
            { label: 'Databases', to: '/databases', icon: <Database />, permission: 'databases.view', hint: 'MySQL, MariaDB, PostgreSQL' },
            { label: 'Backups', to: '/backups', icon: <Archive />, permission: 'backups.view', hint: 'Schedules, targets, restores' },
        ],
    },
    {
        label: 'Server',
        entries: [
            { label: 'Services', to: '/services', icon: <Cog />, permission: 'services.view', hint: 'systemd services' },
            { label: 'Logs', to: '/logs', icon: <ScrollText />, permission: 'logs.view', hint: 'The system journal' },
            { label: 'Terminal', to: '/terminal', icon: <SquareTerminal />, permission: 'terminal.app', hint: 'A shell in the browser' },
            { label: 'Files', to: '/files', icon: <FolderOpen />, permission: 'files.manage', hint: 'An app user\'s files' },
            { label: 'Updates', to: '/updates', icon: <PackageCheck />, permission: 'packages.view', hint: 'The panel and system packages' },
            { label: 'Alerts', to: '/alerts', icon: <Bell />, permission: 'system.view', hint: 'What the panel watches' },
        ],
    },
    {
        label: 'Security',
        entries: [
            { label: 'Firewall', to: '/firewall', icon: <Shield />, permission: 'firewall.view', hint: 'Ports, rules, bans' },
            { label: 'SSH access', to: '/ssh', icon: <KeyRound />, permission: 'ssh.view', hint: 'Who can log in, and how' },
        ],
    },
    {
        label: 'Panel',
        entries: [
            { label: 'Team', to: '/team', icon: <Users />, permission: 'users.manage', hint: 'Users, roles, invites' },
            { label: 'Activity', to: '/activity', icon: <History />, permission: 'audit.view', hint: 'Changes and sign-ins' },
            { label: 'API', to: '/api-docs', icon: <Braces />, permission: 'system.view', hint: 'Tokens and reference' },
            { label: 'Settings', to: '/settings', icon: <SlidersHorizontal />, permission: 'sites.view', hint: 'The panel\'s address and more' },
        ],
    },
];

/** The navigation's sections, with only the entries the signed-in user may open. */
export function navFor(me: Me): NavSection[] {
    return SECTIONS.map((section) => ({ label: section.label, entries: section.entries.filter((entry) => !entry.permission || [entry.permission].flat().some((p) => me.permissions.includes(p))) })).filter(
        (section) => section.entries.length > 0,
    );
}

const under = (pathname: string, to: string) => (to === '/' ? pathname === '/' : pathname === to || pathname.startsWith(`${to}/`));

const isAt = (pathname: string, entry: NavEntry) => under(pathname, entry.to) || !!entry.also?.some((path) => under(pathname, path));

/** Whether an entry holds the page open now. */
export function useIsActive() {
    const { pathname } = useLocation();

    return (entry: NavEntry) => isAt(pathname, entry);
}

/** The navigation's entry for the page open now, if it has one. */
export function useActiveEntry(): NavEntry | undefined {
    const { pathname } = useLocation();

    return SECTIONS.flatMap((section) => section.entries).find((entry) => isAt(pathname, entry));
}
