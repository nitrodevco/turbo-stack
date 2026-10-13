import { api } from './api';

// WebAuthn speaks ArrayBuffers; the panel speaks base64url JSON. These convert between the two.

const fromB64 = (text: string) => {
    const base64 = text.replace(/-/g, '+').replace(/_/g, '/');
    const binary = atob(base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '='));

    return Uint8Array.from(binary, (c) => c.charCodeAt(0)).buffer;
};

const toB64 = (buffer: ArrayBuffer | null) =>
    buffer ? btoa(String.fromCharCode(...new Uint8Array(buffer))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '') : null;

type Descriptor = { id: string; type: 'public-key'; transports?: AuthenticatorTransport[] };
type Ceremony<T> = { ceremony: string; options: T };
type CreationJson = Omit<PublicKeyCredentialCreationOptions, 'challenge' | 'user' | 'excludeCredentials'> & {
    challenge: string;
    user: { id: string; name: string; displayName: string };
    excludeCredentials?: Descriptor[];
};
type RequestJson = Omit<PublicKeyCredentialRequestOptions, 'challenge' | 'allowCredentials'> & { challenge: string; allowCredentials?: Descriptor[] };

const descriptors = (list?: Descriptor[]) => list?.map((d) => ({ ...d, id: fromB64(d.id) }));

export const passkeysSupported = () => typeof window.PublicKeyCredential !== 'undefined';

async function get(options: RequestJson) {
    const credential = (await navigator.credentials.get({
        publicKey: { ...options, challenge: fromB64(options.challenge), allowCredentials: descriptors(options.allowCredentials) },
    })) as PublicKeyCredential | null;

    if (!credential) throw new Error('No passkey was chosen.');
    const response = credential.response as AuthenticatorAssertionResponse;

    return {
        id: credential.id,
        rawId: toB64(credential.rawId),
        type: credential.type,
        response: {
            authenticatorData: toB64(response.authenticatorData),
            clientDataJSON: toB64(response.clientDataJSON),
            signature: toB64(response.signature),
            userHandle: toB64(response.userHandle),
        },
        clientExtensionResults: credential.getClientExtensionResults(),
    };
}

/** Adds a passkey to your account (the panel asks for your second factor first). */
export async function addPasskey(name: string) {
    const { ceremony, options } = await api.post<Ceremony<CreationJson>>('/api/account/passkeys/options');
    const credential = (await navigator.credentials.create({
        publicKey: {
            ...options,
            challenge: fromB64(options.challenge),
            user: { ...options.user, id: fromB64(options.user.id) },
            excludeCredentials: descriptors(options.excludeCredentials),
        },
    })) as PublicKeyCredential | null;

    if (!credential) throw new Error('No passkey was created.');
    const response = credential.response as AuthenticatorAttestationResponse;

    return api.post('/api/account/passkeys', {
        ceremony,
        name,
        response: {
            id: credential.id,
            rawId: toB64(credential.rawId),
            type: credential.type,
            response: { attestationObject: toB64(response.attestationObject), clientDataJSON: toB64(response.clientDataJSON) },
            clientExtensionResults: credential.getClientExtensionResults(),
        },
    });
}

/** Signs in with a passkey: no username, no password, no code. */
export async function signInWithPasskey() {
    const { ceremony, options } = await api.post<Ceremony<RequestJson>>('/api/auth/passkey/options');

    return api.post('/api/auth/passkey', { ceremony, response: await get(options) });
}

/** "Confirm it's you" with one of your passkeys. */
export async function confirmWithPasskey() {
    const { ceremony, options } = await api.post<Ceremony<RequestJson>>('/api/account/sudo/passkey/options');

    return api.post('/api/account/sudo/passkey', { ceremony, response: await get(options) });
}
