import { APIRequestContext } from '@playwright/test';

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
 * メール本文（プレーンテキスト推奨）から `token=...` クエリの値を抽出して URL デコードする。
 * 確認 URL（/signup/confirm?token=...）とマジックリンク URL（/signin/magic-link?token=...）の
 * どちらにも対応する。
 */
export function extractToken(body: string, tokenParam: string = 'token'): string {
  // URL-safe な base64（英数字 + - _ . ~ %）を貪欲に拾い、区切り文字（引用符・空白・タグ・& 等）で止める。
  const match = body.match(new RegExp(`[?&]${tokenParam}=([^"'&\\s<>\\)]+)`));
  if (!match) {
    throw new Error(`メール本文から ${tokenParam} を抽出できませんでした。`);
  }
  return decodeURIComponent(match[1]);
}
