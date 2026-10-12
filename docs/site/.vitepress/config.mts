import { defineConfig } from 'vitepress';

// Served from the install domain under /docs (scripts/package.sh --docs copies the build into dist/docs).
export default defineConfig({
    title: 'Turbo Manager',
    description: 'A self-hosted server panel for Turbo and Nitro hotels.',
    base: '/docs/',
    cleanUrls: true,
    lastUpdated: false,
    themeConfig: {
        nav: [
            { text: 'Guide', link: '/guide/install' },
            { text: 'Recovery', link: '/guide/troubleshooting' },
        ],
        sidebar: [
            {
                text: 'Getting started',
                items: [
                    { text: 'Installing', link: '/guide/install' },
                    { text: 'First-run setup', link: '/guide/setup' },
                    { text: 'Your first hotel', link: '/guide/hotels' },
                ],
            },
            {
                text: 'Running a hotel',
                items: [
                    { text: 'Apps and deploys', link: '/guide/deploys' },
                    { text: 'Sites and certificates', link: '/guide/sites' },
                    { text: 'Databases', link: '/guide/databases' },
                    { text: 'Backups', link: '/guide/backups' },
                    { text: 'Files, terminal and SFTP', link: '/guide/files' },
                ],
            },
            {
                text: 'Operating the server',
                items: [
                    { text: 'Updates', link: '/guide/updates' },
                    { text: 'Team, roles and API tokens', link: '/guide/team' },
                    { text: 'Security', link: '/guide/security' },
                    { text: 'Alerts', link: '/guide/alerts' },
                ],
            },
            {
                text: 'When things go wrong',
                items: [
                    { text: 'Troubleshooting', link: '/guide/troubleshooting' },
                    { text: 'Restoring the panel', link: '/guide/restore-panel' },
                    { text: 'Upgrading Ubuntu (24.04 → 26.04)', link: '/guide/ubuntu-upgrade' },
                ],
            },
        ],
        search: { provider: 'local' },
        outline: { level: [2, 3] },
    },
});
