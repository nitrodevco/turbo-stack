import { useQueryClient } from '@tanstack/react-query';
import type { Me } from './api';

/** Returns a checker for the signed-in user's permissions (the API enforces them; this only hides controls). */
export function usePermissions() {
    const me = useQueryClient().getQueryData<Me>(['me']);

    return (permission: string) => me?.permissions.includes(permission) ?? false;
}

/** The signed-in user (from the cached /api/auth/me). */
export function useMe() {
    return useQueryClient().getQueryData<Me>(['me']);
}
