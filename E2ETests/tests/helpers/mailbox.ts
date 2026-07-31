/**
 * 確認メール / マジックリンクの受信口を抽象化する。
 *
 * Account 申込のトークンは「メール本文にしか存在しない」（DB には SHA-256 ハッシュのみ）。
 * E2E で申込フローを完走するには本文を機械的に読む必要があるが、受信口は環境で違う:
 *
 *   - ローカル / CI      … mailpit（compose で同居。plain HTTP）
 *   - デプロイ済み環境   … SendGrid 送信なので、Inbound Parse → Cloudflare Worker の
 *                          E2E 用メールボックス（詳細は EcAuth の CLAUDE.md）
 *
 * spec 側が受信口の違いを知らずに済むよう、両者をこの Mailbox に揃える。
 * 選択は E2E_MAILBOX_KIND（既定 mailpit）。
 *
 * 後始末を「ID の配列」ではなく「宛先」で表現しているのは、Worker 側の DELETE が
 * 宛先単位でしか消せないため（1 メッセージ 1 キーで保存し、ID を返さない）。
 * mailpit 実装は他 spec のメールを巻き込まないよう、自分が読んだ ID だけを覚えて消す。
 */
import { createMailpitMailbox } from './mailpit';
import { createE2EMailbox } from './e2e-mailbox';

export type MailboxKind = 'mailpit' | 'e2e-mailbox';

/** 受信口によらず spec が必要とする最小限のフィールド。 */
export interface MailboxMessage {
  subject: string;
  /** プレーンテキスト本文 */
  text: string;
  /** HTML 本文 */
  html: string;
}

export interface WaitForMessageOptions {
  /** 件名に含まれる文字列で絞り込む（同一宛先に複数種のメールが届く場合） */
  subjectIncludes?: string;
  /** 最大待機時間。既定値は実装ごとに異なる（配送経路の長さが違うため） */
  timeoutMs?: number;
  /** ポーリング間隔。既定値は実装ごとに異なる */
  intervalMs?: number;
}

export interface Mailbox {
  readonly kind: MailboxKind;
  /** 宛先宛の最新メッセージを、届くまでポーリングして返す。 */
  waitForMessage(toEmail: string, opts?: WaitForMessageOptions): Promise<MailboxMessage>;
  /** その宛先宛のメールを片付ける（テスト後の後始末）。 */
  cleanup(toEmail: string): Promise<void>;
  dispose(): Promise<void>;
}

/**
 * E2E_MAILBOX_KIND に従って受信口を組み立てる。
 * 呼び出し側は afterAll などで必ず dispose() すること。
 */
export async function createMailbox(): Promise<Mailbox> {
  const kind = (process.env.E2E_MAILBOX_KIND || 'mailpit') as MailboxKind;

  switch (kind) {
    case 'mailpit':
      return createMailpitMailbox();
    case 'e2e-mailbox':
      return createE2EMailbox();
    default:
      throw new Error(
        `E2E_MAILBOX_KIND=${kind} は未対応です（mailpit / e2e-mailbox のいずれかを指定してください）。`
      );
  }
}

/**
 * メール本文（プレーンテキスト推奨）から `token=...` クエリの値を抽出して URL デコードする。
 * 確認 URL（/signup/confirm?token=...）とマジックリンク URL（/signin/magic-link?token=...）の
 * どちらにも対応する。受信口に依存しない純関数。
 */
export function extractToken(body: string, tokenParam: string = 'token'): string {
  // URL-safe な base64（英数字 + - _ . ~ %）を貪欲に拾い、区切り文字（引用符・空白・タグ・& 等）で止める。
  const match = body.match(new RegExp(`[?&]${tokenParam}=([^"'&\\s<>\\)]+)`));
  if (!match) {
    throw new Error(`メール本文から ${tokenParam} を抽出できませんでした。`);
  }
  return decodeURIComponent(match[1]);
}
