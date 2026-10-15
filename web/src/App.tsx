import { useQuery } from '@tanstack/react-query';
import { Navigate, Route, Routes, useLocation } from 'react-router';
import { api, ApiError, type Me, type SetupStatus } from './api';
import { AppShell } from './components/AppShell';
import { CenteredPage, ErrorNote } from './components/ui';
import { ActivityPage } from './pages/ActivityPage';
import { AppDetailPage } from './pages/AppDetailPage';
import { RecipePage } from './pages/RecipePage';
import { HotelPage, HotelsPage } from './pages/HotelPage';
import { HotelWizardPage } from './pages/HotelWizardPage';
import { AccountPage } from './pages/AccountPage';
import { AlertsPage } from './pages/AlertsPage';
import { TerminalPage } from './pages/TerminalPage';
import { FilesPage } from './pages/FilesPage';
import { InvitePage } from './pages/InvitePage';
import { TeamPage } from './pages/TeamPage';
import { SudoProvider } from './components/Sudo';
import { AppsPage } from './pages/AppsPage';
import { CertificatesPage } from './pages/CertificatesPage';
import { CloudflarePage } from './pages/CloudflarePage';
import { DashboardPage } from './pages/DashboardPage';
import { DatabasesPage } from './pages/DatabasesPage';
import { FirewallPage } from './pages/FirewallPage';
import { LoginPage } from './pages/LoginPage';
import { LogsPage } from './pages/LogsPage';
import { RepositoryPage } from './pages/RepositoryPage';
import { AddressCheckPage, HandoverPage } from './pages/PublicPages';
import { ServicesPage } from './pages/ServicesPage';
import { SettingsPage } from './pages/SettingsPage';
import { SetupPage } from './pages/SetupPage';
import { SiteEditorPage } from './pages/SiteEditorPage';
import { SshPage } from './pages/SshPage';
import { UpdatesPage } from './pages/UpdatesPage';
import { BackupsPage } from './pages/BackupsPage';
import { ApiPage } from './pages/ApiPage';

export function App() {
    const location = useLocation();
    const status = useQuery({ queryKey: ['setup-status'], queryFn: () => api.get<SetupStatus>('/api/setup/status') });
    const me = useQuery({
        queryKey: ['me'],
        queryFn: () => api.get<Me>('/api/auth/me'),
        enabled: status.data?.claimed === true,
        retry: (count, error) => !(error instanceof ApiError && error.status === 401) && count < 2,
    });

    // Reached on the panel's new domain before the user is signed in there.
    if (location.pathname === '/address-check') return <AddressCheckPage />;
    if (location.pathname === '/handover') return <HandoverPage />;

    if (status.isError) {
        return (
            <CenteredPage>
                <ErrorNote message="The panel API is not reachable. Check that ts-panel is running." />
            </CenteredPage>
        );
    }

    if (status.isPending || (status.data.claimed && me.isPending)) {
        return null;
    }

    if (!status.data.claimed) {
        // Until an owner exists, the setup wizard is the only page.
        return location.pathname === '/setup' ? <SetupPage /> : <Navigate to={`/setup${location.search}`} replace />;
    }

    return (
        <SudoProvider>
        <Routes>
            <Route path="/login" element={me.data ? <Navigate to="/" replace /> : <LoginPage />} />
            <Route path="/setup" element={<Navigate to="/" replace />} />
            <Route path="/invite" element={<InvitePage />} />
            <Route element={me.data ? <AppShell me={me.data} /> : <Navigate to="/login" replace />}>
                <Route index element={<DashboardPage />} />
                <Route path="services" element={<ServicesPage />} />
                <Route path="logs" element={<LogsPage />} />
                <Route path="firewall" element={<FirewallPage />} />
                <Route path="ssh" element={<SshPage />} />
                <Route path="updates" element={<UpdatesPage />} />
                <Route path="backups" element={<BackupsPage />} />
                <Route path="api-docs" element={<ApiPage />} />
                <Route path="activity" element={<ActivityPage />} />
                <Route path="sites" element={<Navigate to="/apps?show=sites" replace />} />
                <Route path="sites/new" element={<SiteEditorPage />} />
                <Route path="sites/:id" element={<SiteEditorPage />} />
                <Route path="certificates" element={<CertificatesPage />} />
                <Route path="settings" element={<SettingsPage />} />
                <Route path="cloudflare" element={<CloudflarePage />} />
                <Route path="databases" element={<DatabasesPage />} />
                <Route path="apps" element={<AppsPage />} />
                <Route path="team" element={<TeamPage />} />
                <Route path="alerts" element={<AlertsPage />} />
                <Route path="terminal" element={<TerminalPage />} />
                <Route path="files" element={<FilesPage />} />
                <Route path="account" element={me.data ? <AccountPage me={me.data} /> : null} />
                <Route path="hotel" element={<HotelsPage />} />
                <Route path="hotel/new" element={<HotelWizardPage />} />
                <Route path="hotel/:id" element={<HotelPage />} />
                <Route path="apps/new" element={<RecipePage />} />
                <Route path="apps/:id" element={<AppDetailPage />} />
                <Route path="repositories/:id" element={<RepositoryPage />} />
                <Route path="*" element={<Navigate to="/" replace />} />
            </Route>
        </Routes>
        </SudoProvider>
    );
}
