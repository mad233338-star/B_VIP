using B_VIP.Core.Rules;
using Xunit;

namespace B_VIP.UnitTests
{
    /// <summary>
    /// The e-mail rule is shared by the desktop client and the API, so it is pinned here once:
    /// ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$
    /// </summary>
    public sealed class EmailValidationTests
    {
        [Theory]
        [InlineData("technician@example.com")]
        [InlineData("first.last+tag@workshop.co.uk")]
        [InlineData("a_b-c%d@sub.domain.io")]
        [InlineData("  spaced@example.com  ")]
        public void Accepted_addresses_match_the_pattern(string email)
        {
            Assert.True(ValidationRules.IsValidEmail(email));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("plainaddress")]
        [InlineData("missing@tld")]
        [InlineData("user@.com")]
        [InlineData("user@domain.")]
        [InlineData("user@@example.com")]
        [InlineData("user name@example.com")]
        [InlineData("user@exa mple.com")]
        [InlineData("user@example.c")]
        [InlineData("user@example.com,other@example.com")]
        [InlineData("<user@example.com>")]
        public void Rejected_strings_never_reach_the_server(string? email)
        {
            Assert.False(ValidationRules.IsValidEmail(email));
        }

        [Fact]
        public void The_length_limit_is_enforced()
        {
            string local = new('a', ValidationRules.MaximumEmailLength - "example.com".Length - 1);
            string email = local + "@example.com";

            Assert.True(email.Length <= ValidationRules.MaximumEmailLength);
            Assert.True(ValidationRules.IsValidEmail(email));
            Assert.False(ValidationRules.IsValidEmail(new string('a', 250) + "@example.com"));
        }
    }
}
