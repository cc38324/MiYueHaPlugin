using System.Text;
using MiYue.Core.Util;
using Xunit;

namespace MiYue.Core.Tests
{
    public class SimplTextTests
    {
        private static string Packed(string s)
        {
            var sb = new StringBuilder();
            foreach (var b in Encoding.UTF8.GetBytes(s)) sb.Append((char)b);
            return sb.ToString();
        }

        [Fact]
        public void Utf8_bytes_from_an_ascii_program_are_decoded()
        {
            Assert.Equal("开饭了", SimplText.FromSimpl(Packed("开饭了")));
            Assert.Equal("开饭了", SimplText.FromSimpl("开饭了"));      // already Unicode
            Assert.Equal("plain", SimplText.FromSimpl("plain"));
            Assert.Equal("Café", SimplText.FromSimpl("Café"));          // genuine Latin-1 stays
            Assert.Equal("", SimplText.FromSimpl(null));
        }

        [Fact]
        public void To_utf8_bytes_round_trips()
        {
            Assert.Equal(Packed("周杰伦"), SimplText.ToUtf8Bytes("周杰伦"));
            Assert.Equal("周杰伦", SimplText.FromSimpl(SimplText.ToUtf8Bytes("周杰伦")));
        }

        [Fact]
        public void Clip_never_splits_a_surrogate_pair()
        {
            Assert.Equal("abc", SimplText.Clip("abcdef", 3));
            Assert.Equal("ab", SimplText.Clip("ab\U0001F600", 3));
            Assert.Equal("short", SimplText.Clip("short", 250));
        }
    }
}
