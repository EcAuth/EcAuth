import { APIRequestContext, Page } from '@playwright/test';

/**
 * B2B パスキーのセレモニー（登録・認証）を、EC-CUBE プラグインと同じ構成で実行するヘルパー。
 *
 * プラグインの実構成では役割が 2 つに分かれている。ここでもそれを再現する:
 *
 *   - **ブラウザ**（店舗のオリジン）… navigator.credentials.create / get のみ。
 *     WebAuthn は rp_id と origin の一致を要求するため、店舗のホストで開いたページで実行する。
 *   - **サーバー**（プラグイン → EcAuth）… options / verify の HTTP 呼び出し。
 *     宛先は /platform/v1/client-resolve が返す `https://<tenant_name>.ec-auth.io`
 *     （ClientResolveController）。Host にテナントが載ることで、EcAuth 側の
 *     グローバルクエリフィルタが顧客 Organization の行に一致する。
 *
 * ブラウザから直接 API を叩くと Host が店舗のホストになり、テナントが解決されずに
 * challenge が見つからない（"Session not found or expired"）。実構成と乖離するので行わない。
 */

/** サーバーが timeout=0 を返した場合に使う代替値（ミリ秒）。 */
const FALLBACK_TIMEOUT_MS = 60000;

export interface B2BContext {
  /** プラグインのサーバー側呼び出しを模す。Host: <tenant_name>.ec-auth.io を付けておくこと。 */
  api: APIRequestContext;
  /** API のベース URL（例 https://localhost:8081） */
  apiBaseUrl: string;
  /** rp_id と一致する origin で開かれているページ */
  page: Page;
}

export interface RegisterParams {
  clientId: string;
  clientSecret: string;
  rpId: string;
  b2bSubject: string;
  externalId: string;
  displayName?: string;
  deviceName?: string;
}

export interface AuthenticateParams {
  clientId: string;
  rpId: string;
  redirectUri: string;
  b2bSubject?: string;
  state?: string;
  codeChallenge?: string;
}

/* eslint-disable @typescript-eslint/no-explicit-any */
type WebAuthnOptions = any;
type CredentialPayload = Record<string, unknown>;

interface CeremonyOutcome {
  ok: boolean;
  credential?: CredentialPayload;
  error?: string;
}

async function postJson(
  ctx: B2BContext,
  path: string,
  body: Record<string, unknown>,
  what: string
): Promise<Record<string, any>> {
  const response = await ctx.api.post(`${ctx.apiBaseUrl}${path}`, { data: body });
  const text = await response.text();
  if (response.status() !== 200) {
    throw new Error(`${what} に失敗しました (${response.status()}): ${text}`);
  }
  return JSON.parse(text);
}

/**
 * パスキーを登録する。戻り値は register/verify のレスポンスボディ。
 */
export async function registerB2BPasskey(
  ctx: B2BContext,
  params: RegisterParams
): Promise<{ success: boolean; credential_id: string }> {
  const optionsBody = await postJson(
    ctx,
    '/v1/b2b/passkey/register/options',
    {
      client_id: params.clientId,
      client_secret: params.clientSecret,
      rp_id: params.rpId,
      b2b_subject: params.b2bSubject,
      external_id: params.externalId,
      display_name: params.displayName,
      device_name: params.deviceName,
    },
    'register/options'
  );

  const credential = await runCreateCeremony(ctx.page, optionsBody.options);

  return (await postJson(
    ctx,
    '/v1/b2b/passkey/register/verify',
    {
      client_id: params.clientId,
      client_secret: params.clientSecret,
      session_id: optionsBody.session_id,
      response: credential,
      device_name: params.deviceName,
    },
    'register/verify'
  )) as { success: boolean; credential_id: string };
}

/**
 * パスキーで認証し、認可コードを含む redirect_url を得る。
 *
 * redirect_uri は Client に登録された値と **完全一致** で検証される（B2BPasskeyController）。
 * テスト側で組み立てず、API から取得した登録済みの値をそのまま渡すこと。
 * これが申込時の初期値が実利用と噛み合っているかの検証になる。
 */
export async function authenticateB2BPasskey(
  ctx: B2BContext,
  params: AuthenticateParams
): Promise<{ redirect_url: string }> {
  const optionsBody = await postJson(
    ctx,
    '/v1/b2b/passkey/authenticate/options',
    {
      client_id: params.clientId,
      rp_id: params.rpId,
      b2b_subject: params.b2bSubject || undefined,
    },
    'authenticate/options'
  );

  const credential = await runGetCeremony(ctx.page, optionsBody.options);

  return (await postJson(
    ctx,
    '/v1/b2b/passkey/authenticate/verify',
    {
      client_id: params.clientId,
      session_id: optionsBody.session_id,
      redirect_uri: params.redirectUri,
      state: params.state || undefined,
      code_challenge: params.codeChallenge,
      code_challenge_method: params.codeChallenge ? 'S256' : undefined,
      response: credential,
    },
    'authenticate/verify'
  )) as { redirect_url: string };
}

