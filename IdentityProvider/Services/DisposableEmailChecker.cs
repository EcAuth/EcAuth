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
        // 埋め込みリソースの論理名（既定: {RootNamespace}.{相対パスのドット区切り}）。
        private const string ResourceName = "IdentityProvider.Resources.disposable_email_domains.txt";

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
            using var stream = assembly.GetManifestResourceStream(ResourceName);
            if (stream == null)
            {
                // リソースが見つからない場合は空のリストで動作する（判定は常に false）。
                return set;
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
