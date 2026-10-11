import type { ButtonHTMLAttributes, InputHTMLAttributes, ReactNode } from 'react';

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
    return (
        <div className={`rounded-xl border border-slate-200 bg-white p-6 shadow-sm dark:border-slate-800 dark:bg-slate-900 ${className}`}>
            {children}
        </div>
    );
}

export function Button({ className = '', variant = 'primary', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: 'primary' | 'ghost' }) {
    const styles =
        variant === 'primary'
            ? 'bg-brand-600 text-white hover:bg-brand-700 disabled:opacity-60'
            : 'text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800';

    return (
        <button
            className={`inline-flex items-center justify-center gap-2 rounded-lg px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-brand-500 ${styles} ${className}`}
            {...props}
        />
    );
}

export function Field({ label, hint, ...props }: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string }) {
    return (
        <label className="block space-y-1.5">
            <span className="text-sm font-medium text-slate-700 dark:text-slate-300">{label}</span>
            <input
                className="block w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm shadow-xs outline-none focus:border-brand-500 focus:ring-2 focus:ring-brand-500/30 dark:border-slate-700 dark:bg-slate-950"
                {...props}
            />
            {hint && <span className="block text-xs text-slate-500">{hint}</span>}
        </label>
    );
}

export function ErrorNote({ message }: { message?: string | null }) {
    if (!message) return null;

    return (
        <p role="alert" className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700 dark:border-red-900 dark:bg-red-950 dark:text-red-300">
            {message}
        </p>
    );
}

export function Logo() {
    return (
        <div className="flex items-center gap-2.5">
            <img src="/favicon.svg" alt="" className="size-8" />
            <span className="text-lg font-semibold tracking-tight">Turbo Manager</span>
        </div>
    );
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
