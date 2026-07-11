using ConsoleAppFramework;
using IdentityProvider.Models;
using IdpUtilities.Security;
using Microsoft.EntityFrameworkCore;

namespace EcAuthConsoleApp.Commands
{
    /// <summary>
    /// 平文のまま保存されている <c>client.client_secret</c> を Key Vault 暗号化（<c>kv1:</c> エンベロープ）へ
    /// 移行する一回限りの backfill コマンド（EcAuthDocs#106）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 暗号化導入前にシードされた既存クライアントの <c>client_secret</c> は平文のまま残る
    /// （冪等シーダーは既存行を skip するため）。認証はレガシー平文経路で従来どおり動くが、暗号化の
    /// 恩恵は受けられない。本コマンドで既存の平文行を <c>ProtectAsync</c> により <c>kv1:</c> 化する。
    /// </para>
    /// <para>
    /// <b>冪等</b>: 既に <c>kv1:</c> の行は skip する。何度実行しても安全。
    /// </para>
    /// <para>
    /// <b>実行前提</b>: 暗号化を検証できるのは暗号化対応アプリ（ISecretProtector 導入済み）が稼働する環境
    /// だけなので、対象環境が暗号化対応版をデプロイ済みであること。<c>ClientSecretProtection__KeyVaultKeyId</c>
    /// が設定され、実行アイデンティティが対象鍵に <c>Get</c> 権限を持つこと（暗号化はローカル処理のため
    /// <c>Get</c> で足りる）。
    /// </para>
    /// </remarks>
    [RegisterCommands]
    internal class BackfillClientSecretsCommand
    {
        private const string KeyVaultSchemePrefix = "kv1:";

        private readonly EcAuthDbContext _db;
        private readonly ISecretProtector _secretProtector;

        public BackfillClientSecretsCommand(EcAuthDbContext db, ISecretProtector secretProtector)
        {
            _db = db;
            _secretProtector = secretProtector;
        }

        /// <summary>
        /// 平文の client.client_secret を Key Vault 暗号化（kv1:）へ移行する。
        /// </summary>
        /// <param name="dryRun">-d, 実際には保存せず対象件数のみ表示する。</param>
        public async Task BackfillClientSecrets(bool dryRun = false, CancellationToken cancellationToken = default)
        {
            // Client 自体はテナントフィルタを持たないが、将来フィルタが追加されても全テナントを
            // 対象化できるよう防御的に IgnoreQueryFilters() を付ける。
            var clients = await _db.Clients
                .IgnoreQueryFilters()
                .ToListAsync(cancellationToken);

            var total = clients.Count;
            var migrated = 0;
            var alreadyEncrypted = 0;
            var empty = 0;

            foreach (var client in clients)
            {
                if (string.IsNullOrEmpty(client.ClientSecret))
                {
                    empty++;
                    continue;
                }

                // kv1: プレフィックスの有無で暗号化済みか判定する。SecretEnvelope は IdpUtilities 内 internal の
                // ため、ここでは公開されたエンベロープ規約（プレフィックス = 暗号化済み / 無印 = レガシー平文）で判定する。
                if (client.ClientSecret.StartsWith(KeyVaultSchemePrefix, StringComparison.Ordinal))
                {
                    alreadyEncrypted++;
                    continue;
                }

                if (dryRun)
                {
                    // 値そのものは出力しない（client_id のみ）。
                    Console.WriteLine($"[dry-run] would encrypt: client_id={client.ClientId}");
                    migrated++;
                    continue;
                }

                client.ClientSecret = await _secretProtector.ProtectAsync(client.ClientSecret, cancellationToken);
                migrated++;
                Console.WriteLine($"encrypted: client_id={client.ClientId}");
            }

            if (!dryRun && migrated > 0)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            Console.WriteLine(
                $"Backfill {(dryRun ? "(dry-run) " : string.Empty)}completed: " +
                $"total={total}, migrated={migrated}, already_encrypted={alreadyEncrypted}, empty={empty}");
        }
    }
}
