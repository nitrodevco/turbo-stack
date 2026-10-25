import { type ButtonHTMLAttributes, type InputHTMLAttributes, type ReactNode, type SelectHTMLAttributes, useEffect, useRef } from 'react';
import { useActiveEntry } from './nav';

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
    return (
        <div className={`rounded-xl border border-line bg-surface p-6 ${className}`}>
            {children}
        </div>
    );
}

type ButtonVariant = 'primary' | 'ghost' | 'danger' | 'secondary';

const buttonStyles: Record<ButtonVariant, string> = {
    primary: 'border border-accent bg-accent font-semibold text-on-accent hover:border-accent-hover hover:bg-accent-hover',
    secondary: 'border border-line bg-subtle text-ink hover:border-muted/50',
    ghost: 'border border-transparent text-muted hover:border-line hover:bg-subtle hover:text-ink',
    danger: 'border border-bad-line bg-bad-soft text-bad hover:border-bad/60',
};

export function Button({ className = '', variant = 'primary', size = 'md', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; size?: 'sm' | 'md' }) {
    return (
        <button
            className={`inline-flex items-center justify-center gap-2 rounded-lg font-medium whitespace-nowrap transition active:scale-[0.98] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent disabled:pointer-events-none disabled:opacity-50 [&>svg]:size-4 ${size === 'sm' ? 'h-7 px-2.5 text-xs' : 'h-9 px-3.5 text-sm'} ${buttonStyles[variant]} ${className}`}
            {...props}
        />
    );
}

const inputClass =
    'block w-full rounded-lg border border-line bg-canvas px-3 py-2 text-sm text-ink placeholder:text-muted/70 outline-none focus:border-accent focus:ring-2 focus:ring-accent/25 disabled:opacity-60';

export function Field({ label, hint, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string }) {
    return (
        <label className="block space-y-1.5">
            <span className="text-xs font-medium text-muted">{label}</span>
            <input className={inputClass} {...props} />
            {hint && <span className="block text-xs text-muted">{hint}</span>}
        </label>
    );
}

export function SelectField({ label, hint, children, ...props }: SelectHTMLAttributes<HTMLSelectElement> & { label: string; hint?: string }) {
    return (
        <label className="block space-y-1.5">
            <span className="text-xs font-medium text-muted">{label}</span>
            <select className={inputClass} {...props}>
                {children}
            </select>
            {hint && <span className="block text-xs text-muted">{hint}</span>}
        </label>
    );
}

export function Input(props: InputHTMLAttributes<HTMLInputElement>) {
    return <input {...props} className={`${inputClass} ${props.className ?? ''}`} />;
}

export function Select({ children, ...props }: SelectHTMLAttributes<HTMLSelectElement>) {
    return (
        <select {...props} className={`${inputClass} ${props.className ?? ''}`}>
            {children}
        </select>
    );
}

export function ErrorNote({ message }: { message?: string | null }) {
    if (!message) return null;

    return (
        <p role="alert" className="rounded-xl border border-bad-line bg-bad-soft px-4 py-3 text-sm text-bad">
            {message}
        </p>
    );
}

export function Notice({ tone = 'info', children }: { tone?: 'info' | 'warning' | 'danger' | 'success'; children: ReactNode }) {
    const tones = {
        info: 'border-accent/40 bg-accent-soft text-ink',
        warning: 'border-warn-line bg-warn-soft text-warn',
        danger: 'border-bad-line bg-bad-soft text-bad',
        success: 'border-good-line bg-good-soft text-good',
    };

    return <div className={`rounded-xl border px-4 py-3 text-sm ${tones[tone]}`}>{children}</div>;
}

type BadgeTone = 'neutral' | 'good' | 'warning' | 'danger' | 'info';

const badgeTones: Record<BadgeTone, string> = {
    neutral: 'border-line text-muted',
    good: 'border-good-line bg-good-soft text-good',
    warning: 'border-warn-line bg-warn-soft text-warn',
    danger: 'border-bad-line bg-bad-soft text-bad',
    info: 'border-accent/40 bg-accent-soft text-accent',
};

export function Badge({ tone = 'neutral', children }: { tone?: BadgeTone; children: ReactNode }) {
    return <span className={`inline-flex items-center gap-1.5 rounded-md border px-1.5 py-0.5 font-mono text-[11px] font-medium whitespace-nowrap uppercase ${badgeTones[tone]}`}>{children}</span>;
}

export type HeaderTabs<T extends string> = { items: { id: T; label: string }[]; value: T; onChange: (id: T) => void };

/**
 * The top of every page, in the bar along the top of the window as Turbo Admin has it: the page's
 * icon, its title and a line under it, and its actions to the right; along its bottom edge the
 * page's tabs (or, with none, its name marked as a tab is), so a page never has a second row of tabs
 * under its header. It reaches the window's edges past the page's own padding. On a phone it stays
 * at the top as an app bar.
 */