/** ブラウザで navigator.credentials.create を実行し、サーバーに送る形へ整形して返す。 */
async function runCreateCeremony(page: Page, options: WebAuthnOptions): Promise<CredentialPayload> {
  const outcome: CeremonyOutcome = await page.evaluate(
    async ({ options, fallbackTimeoutMs }: { options: WebAuthnOptions; fallbackTimeoutMs: number }) => {
      const encode = (buffer: ArrayBuffer): string => {
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
      };
      const decode = (value: string): ArrayBuffer => {
        const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
        const binary = atob(base64 + '='.repeat((4 - (base64.length % 4)) % 4));
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes.buffer;
      };

      try {
        const credential = (await navigator.credentials.create({
          publicKey: {
            challenge: decode(options.challenge),
            rp: { id: options.rp.id, name: options.rp.name },
            user: {
              id: decode(options.user.id),
              name: options.user.name,
              displayName: options.user.displayName,
            },
            pubKeyCredParams: options.pubKeyCredParams,
            // サーバーが timeout=0 を返すケースがあるため有効値に落とす。
            timeout: options.timeout || fallbackTimeoutMs,
            attestation: options.attestation,
            authenticatorSelection: options.authenticatorSelection,
            excludeCredentials: (options.excludeCredentials || []).map((c: WebAuthnOptions) => ({
              type: c.type,
              id: decode(c.id),
              transports: c.transports,
            })),
          },
        })) as PublicKeyCredential;
        const attestation = credential.response as AuthenticatorAttestationResponse;

        return {
          ok: true,
          credential: {
            id: credential.id,
            rawId: encode(credential.rawId),
            response: {
              attestationObject: encode(attestation.attestationObject),
              clientDataJSON: encode(attestation.clientDataJSON),
              transports: attestation.getTransports ? attestation.getTransports() : [],
            },
            type: credential.type,
            clientExtensionResults: credential.getClientExtensionResults(),
          },
        };
      } catch (e) {
        return { ok: false, error: `${(e as Error).name}: ${(e as Error).message}` };
      }
    },
    { options, fallbackTimeoutMs: FALLBACK_TIMEOUT_MS }
  );

  if (!outcome.ok) {
    throw new Error(`navigator.credentials.create に失敗しました: ${outcome.error}`);
  }
  return outcome.credential!;
}

/** ブラウザで navigator.credentials.get を実行し、サーバーに送る形へ整形して返す。 */
async function runGetCeremony(page: Page, options: WebAuthnOptions): Promise<CredentialPayload> {
  const outcome: CeremonyOutcome = await page.evaluate(
    async ({ options, fallbackTimeoutMs }: { options: WebAuthnOptions; fallbackTimeoutMs: number }) => {
      const encode = (buffer: ArrayBuffer): string => {
        const bytes = new Uint8Array(buffer);
        let binary = '';
        for (let i = 0; i < bytes.byteLength; i++) binary += String.fromCharCode(bytes[i]);
        return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
      };
      const decode = (value: string): ArrayBuffer => {
        const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
        const binary = atob(base64 + '='.repeat((4 - (base64.length % 4)) % 4));
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes.buffer;
      };

      try {
        const credential = (await navigator.credentials.get({
          publicKey: {
            challenge: decode(options.challenge),
            rpId: options.rpId,
            allowCredentials: (options.allowCredentials || []).map((c: WebAuthnOptions) => ({
              type: c.type,
              id: decode(c.id),
              transports: c.transports,
            })),
            userVerification: options.userVerification,
            timeout: options.timeout || fallbackTimeoutMs,
          },
        })) as PublicKeyCredential;
        const assertion = credential.response as AuthenticatorAssertionResponse;

        return {
          ok: true,
          credential: {
            id: credential.id,
            rawId: encode(credential.rawId),
            response: {
              authenticatorData: encode(assertion.authenticatorData),
              clientDataJSON: encode(assertion.clientDataJSON),
              signature: encode(assertion.signature),
              userHandle: assertion.userHandle ? encode(assertion.userHandle) : null,
            },
            type: credential.type,
            clientExtensionResults: credential.getClientExtensionResults(),
          },
        };
      } catch (e) {
        return { ok: false, error: `${(e as Error).name}: ${(e as Error).message}` };
      }
    },
    { options, fallbackTimeoutMs: FALLBACK_TIMEOUT_MS }
  );

  if (!outcome.ok) {
    throw new Error(`navigator.credentials.get に失敗しました: ${outcome.error}`);
  }
  return outcome.credential!;
}
