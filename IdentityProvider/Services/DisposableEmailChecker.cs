using System.Reflection;

namespace IdentityProvider.Services
{
    /// <summary>
    /// 使い捨て（一時）メールドメインの判定を行うサービス。
    /// ブロックリストは不変のため singleton 登録を想定する。
    /// </summary>
    public interface IDisposableEmailChecker
    {
        /// <summary>
        /// メールアドレスのドメインが使い捨てメールのブロックリストに含まれるか判定する。
        /// </summary>
        /// <param name="email">判定対象のメールアドレス</param>
        /// <returns>使い捨てメールドメインの場合 true</returns>
        bool IsDisposable(string email);
    }

    /// <inheritdoc cref="IDisposableEmailChecker" />
    public class DisposableEmailChecker : IDisposableEmailChecker
    {
        // 埋め込みリソースのファイル名（論理名のサフィックスで動的解決する）。
        private const string ResourceFileName = "disposable_email_domains.txt";

        // ブロックリストは不変。初回アクセス時に一度だけロードする。
        private static readonly Lazy<HashSet<string>> _domains = new(LoadDomains);

        /// <inheritdoc />
        public bool IsDisposable(string email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                return false;
            }

            var atIndex = email.LastIndexOf('@');
            if (atIndex < 0 || atIndex == email.Length - 1)
            {
                // ドメイン部が取得できない場合は判定対象外とする。
                return false;
            }

            var domain = email[(atIndex + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
            if (domain.Length == 0)
            {
                return false;
            }

            return _domains.Value.Contains(domain);
        }

        private static HashSet<string> LoadDomains()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var assembly = Assembly.GetExecutingAssembly();

            // リソース名はビルド構成（RootNamespace やフォルダ構成）で変わり得るため、
            // ファイル名サフィックスで動的に解決する。
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                // 見つからない場合に空リストを返すと使い捨てメール拒否が無効化される（fail-open）。
                // セキュリティ機能を黙って無効化しないよう、例外を投げて停止する（fail-closed）。
                throw new InvalidOperationException(
                    $"使い捨てメールドメインの埋め込みリソース（*{ResourceFileName}）が見つかりません。");
            }

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                throw new InvalidOperationException(
                    $"使い捨てメールドメインの埋め込みリソース（{resourceName}）のストリームを取得できませんでした。");
            }

            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var domain = line.Trim().TrimEnd('.').ToLowerInvariant();
                if (domain.Length == 0 || domain.StartsWith('#'))
                {
                    continue;
                }

                set.Add(domain);
            }

            return set;
        }
    }
}
