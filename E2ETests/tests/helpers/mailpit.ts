import { APIRequestContext, request } from '@playwright/test';
import type { Mailbox, MailboxMessage, WaitForMessageOptions } from './mailbox';

/**
 * mailpit REST API のラッパ。
 *
 * Account 申込・マジックリンクの確認/ログイントークンは「メール本文にしか存在しない」
 * （DB には SHA-256 ハッシュのみ保存）。E2E でフローを完走するため、mailpit が受信した
 * メール本文からトークンを抽出する。
 *
 * mailpit は plain HTTP（既定 http://localhost:8025）で REST API を提供する。
 *
 * 注意: mailpit は全 spec で共有される単一インスタンス。Playwright の fullyParallel 実行で
 * 複数 spec が同時にメールを送るため、**全削除（DELETE /api/v1/messages 全体）は使わず**、
 * 宛先メールアドレス（run ごとに一意）で検索し、処理済みメッセージは ID 指定で削除する。
 */
const MAILPIT_BASE = process.env.MAILPIT_BASE_URL || 'http://localhost:8025';

export interface MailpitMessage {
  ID: string;
  /** プレーンテキスト本文 */
  Text: string;
  /** HTML 本文 */
  HTML: string;
  Subject: string;
}

/**
 * 指定した宛先メールアドレス宛の最新メッセージを取得する。
 * 送信は非同期のため、受信するまで一定間隔でポーリングする。
 *
 * @param request Playwright の APIRequestContext
 * @param toEmail 宛先メールアドレス（完全一致で検索）
 * @param opts.subjectIncludes 件名に含まれる文字列でさらに絞り込む（同一宛先に複数種のメールが届く場合）
 * @param opts.timeoutMs 最大待機時間（既定 20000ms）
 * @param opts.intervalMs ポーリング間隔（既定 500ms）
 */
export async function waitForMessage(
  request: APIRequestContext,
  toEmail: string,
  opts: { subjectIncludes?: string; timeoutMs?: number; intervalMs?: number } = {}
): Promise<MailpitMessage> {
  const timeoutMs = opts.timeoutMs ?? 20000;
  const intervalMs = opts.intervalMs ?? 500;
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    const res = await request.get(`${MAILPIT_BASE}/api/v1/search`, {
      params: { query: `to:${toEmail}` },
    });
    if (res.ok()) {
      const body = await res.json();
      const summaries: Array<{ ID: string; Subject?: string }> = Array.isArray(body.messages)
        ? body.messages
        : [];
      // 検索結果は新しい順。件名フィルタがあれば一致する最新を選ぶ。
      const summary = opts.subjectIncludes
        ? summaries.find((m) => (m.Subject ?? '').includes(opts.subjectIncludes!))
        : summaries[0];
      if (summary) {
        const detail = await request.get(`${MAILPIT_BASE}/api/v1/message/${summary.ID}`);
        if (detail.ok()) {
          return (await detail.json()) as MailpitMessage;
        }
      }
    }
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }

  const suffix = opts.subjectIncludes ? `（件名: ${opts.subjectIncludes}）` : '';
  throw new Error(`Mailpit: ${toEmail} 宛のメール${suffix}が ${timeoutMs}ms 以内に受信されませんでした。`);
}

/**
 * 指定した ID のメッセージを削除する（テスト後の後始末）。
 * 他 spec のメールを消さないよう、必ず処理済みの ID のみを渡すこと。
 */
export async function deleteMessages(request: APIRequestContext, ids: string[]): Promise<void> {
  if (ids.length === 0) {
    return;
  }
  await request.delete(`${MAILPIT_BASE}/api/v1/messages`, {
    data: { IDs: ids },
  });
}

/**
 * mailpit を {@link Mailbox} として使えるようにする。
 *
 * cleanup が宛先単位のインターフェースなのに ID 指定で消しているのは、mailpit が
 * 全 spec で共有される単一インスタンスだから。宛先で一括削除する API に寄せると、
 * 並列実行中の他 spec のメールまで巻き込みかねない。**自分が読んだ ID だけ**を
 * 覚えておいて消す（このモジュール冒頭の注意書きと同じ理由）。
 */
export async function createMailpitMailbox(): Promise<Mailbox> {
  const ctx = await request.newContext();
  const seenIds = new Map<string, Set<string>>();

  return {
    kind: 'mailpit',

    async waitForMessage(toEmail: string, opts: WaitForMessageOptions = {}): Promise<MailboxMessage> {
      const message = await waitForMessage(ctx, toEmail, opts);

      const ids = seenIds.get(toEmail) ?? new Set<string>();
      ids.add(message.ID);
      seenIds.set(toEmail, ids);

      return { subject: message.Subject, text: message.Text, html: message.HTML };
    },

    async cleanup(toEmail: string): Promise<void> {
      await deleteMessages(ctx, [...(seenIds.get(toEmail) ?? [])]);
      seenIds.delete(toEmail);
    },

    async dispose(): Promise<void> {
      await ctx.dispose();
    },
  };
}
