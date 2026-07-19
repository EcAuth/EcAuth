using IdentityProvider.Security;
using Xunit;

namespace IdentityProvider.Test.Security
{
    public class PkceValidatorTests
    {
        // RFC 7636 Appendix B の公式テストベクタ
        private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
        private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

        [Fact]
        public void Verify_ValidS256Vector_ReturnsTrue()
        {
            Assert.True(PkceValidator.Verify(Verifier, Challenge, "S256"));
        }

        [Fact]
        public void Verify_MethodOmitted_DefaultsToS256()
        {
            Assert.True(PkceValidator.Verify(Verifier, Challenge, null));
        }

        [Fact]
        public void Verify_WrongVerifier_ReturnsFalse()
        {
            Assert.False(PkceValidator.Verify("wrong-verifier-0000000000000000000000000000", Challenge, "S256"));
        }

        [Fact]
        public void Verify_PlainMethodNotSupported_ReturnsFalse()
        {
            // plain（verifier == challenge）でも S256 以外は許容しない
            Assert.False(PkceValidator.Verify(Verifier, Verifier, "plain"));
        }

        [Theory]
        [InlineData(null, Challenge)]
        [InlineData("", Challenge)]
        [InlineData(Verifier, null)]
        [InlineData(Verifier, "")]
        public void Verify_MissingInputs_ReturnsFalse(string? verifier, string? challenge)
        {
            Assert.False(PkceValidator.Verify(verifier, challenge, "S256"));
        }

        [Theory]
        [InlineData(42)]   // 43 未満
        [InlineData(129)]  // 128 超（極端に長い入力による資源枯渇対策）
        public void Verify_VerifierLengthOutOfRange_ReturnsFalse(int length)
        {
            // RFC 7636 Section 4.1: code_verifier は 43〜128 文字
            var verifier = new string('a', length);
            Assert.False(PkceValidator.Verify(verifier, Challenge, "S256"));
        }
    }
}
