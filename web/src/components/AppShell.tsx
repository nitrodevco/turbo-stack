import { useQueryClient } from '@tanstack/react-query';
import { LayoutGrid, LogOut, Monitor, Moon, Sun, TriangleAlert } from 'lucide-react';
import { type ReactNode, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link, NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import { api, type Me } from '../api';
import { setTheme, type ThemeChoice, useTheme } from '../theme';
import { cx } from './cx';
import { type NavEntry, navFor, useIsActive } from './nav';
import { PendingFirewallBanner } from './PendingFirewallBanner';
import { LogoMark } from './ui';

const THEMES: { value: ThemeChoice; label: string; icon: ReactNode }[] = [
    { value: 'dark', label: 'Dark', icon: <Moon /> },
    { value: 'light', label: 'Light', icon: <Sun /> },
    { value: 'system', label: 'System', icon: <Monitor /> },
];

function useSignOut() {
    const navigate = useNavigate();
    const queryClient = useQueryClient();

    return async () => {
        await api.post('/api/auth/logout');
        queryClient.clear();
        void navigate('/login', { replace: true });
    };
}

const initials = (me: Me) => me.username.slice(0, 2).toUpperCase();

/** The small square buttons at the sidebar's foot: the theme, signing out. */
const footButton = 'grid size-9 shrink-0 place-items-center rounded-lg text-muted transition-colors hover:bg-subtle hover:text-ink [&>svg]:size-4.5';

/** Dark, light or as the system is: the chosen one's icon at the sidebar's foot, the three above it when opened. */
function RailTheme() {
    const choice = useTheme();
    const [open, setOpen] = useState(false);
    const box = useRef<HTMLDivElement>(null);
    const current = THEMES.find((entry) => entry.value === choice) ?? THEMES[0];

    useEffect(() => {
        if (!open) return;

        const away = (event: MouseEvent) => {
            if (!box.current?.contains(event.target as Node)) setOpen(false);
        };
        const onKey = (event: KeyboardEvent) => {
            if (event.key === 'Escape') setOpen(false);
        };

        document.addEventListener('mousedown', away);
        window.addEventListener('keydown', onKey);

        return () => {
            document.removeEventListener('mousedown', away);
            window.removeEventListener('keydown', onKey);
        };
    }, [open]);

    return (
        <div ref={box} className="relative">
            <button type="button" onClick={() => setOpen(!open)} title={`Theme: ${current.label.toLowerCase()}`} aria-label="Theme" aria-expanded={open} className={footButton}>
                {current.icon}
            </button>
            {open && (
                <div role="radiogroup" aria-label="Theme options" className="absolute right-0 bottom-full z-40 mb-2 flex gap-1 rounded-lg border border-line bg-surface p-1 shadow-xl">
                    {THEMES.map((entry) => (
                        <button
                            key={entry.value}
                            type="button"
                            role="radio"
                            aria-checked={entry.value === choice}
                            title={entry.label}
                            aria-label={entry.label}
                            onClick={() => {
                                setTheme(entry.value);
                                setOpen(false);
                            }}
                            className={cx('grid size-9 place-items-center rounded-md [&>svg]:size-4', entry.value === choice ? 'bg-subtle text-accent' : 'text-muted hover:text-ink')}
                        >
                            {entry.icon}
                        </button>
                    ))}
                </div>
            )}
        </div>
    );
}

function RailLink({ entry, active }: { entry: NavEntry; active: boolean }) {
    return (
        <Link
            to={entry.to}
            aria-current={active ? 'page' : undefined}
            className={cx(
                'relative flex h-9 items-center gap-3 rounded-lg px-3 text-sm transition-colors [&>svg]:size-4.5 [&>svg]:shrink-0',
                active
                    ? 'bg-subtle font-medium text-ink before:absolute before:inset-y-2 before:-left-3 before:w-0.75 before:rounded-r-full before:bg-accent [&>svg]:text-accent'
                    : 'text-muted hover:bg-subtle hover:text-ink',
            )}
        >
            {entry.icon}
            <span className="min-w-0 flex-1 truncate">{entry.label}</span>
        </Link>
    );
}

/** A warning that the account is running out of recovery codes, linking to where more are made. */
function RecoveryCodesWarning({ me, onClick }: { me: Me; onClick?: () => void }) {
    if (me.remainingRecoveryCodes > 3) return null;

    return (
        <Link to="/account" onClick={onClick} className="flex items-center gap-2 rounded-lg border border-warn-line bg-warn-soft px-3 py-2 text-xs text-warn [&>svg]:size-4 [&>svg]:shrink-0">
            <TriangleAlert />
            {me.remainingRecoveryCodes} recovery codes left
        </Link>
    );
}

/** From a laptop up: the places in their sections down the left, and at its foot your account, the theme and signing out. */
function Rail({ me }: { me: Me }) {
    const isActive = useIsActive();
    const signOut = useSignOut();

    return (
        <nav aria-label="Main" className="sticky top-0 hidden h-dvh w-60 shrink-0 flex-col border-r border-line bg-chrome lg:flex">
            <Link to="/" className="flex h-16 shrink-0 items-center gap-3 px-4">
                <LogoMark />
            </Link>
            <div className="no-scrollbar flex flex-1 flex-col gap-3 overflow-y-auto px-3 py-3">
                {navFor(me).map((section) => (
                    <div key={section.label ?? 'top'} className="flex flex-col gap-0.5">
                        {section.label && <span className="px-3 pb-1 font-mono text-[10px] font-medium tracking-[0.12em] text-muted/70 uppercase">{section.label}</span>}
                        {section.entries.map((entry) => (
                            <RailLink key={entry.to} entry={entry} active={isActive(entry)} />
                        ))}
                    </div>
                ))}
            </div>
            <div className="flex flex-col gap-2 border-t border-line px-3 py-3">
                <RecoveryCodesWarning me={me} />
                <div className="flex items-center gap-1">
                    <NavLink
                        to="/account"
                        title={`Your account (${me.role})`}
                        className={({ isActive: open }) => cx('flex h-9 min-w-0 flex-1 items-center gap-2 rounded-lg px-2 text-[13px] transition-colors', open ? 'bg-subtle text-ink' : 'text-muted hover:bg-subtle hover:text-ink')}
                    >
                        <span className="grid size-6 shrink-0 place-items-center rounded-md bg-warn font-mono text-[10px] font-semibold text-canvas">{initials(me)}</span>
                        <span className="truncate">{me.username}</span>
                    </NavLink>
                    <RailTheme />
                    <button type="button" onClick={() => void signOut()} title="Sign out" aria-label="Sign out" className={cx(footButton, 'hover:text-bad')}>
                        <LogOut />
                    </button>
                </div>
            </div>
        </nav>
    );
}

/** The places a phone's bottom bar keeps in reach, the first four of these the viewer may open. */
const DOCK = ['/', '/hotel', '/sites', '/apps', '/services', '/logs'];

function DockItem({ to, label, icon, active, onClick }: { to?: string; label: string; icon: ReactNode; active: boolean; onClick?: () => void }) {
    const body = (
        <>
            <span className={cx('grid h-7 w-12 place-items-center rounded-full transition-colors [&>svg]:size-5.25', active && 'bg-accent-soft text-accent')}>{icon}</span>
            <span className="max-w-full truncate">{label}</span>
        </>
    );
    const className = cx('flex min-w-0 flex-1 flex-col items-center gap-0.5 pt-2 pb-1.5 text-[11px] font-medium transition-colors', active ? 'text-ink' : 'text-muted');

    return to ? (
        <Link to={to} aria-current={active ? 'page' : undefined} className={className}>
            {body}
        </Link>
    ) : (
        <button type="button" onClick={onClick} className={className}>
            {body}
        </button>
    );
}

/** On a phone and a tablet: the bar along the bottom, where a thumb is, with the most used places and the menu, which holds everything. */
function BottomBar({ me, menuOpen, openMenu }: { me: Me; menuOpen: boolean; openMenu: () => void }) {
    const entries = navFor(me).flatMap((section) => section.entries);
    const isActive = useIsActive();
    const docked = DOCK.map((to) => entries.find((entry) => entry.to === to))
        .filter((entry) => !!entry)
        .slice(0, 4);
    const elsewhere = !docked.some((entry) => isActive(entry));

    return (
        <nav aria-label="Main" className="fixed inset-x-0 bottom-0 z-30 border-t border-line bg-chrome/95 pb-[env(safe-area-inset-bottom)] backdrop-blur lg:hidden">
            <div className="mx-auto flex max-w-xl items-stretch px-1">
                {docked.map((entry) => (
                    <DockItem key={entry.to} to={entry.to} label={entry.label} icon={entry.icon} active={isActive(entry)} />
                ))}
                <DockItem label="Menu" icon={<LayoutGrid />} active={menuOpen || elsewhere} onClick={openMenu} />
            </div>
        </nav>
    );
}

/**
 * On a phone and a tablet, everything: every place as a tile in its section, the one open now
 * marked, then your account, the theme and signing out. A sheet from the bottom that closes when a
 * place is picked, on a tap outside it, and on Escape.
 */
function MenuSheet({ me, open, close }: { me: Me; open: boolean; close: () => void }) {
    const isActive = useIsActive();
    const choice = useTheme();
    const signOut = useSignOut();
    const ref = useRef<HTMLDialogElement>(null);

    useEffect(() => {
        const dialog = ref.current;

        if (open && dialog && !dialog.open) dialog.showModal();
        if (!open && dialog?.open) dialog.close();
    }, [open]);

    return (
        <dialog
            ref={ref}
            onClose={close}
            onClick={(event) => event.target === event.currentTarget && close()}
            aria-label="Menu"
            className="mx-0 mt-auto mb-0 max-h-[88dvh] w-full max-w-none animate-sheet overflow-y-auto rounded-t-3xl border border-line bg-chrome p-0 text-ink shadow-2xl sm:mx-auto sm:max-w-xl lg:hidden"
        >
            {open && (
                <div className="flex flex-col gap-5 px-4 pt-2 pb-[calc(1rem+env(safe-area-inset-bottom))]">
                    <div aria-hidden className="mx-auto h-1 w-10 rounded-full bg-line" />
                    {navFor(me).map((section) => (
                        <section key={section.label ?? 'top'} aria-label={section.label ?? 'Overview'}>
                            {section.label && <h2 className="mb-2 px-1 font-mono text-[10px] font-medium tracking-[0.12em] text-muted uppercase">{section.label}</h2>}
                            <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
                                {section.entries.map((entry) => {
                                    const active = isActive(entry);

                                    return (
                                        <Link
                                            key={entry.to}
                                            to={entry.to}
                                            onClick={close}
                                            aria-current={active ? 'page' : undefined}
                                            className={cx(
                                                'flex min-h-18 flex-col justify-between gap-2 rounded-2xl border p-3 transition active:scale-[0.98] [&>svg]:size-5',
                                                active ? 'border-accent/50 bg-accent-soft text-accent' : 'border-line bg-surface text-ink hover:border-muted/50',
                                            )}
                                        >
                                            {entry.icon}
                                            <span className="min-w-0">
                                                <span className="block truncate text-sm font-medium">{entry.label}</span>
                                                <span className={cx('block truncate text-[11px]', active ? 'text-accent/80' : 'text-muted')}>{entry.hint}</span>
                                            </span>
                                        </Link>
                                    );
                                })}
                            </div>
                        </section>
                    ))}
                    <div className="flex flex-col gap-2 rounded-2xl border border-line bg-surface p-2">
                        <RecoveryCodesWarning me={me} onClick={close} />
                        <Link to="/account" onClick={close} className="flex min-h-12 items-center gap-3 rounded-xl px-2 hover:bg-subtle">
                            <span className="grid size-8 shrink-0 place-items-center rounded-lg bg-warn font-mono text-[11px] font-semibold text-canvas">{initials(me)}</span>
                            <span className="min-w-0 flex-1">
                                <span className="block truncate text-sm font-medium">{me.username}</span>
                                <span className="block text-[11px] text-muted">{me.role} · your account and passkeys</span>
                            </span>
                        </Link>
                        <div role="radiogroup" aria-label="Theme" className="mx-1 grid grid-cols-3 gap-1 rounded-lg border border-line bg-canvas p-1">
                            {THEMES.map((entry) => (
                                <button
                                    key={entry.value}
                                    type="button"
                                    role="radio"
                                    aria-checked={entry.value === choice}
                                    onClick={() => setTheme(entry.value)}
                                    className={cx('flex h-9 items-center justify-center gap-2 rounded-md text-sm [&>svg]:size-4', entry.value === choice ? 'bg-subtle font-medium text-ink' : 'text-muted')}
                                >
                                    {entry.icon}
                                    {entry.label}
                                </button>
                            ))}
                        </div>
                        <button type="button" onClick={() => void signOut()} className="flex min-h-12 items-center gap-3 rounded-xl px-3 text-[15px] text-bad hover:bg-bad-soft [&>svg]:size-5">
                            <LogOut />
                            Sign out
                        </button>
                    </div>
                </div>
            )}
        </dialog>
    );
}

/** The frame every signed-in page sits in: the sidebar from a laptop up, the bottom bar and its menu below that. */
export function AppShell({ me }: { me: Me }) {
    const { pathname } = useLocation();
    const [menuOpen, setMenuOpen] = useState(false);

    // A new page starts at its top, with the menu closed.
    useLayoutEffect(() => {
        window.scrollTo({ top: 0 });
        setMenuOpen(false);
    }, [pathname]);

    return (
        <div className="flex min-h-dvh">
            <Rail me={me} />
            <MenuSheet me={me} open={menuOpen} close={() => setMenuOpen(false)} />
            <div className="flex min-w-0 flex-1 flex-col">
                {me.permissions.includes('firewall.view') && <PendingFirewallBanner />}
                <main className="flex-1 px-3 pb-24 sm:px-4 lg:px-6 lg:pb-10">
                    <Outlet />
                </main>
            </div>
            <BottomBar me={me} menuOpen={menuOpen} openMenu={() => setMenuOpen(true)} />
        </div>
    );
}
