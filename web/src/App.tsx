import { useQuery } from '@tanstack/react-query';
import { Navigate, Route, Routes, useLocation } from 'react-router';
import { api, ApiError, type Me, type SetupStatus } from './api';
import { AppShell } from './components/AppShell';
import { CenteredPage, ErrorNote } from './components/ui';
import { ActivityPage } from './pages/ActivityPage';
import { DashboardPage } from './pages/DashboardPage';
import { FirewallPage } from './pages/FirewallPage';
import { LoginPage } from './pages/LoginPage';
import { LogsPage } from './pages/LogsPage';
import { ServicesPage } from './pages/ServicesPage';
import { SetupPage } from './pages/SetupPage';
import { SshPage } from './pages/SshPage';
import { UpdatesPage } from './pages/UpdatesPage';

export function App() {
    const location = useLocation();
    const status = useQuery({ queryKey: ['setup-status'], queryFn: () => api.get<SetupStatus>('/api/setup/status') });
    const me = useQuery({
        queryKey: ['me'],
        queryFn: () => api.get<Me>('/api/auth/me'),
        enabled: status.data?.claimed === true,
        retry: (count, error) => !(error instanceof ApiError && error.status === 401) && count < 2,
    });

    if (status.isError) {
        return (
            <CenteredPage>
                <ErrorNote message="The panel API is not reachable. Check that tm-panel is running." />
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
        <Routes>
            <Route path="/login" element={me.data ? <Navigate to="/" replace /> : <LoginPage />} />
            <Route path="/setup" element={<Navigate to="/" replace />} />
            <Route element={me.data ? <AppShell me={me.data} /> : <Navigate to="/login" replace />}>
                <Route index element={<DashboardPage />} />
                <Route path="services" element={<ServicesPage />} />
                <Route path="logs" element={<LogsPage />} />
                <Route path="firewall" element={<FirewallPage />} />
                <Route path="ssh" element={<SshPage />} />
                <Route path="updates" element={<UpdatesPage />} />
                <Route path="activity" element={<ActivityPage />} />
                <Route path="*" element={<Navigate to="/" replace />} />
            </Route>
        </Routes>
    );
}
