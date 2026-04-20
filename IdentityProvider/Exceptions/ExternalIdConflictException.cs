namespace IdentityProvider.Exceptions
{
    /// <summary>
    /// ExternalId の自動同期対象が、同一組織内の別ユーザーに既に使われている場合にスローされる例外。
    /// HTTP API としては 409 Conflict に変換される。
    /// </summary>
    public class ExternalIdConflictException : Exception
    {
        public ExternalIdConflictException(string message, Exception? innerException = null)
            : base(message, innerException) { }
    }
}
