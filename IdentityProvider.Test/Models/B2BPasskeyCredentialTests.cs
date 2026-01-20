using System.Text.Json;
using IdentityProvider.Models;

namespace IdentityProvider.Test.Models
{
    public class B2BPasskeyCredentialTests
    {
        [Fact]
        public void B2BPasskeyCredential_DefaultValues_ShouldBeSetCorrectly()
        {
            var credential = new B2BPasskeyCredential();

            Assert.Equal(0, credential.Id);
            Assert.Equal(string.Empty, credential.B2BSubject);
            Assert.NotNull(credential.CredentialId);
            Assert.Empty(credential.CredentialId);
            Assert.NotNull(credential.PublicKey);
            Assert.Empty(credential.PublicKey);
            Assert.Equal(0u, credential.SignCount);
            Assert.Null(credential.DeviceName);
            Assert.Equal(Guid.Empty, credential.AaGuid);
            Assert.Null(credential.TransportsJson);
            Assert.NotNull(credential.Transports);
            Assert.Empty(credential.Transports);
            Assert.True(credential.CreatedAt <= DateTimeOffset.UtcNow);
            Assert.Null(credential.LastUsedAt);
        }

        [Fact]
        public void B2BPasskeyCredential_SetProperties_ShouldRetainValues()
        {
            var id = 123;
            var b2bSubject = "test-subject-uuid";
            var credentialId = new byte[] { 0x01, 0x02, 0x03, 0x04 };
            var publicKey = new byte[] { 0x05, 0x06, 0x07, 0x08 };
            var signCount = 5u;
            var deviceName = "MacBook Pro";
            var aaGuid = Guid.NewGuid();
            var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
            var lastUsedAt = DateTimeOffset.UtcNow;

            var credential = new B2BPasskeyCredential
            {
                Id = id,
                B2BSubject = b2bSubject,
                CredentialId = credentialId,
                PublicKey = publicKey,
                SignCount = signCount,
                DeviceName = deviceName,
                AaGuid = aaGuid,
                CreatedAt = createdAt,
                LastUsedAt = lastUsedAt
            };

            Assert.Equal(id, credential.Id);
            Assert.Equal(b2bSubject, credential.B2BSubject);
            Assert.Equal(credentialId, credential.CredentialId);
            Assert.Equal(publicKey, credential.PublicKey);
            Assert.Equal(signCount, credential.SignCount);
            Assert.Equal(deviceName, credential.DeviceName);
            Assert.Equal(aaGuid, credential.AaGuid);
            Assert.Equal(createdAt, credential.CreatedAt);
            Assert.Equal(lastUsedAt, credential.LastUsedAt);
        }

        [Fact]
        public void B2BPasskeyCredential_CredentialId_ShouldAcceptByteArray()
        {
            var credentialId = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF };
            var credential = new B2BPasskeyCredential { CredentialId = credentialId };

            Assert.Equal(credentialId, credential.CredentialId);
            Assert.Equal(6, credential.CredentialId.Length);
        }

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(uint.MaxValue)]
        public void B2BPasskeyCredential_SignCount_ShouldAcceptValidValues(uint signCount)
        {
            var credential = new B2BPasskeyCredential { SignCount = signCount };

            Assert.Equal(signCount, credential.SignCount);
        }

        [Fact]
        public void B2BPasskeyCredential_Transports_JsonSerialization_ShouldWork()
        {
            var transports = new[] { "internal", "usb", "nfc", "ble" };
            var credential = new B2BPasskeyCredential { Transports = transports };

            // Transports を設定すると TransportsJson に JSON が保存される
            Assert.NotNull(credential.TransportsJson);
            var deserializedTransports = JsonSerializer.Deserialize<string[]>(credential.TransportsJson);
            Assert.NotNull(deserializedTransports);
            Assert.Equal(transports, deserializedTransports);

            // Transports プロパティからも読み取り可能
            Assert.Equal(transports, credential.Transports);
        }

        [Fact]
        public void B2BPasskeyCredential_Transports_NullHandling_ShouldWork()
        {
            var credential = new B2BPasskeyCredential { Transports = null };

            Assert.Null(credential.TransportsJson);
            Assert.NotNull(credential.Transports);
            Assert.Empty(credential.Transports);
        }

        [Fact]
        public void B2BPasskeyCredential_Transports_EmptyArray_ShouldWork()
        {
            var credential = new B2BPasskeyCredential { Transports = Array.Empty<string>() };

            Assert.Null(credential.TransportsJson);
            Assert.NotNull(credential.Transports);
            Assert.Empty(credential.Transports);
        }

        [Fact]
        public void B2BPasskeyCredential_B2BUserRelation_ShouldWork()
        {
            var user = new B2BUser
            {
                Id = 1,
                Subject = "test-subject-uuid",
                OrganizationId = 1
            };

            var credential = new B2BPasskeyCredential
            {
                B2BSubject = user.Subject,
                B2BUser = user
            };

            Assert.NotNull(credential.B2BUser);
            Assert.Equal(user.Subject, credential.B2BSubject);
            Assert.Equal(user, credential.B2BUser);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("MacBook Pro")]
        [InlineData("iPhone 15")]
        [InlineData("YubiKey 5")]
        public void B2BPasskeyCredential_DeviceName_ShouldAcceptValidValues(string? deviceName)
        {
            var credential = new B2BPasskeyCredential { DeviceName = deviceName };

            Assert.Equal(deviceName, credential.DeviceName);
        }
    }
}
