import { useQuery } from '@tanstack/react-query';
import { api, type AppSummary, type SiteSummary } from './api';
import { usePermissions } from './auth';

/**
 * Which sites serve which apps. Nothing stores the link: a site belongs to an app when it carries the
 * app's name (a recipe names the site after its app), when a route serves files from the app's folder,
 * or when a route proxies to one of the app's ports on this server. A site may serve several apps (a
 * hotel's site proxies its API to the emulator), and an app may have no site at all (the emulator).
 */
export function servesApp(site: SiteSummary, app: AppSummary): boolean {
    if (site.name === app.name) return true;

    const ports = new Set([app.servicePort, ...Object.values(app.ports)].filter((port): port is number => !!port));

    return site.routes.some((route) => {
        if (route.kind === 'Static' || route.kind === 'Spa') {
            return route.target === app.appDirectory || route.target.startsWith(`${app.appDirectory}/`);
        }

        if (route.kind === 'Proxy' || route.kind === 'WebSocket') {
            const port = localPort(route.target);

            return port !== null && ports.has(port);
        }

        return false;
    });
}

/** The port of an upstream on this server (http://127.0.0.1:8080/...), or null for anything else. */
function localPort(target: string): number | null {
    const match = /^(?:https?|wss?):\/\/(?:127\.0\.0\.1|localhost|\[::1\]):(\d+)/i.exec(target.trim());

    return match ? Number(match[1]) : null;
}

/**
 * The new-site page, filled in for an app: named after it, serving its built files (as a single-page
 * app) or, for a service, proxying to its port.
 */
export function newSiteLink(app: { name: string; staticPath: string | null; servicePort: number | null }): string {
    const params = new URLSearchParams({ name: app.name });

    if (app.staticPath) {
        params.set('target', app.staticPath);
        params.set('kind', 'Spa');
    } else if (app.servicePort) {
        params.set('target', `http://127.0.0.1:${app.servicePort}`);
        params.set('kind', 'Proxy');
    }

    return `/sites/new?${params}`;
}

/** The apps and sites the signed-in user may see, and the links between them. */
export function useAppsAndSites() {
    const can = usePermissions();
    const apps = useQuery({ queryKey: ['apps'], queryFn: () => api.get<AppSummary[]>('/api/apps'), refetchInterval: 10_000, enabled: can('apps.view') });
    const sites = useQuery({ queryKey: ['sites'], queryFn: () => api.get<SiteSummary[]>('/api/sites'), enabled: can('sites.view') });
    const appList = apps.data ?? [];
    const siteList = sites.data ?? [];

    return {
        apps,
        sites,
        sitesOf: (app: AppSummary) => siteList.filter((site) => servesApp(site, app)),
        appsOf: (site: SiteSummary) => appList.filter((app) => servesApp(site, app)),
    };
}
