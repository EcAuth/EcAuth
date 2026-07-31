import { createHash, randomBytes } from 'crypto';

/**
 * PKCE (RFC 7636) のヘルパー。
 * EcAuth は S256 のみサポートする（plain は許容しない）。
 */

/**
 * code_verifier を生成する。
 * RFC 7636 Section 4.1: 43〜128 文字の unreserved 文字列。
 * 32 バイトを base64url エンコードすると 43 文字になる。
 */
export function generateCodeVerifier(): string {
  return randomBytes(32).toString('base64url');
}

/**
 * code_verifier から code_challenge を導出する。
 * RFC 7636 Section 4.2: BASE64URL-ENCODE(SHA256(ASCII(code_verifier)))
 */
export function codeChallengeFromVerifier(codeVerifier: string): string {
  return createHash('sha256').update(codeVerifier, 'ascii').digest('base64url');
}

/**
 * code_verifier と対応する code_challenge のペアを生成する。
 */
export function generatePkcePair(): { codeVerifier: string; codeChallenge: string } {
  const codeVerifier = generateCodeVerifier();
  return { codeVerifier, codeChallenge: codeChallengeFromVerifier(codeVerifier) };
}
