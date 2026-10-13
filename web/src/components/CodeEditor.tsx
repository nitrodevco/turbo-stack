import { DiffEditor, Editor, loader } from '@monaco-editor/react';
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api';
// Syntax highlighting for the common languages (no language-service workers: nothing here runs foreign code).
import 'monaco-editor/esm/vs/basic-languages/monaco.contribution';
// The icon font (find widget, diff arrows); served as a file, which the panel's CSP allows.
import 'monaco-editor/esm/vs/base/browser/ui/codicons/codicon/codicon.css';
import EditorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import { useEffect, useRef, useState } from 'react';

// Bundled with the panel: the default loader fetches Monaco from a CDN, which the panel's CSP (rightly) refuses.
self.MonacoEnvironment = { getWorker: () => new EditorWorker() };
loader.config({ monaco });

const languages: Record<string, string> = {
    js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript', ts: 'typescript', tsx: 'typescript',
    json: 'json', md: 'markdown', yml: 'yaml', yaml: 'yaml', html: 'html', htm: 'html', css: 'css', scss: 'scss',
    xml: 'xml', cs: 'csharp', csproj: 'xml', sh: 'shell', bash: 'shell', ini: 'ini', conf: 'ini', env: 'ini', toml: 'ini',
    sql: 'sql', py: 'python', php: 'php', dockerfile: 'dockerfile', lua: 'lua',
};

export function languageOf(path: string): string {
    const name = path.split('/').pop()!.toLowerCase();

    if (name === 'dockerfile') return 'dockerfile';
    if (name.startsWith('.env')) return 'ini';

    return languages[name.split('.').pop() ?? ''] ?? 'plaintext';
}

function useDarkMode() {
    const query = '(prefers-color-scheme: dark)';
    const [dark, setDark] = useState(() => window.matchMedia(query).matches || document.documentElement.classList.contains('dark'));

    useEffect(() => {
        const media = window.matchMedia(query);
        const update = () => setDark(media.matches || document.documentElement.classList.contains('dark'));
        media.addEventListener('change', update);

        return () => media.removeEventListener('change', update);
    }, []);

    return dark;
}

const options = { fontSize: 13, minimap: { enabled: false }, scrollBeyondLastLine: false, automaticLayout: true, tabSize: 4 } as const;

/** The file manager's editor (Monaco). Ctrl/Cmd+S saves. With <c>original</c>, shows a side-by-side diff instead. */
export default function CodeEditor({
    path,
    value,
    original,
    onChange,
    onSave,
}: {
    path: string;
    value: string;
    original?: string;
    onChange: (value: string) => void;
    onSave: () => void;
}) {
    const theme = useDarkMode() ? 'vs-dark' : 'vs';
    const language = languageOf(path);
    // The editor binds Ctrl+S once; it must call the latest save (with the latest text).
    const save = useRef(onSave);
    save.current = onSave;

    if (original !== undefined) {
        return (
            <div className="h-[32rem] overflow-hidden rounded-lg border border-slate-300 dark:border-slate-700">
                <DiffEditor original={original} modified={value} language={language} theme={theme} options={{ ...options, readOnly: true, renderSideBySide: true }} />
            </div>
        );
    }

    return (
        <div className="h-[32rem] overflow-hidden rounded-lg border border-slate-300 dark:border-slate-700">
            <Editor
                path={path}
                value={value}
                language={language}
                theme={theme}
                options={options}
                onChange={(next) => onChange(next ?? '')}
                onMount={(editor) => editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, () => save.current())}
            />
        </div>
    );
}