export function PageHeader<T extends string>({ title, description, actions, tabs }: { title: string; description?: ReactNode; actions?: ReactNode; tabs?: HeaderTabs<T> }) {
    const entry = useActiveEntry();

    return (
        <header className="sticky top-0 z-20 -mx-3 border-b border-line bg-chrome/95 backdrop-blur sm:-mx-4 lg:static lg:-mx-6 lg:bg-chrome lg:backdrop-blur-none">
            <div className="flex min-h-14 flex-wrap items-center gap-x-3 gap-y-2 px-3 py-2.5 sm:px-4 lg:min-h-17 lg:px-6">
                {entry && <span aria-hidden className="grid size-9 shrink-0 place-items-center rounded-xl bg-accent-soft text-accent max-sm:hidden [&>svg]:size-5">{entry.icon}</span>}
                <div className="min-w-0 flex-1 basis-60">
                    <h1 className="truncate text-base leading-7 font-semibold tracking-tight lg:text-lg">{title}</h1>
                    {description && <p className="text-[13px] leading-5 text-muted">{description}</p>}
                </div>
                {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
            </div>
            {tabs ? (
                <div role="tablist" className="no-scrollbar flex h-10 items-end gap-1 overflow-x-auto px-2 sm:px-4 lg:px-6">
                    {tabs.items.map((tab) => (
                        <button
                            key={tab.id}
                            type="button"
                            role="tab"
                            aria-selected={tab.id === tabs.value}
                            onClick={() => tabs.onChange(tab.id)}
                            className={`-mb-px shrink-0 border-b-2 px-3 py-2 text-sm font-medium whitespace-nowrap transition-colors ${tab.id === tabs.value ? 'border-accent text-accent' : 'border-transparent text-muted hover:text-ink'}`}
                        >
                            {tab.label}
                        </button>
                    ))}
                </div>
            ) : (
                <div className="flex h-10 items-end px-2 max-lg:hidden sm:px-4 lg:px-6">
                    <span className="-mb-px flex shrink-0 items-center gap-1.5 border-b-2 border-accent px-3 py-2 text-sm font-medium whitespace-nowrap text-accent [&>svg]:size-4">
                        {entry?.icon}
                        {title}
                    </span>
                </div>
            )}
        </header>
    );
}

export function Tabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string }[]; value: T; onChange: (id: T) => void }) {
    return (
        <div role="tablist" className="no-scrollbar flex gap-1 overflow-x-auto border-b border-line">
            {tabs.map((tab) => (
                <button
                    key={tab.id}
                    role="tab"
                    aria-selected={tab.id === value}
                    onClick={() => onChange(tab.id)}
                    className={`-mb-px border-b-2 px-3 py-2.5 text-sm font-medium whitespace-nowrap transition-colors ${tab.id === value ? 'border-accent text-accent' : 'border-transparent text-muted hover:text-ink'}`}
                >
                    {tab.label}
                </button>
            ))}
        </div>
    );
}

/** Wraps a table so it scrolls horizontally on narrow screens instead of the page. */
export function TableWrap({ children }: { children: ReactNode }) {
    return (
        <div className="overflow-x-auto rounded-xl border border-line bg-surface">
            <table className="w-full text-left text-sm [&_td]:px-4 [&_td]:py-2.5 [&_th]:px-4 [&_th]:py-2.5 [&_th]:font-mono [&_th]:text-[11px] [&_th]:font-medium [&_th]:tracking-[0.08em] [&_th]:text-muted [&_th]:uppercase [&_tbody_tr]:border-t [&_tbody_tr]:border-line">
                {children}
            </table>
        </div>
    );
}

export function Modal({ open, title, onClose, children }: { open: boolean; title: string; onClose: () => void; children: ReactNode }) {
    const ref = useRef<HTMLDialogElement>(null);

    useEffect(() => {
        const dialog = ref.current;

        if (open && dialog && !dialog.open) dialog.showModal();
        if (!open && dialog?.open) dialog.close();
    }, [open]);

    return (
        <dialog
            ref={ref}
            onClose={onClose}
            className="m-auto w-[min(32rem,calc(100vw-2rem))] rounded-xl border border-line bg-surface p-0 text-ink shadow-2xl"
        >
            <div className="space-y-4 p-6">
                <h2 className="text-lg font-semibold">{title}</h2>
                {children}
            </div>
        </dialog>
    );
}

export function Spinner({ label = 'Loading…' }: { label?: string }) {
    return <p className="py-6 text-center text-sm text-muted">{label}</p>;
}

/** The panel's mark and name: a teal tile and the name in the mono face. */
export function LogoMark() {
    return (
        <span className="flex items-center gap-3">
            <span className="grid size-8 place-items-center rounded-lg bg-accent font-mono text-sm font-bold text-on-accent">T</span>
            <span className="font-mono text-xs font-medium tracking-[0.08em] text-ink uppercase">Turbo Stack</span>
        </span>
    );
}

export function Logo() {
    return <LogoMark />;
}

export function CenteredPage({ children }: { children: ReactNode }) {
    return (
        <main className="flex min-h-full items-center justify-center px-4 py-12">
            <div className="w-full max-w-md space-y-6">
                <Logo />
                {children}
            </div>
        </main>
    );
}
