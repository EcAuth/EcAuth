import { request } from '@playwright/test';
import type { Mailbox, MailboxMessage, WaitForMessageOptions } from './mailbox';

/**
 * デプロイ済み環境向けの受信口（SendGrid Inbound Parse → Cloudflare Worker）。
 *
 * ecauth-infrastructure の environments/cdn が提供する。経路と制約は
 * ecauth-infrastructure の CLAUDE.md「E2E 用メールボックス」を参照。
 *
 *   GET    /messages?to=<address>   受信順（古い順）に返す。要 Bearer
 *   DELETE /messages?to=<address>   宛先単位で削除。要 Bearer
 *
 * レスポンスは {"messages":[{to, from, subject, text, html, received_at}, ...]}。
 * mailpit（Subject / Text / HTML）とフィールド名が違うので、ここで Mailbox の形に正規化する。
 */

const DEFAULT_BASE_URL = 'https://e2e-mail.ec-auth.io';

/**
 * 既定のタイムアウトが mailpit（20 秒）より長いのは、経路が違うため:
 *   SendGrid の送信キュー → MX 受信 → Inbound Parse の POST → D1 への書き込み
 * を待つ必要がある。ただし **D1 は強整合なので、書き込まれた直後から読める**。
 *
 * KV 時代はここが 180 秒 / 5 秒だった。書き込みが読み出しに反映されるまで数十秒かかり、
 * その伝播を待つ必要があったため。D1 へ移行して待つ対象が配送だけになったので縮めた
 * （EcAuth/ecauth-infrastructure#167）。移行直後の production verify の実測では、
 * 申込 → 確認メール → Account トークン取得が 38.5 秒 → 12.3 秒になっている。
 */
const DEFAULT_TIMEOUT_MS = 60000;
const DEFAULT_INTERVAL_MS = 2000;

interface WorkerMessage {
  to?: string;
  from?: string;
  subject?: string;
  text?: string;
  html?: string;
  received_at?: string;
}

export async function createE2EMailbox(): Promise<Mailbox> {
  const baseUrl = (process.env.E2E_MAILBOX_BASE_URL || DEFAULT_BASE_URL).replace(/\/+$/, '');
  const token = process.env.E2E_MAILBOX_API_TOKEN;
  if (!token) {
    throw new Error(
      'E2E_MAILBOX_API_TOKEN が未設定です。' +
        "op read 'op://EcAuth/ecauth-e2e-mailbox/api_token' の値を渡してください。"
    );
  }

  // トークンはコンテキストのヘッダに閉じ込める。個々の呼び出しで組み立てない。
  const ctx = await request.newContext({
    extraHTTPHeaders: { Authorization: `Bearer ${token}` },
  });

  /** 回復の見込みが無い失敗。ポーリングで粘らず即座に落とす。 */
  class FatalMailboxError extends Error {}

  async function fetchMessages(toEmail: string): Promise<WorkerMessage[]> {
    const response = await ctx.get(`${baseUrl}/messages`, { params: { to: toEmail } });
    if (!response.ok()) {
      const detail = `(${response.status()}): ${await response.text()}`;
      // 401/403 はトークン不一致・設定ミスで、待っても直らない。
      // タイムアウトまで粘ってから同じ理由で落ちるより、即座に理由を出す方が早く直せる。
      // 本文に読み出しトークンは含まれない。
      if (response.status() === 401 || response.status() === 403) {
        throw new FatalMailboxError(`E2E メールボックスの認証に失敗しました ${detail}`);
      }
      throw new Error(`E2E メールボックスの取得に失敗しました ${detail}`);
    }
    const body = await response.json();
    return Array.isArray(body.messages) ? (body.messages as WorkerMessage[]) : [];
  }

  return {
    kind: 'e2e-mailbox',

    async waitForMessage(toEmail: string, opts: WaitForMessageOptions = {}): Promise<MailboxMessage> {
      const timeoutMs = opts.timeoutMs ?? DEFAULT_TIMEOUT_MS;
      const intervalMs = opts.intervalMs ?? DEFAULT_INTERVAL_MS;
      const deadline = Date.now() + timeoutMs;

      let lastSubjects: string[] = [];
      let lastTransientError: string | undefined;

      while (Date.now() < deadline) {
        let messages: WorkerMessage[];
        try {
          // Worker は受信順（古い順）で返す。mailpit と揃えて新しい順で扱う。
          messages = (await fetchMessages(toEmail)).reverse();
        } catch (e) {
          if (e instanceof FatalMailboxError) {
            throw e;
          }
          // ネットワーク瞬断や Worker の一時的な 5xx で待機を打ち切らない。
          // 途中の 1 回の失敗で落ちると本番スモークが偽陰性になる
          // （本番スモークは retries: 0 で回るため取り返しがきかない）。
          lastTransientError = (e as Error).message;
          await new Promise((resolve) => setTimeout(resolve, intervalMs));
          continue;
        }

        lastSubjects = messages.map((m) => m.subject ?? '');

        const found = opts.subjectIncludes
          ? messages.find((m) => (m.subject ?? '').includes(opts.subjectIncludes!))
          : messages[0];

        if (found) {
          return {
            subject: found.subject ?? '',
            text: found.text ?? '',
            html: found.html ?? '',
          };
        }
        await new Promise((resolve) => setTimeout(resolve, intervalMs));
      }

      const suffix = opts.subjectIncludes ? `（件名: ${opts.subjectIncludes}）` : '';
      // 「1 通も届いていない」のか「別の件名しか無い」のかで原因が全く違うので区別できるようにする。
      const received =
        lastSubjects.length === 0
          ? '受信 0 件'
          : `受信済みの件名: ${lastSubjects.join(' / ')}`;
      // 取得自体が失敗し続けていた場合は、それが本当の原因なので添える。
      const transient = lastTransientError ? `。直近の取得エラー: ${lastTransientError}` : '';
      throw new Error(
        `E2E メールボックス: ${toEmail} 宛のメール${suffix}が ${timeoutMs}ms 以内に受信されませんでした（${received}）${transient}。`
      );
    },

    async cleanup(toEmail: string): Promise<void> {
      // 保持期間（1 時間）を過ぎた行は Worker が投入のついでに消すが、
      // 同一宛先での再実行に備えて明示的に消す。
      await ctx.delete(`${baseUrl}/messages`, { params: { to: toEmail } });
    },

    async dispose(): Promise<void> {
      await ctx.dispose();
    },
  };
}
