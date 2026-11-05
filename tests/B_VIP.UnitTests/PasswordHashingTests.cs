using B_VIP.Core.Security;
using Xunit;

namespace B_VIP.UnitTests
{
    public sealed class PasswordHashingTests
    {
        /// <summary>The master administrator credential the server seeds at startup.</summary>
        public const string MasterAdminPassword = "Bashir0935784653AdminB";

        [Fact]
        public void The_bcrypt_hash_carries_the_expected_prefix()
        {
            string stored = PasswordHasher.HashBcrypt(MasterAdminPassword);

            Assert.True(PasswordHasher.IsBcrypt(stored));
            Assert.StartsWith("$2", stored);
            Assert.NotEqual(MasterAdminPassword, stored);
        }

        [Fact]
        public void The_bcrypt_hash_verifies_the_master_password_only()
        {
            string stored = PasswordHasher.HashBcrypt(MasterAdminPassword);

            Assert.True(PasswordHasher.Verify(MasterAdminPassword, stored));
            Assert.False(PasswordHasher.Verify(MasterAdminPassword + "x", stored));
            Assert.False(PasswordHasher.Verify(string.Empty, stored));
        }

        [Fact]
        public void The_same_password_hashes_differently_each_time()
        {
            string first = PasswordHasher.HashBcrypt(MasterAdminPassword);
            string second = PasswordHasher.HashBcrypt(MasterAdminPassword);

            Assert.NotEqual(first, second);
            Assert.True(PasswordHasher.Verify(MasterAdminPassword, first));
            Assert.True(PasswordHasher.Verify(MasterAdminPassword, second));
        }

        [Fact]
        public void The_pbkdf2_format_still_verifies_after_the_bcrypt_support()
        {
            string stored = PasswordHasher.Hash(MasterAdminPassword);

            Assert.False(PasswordHasher.IsBcrypt(stored));
            Assert.True(PasswordHasher.Verify(MasterAdminPassword, stored));
            Assert.False(PasswordHasher.Verify("wrong-password", stored));
        }

        [Fact]
        public void A_corrupt_bcrypt_hash_is_rejected_instead_of_throwing()
        {
            Assert.False(PasswordHasher.Verify("anything", "$2b$12$not-a-real-hash"));
        }

        [Fact]
        public void An_unknown_format_is_never_accepted()
        {
            Assert.False(PasswordHasher.Verify("anything", "plain-text-password"));
            Assert.False(PasswordHasher.Verify("anything", string.Empty));
        }
    }
}
