using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using MiYue.Core.Engine;
using Xunit;

namespace MiYue.Core.Tests
{
    /// <summary>
    /// The SIMPL+ modules cannot be compiled here, so their contract with the C# side is checked
    /// textually: #DEFINE_CONSTANT signal numbers vs PlayerSignals / BrowserSignals, and every method /
    /// delegate property the .usp touches vs the SIMPL+ facade classes in MiYue.Crestron.
    /// </summary>
    public class SignalContractTests
    {
        private static string SimplPlusDir => Path.Combine(AppContext.BaseDirectory, "SimplPlus");

        private static string Usp(string name) => File.ReadAllText(Path.Combine(SimplPlusDir, name), Encoding.UTF8);

        private static Dictionary<string, int> Defines(string usp)
        {
            var map = new Dictionary<string, int>();
            foreach (Match m in Regex.Matches(usp, @"^#DEFINE_CONSTANT\s+(\w+)\s+(\d+)\s*$", RegexOptions.Multiline))
                map[m.Groups[1].Value] = int.Parse(m.Groups[2].Value);
            return map;
        }

        private static string Snake(string pascal) => Regex.Replace(pascal, "(?<=[a-z0-9])([A-Z])", "_$1").ToUpperInvariant();

        private static Dictionary<string, int> CsConstants(Type t) =>
            t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.IsLiteral && (f.FieldType == typeof(ushort) || f.FieldType == typeof(int)))
                .ToDictionary(f => Snake(f.Name), f => Convert.ToInt32(f.GetRawConstantValue()));

        [Theory]
        [InlineData("MiYue Player v1.0.usp", typeof(PlayerSignals))]
        [InlineData("MiYue Browser v1.0.usp", typeof(BrowserSignals))]
        public void Usp_constants_match_csharp_signal_indices(string file, Type signals)
        {
            var usp = Defines(Usp(file));
            var cs = CsConstants(signals);
            if (cs.ContainsKey("MAX_PAGE_SIZE"))
            {
                Assert.Equal(cs["MAX_PAGE_SIZE"], usp["MAX_ROWS"]);
                cs.Remove("MAX_PAGE_SIZE");
            }
            foreach (var kv in cs)
            {
                Assert.True(usp.ContainsKey(kv.Key), file + " lacks #DEFINE_CONSTANT " + kv.Key);
                Assert.True(kv.Value == usp[kv.Key], file + ": " + kv.Key + " = " + usp[kv.Key] + ", C# says " + kv.Value);
            }
            foreach (var name in usp.Keys.Where(k => Regex.IsMatch(k, "^(DI|AI|SI|DO|AO|SO)_")))
                Assert.True(cs.ContainsKey(name), file + " defines " + name + " which C# does not know");
        }

        [Theory]
        [InlineData("MiYue Player v1.0.usp", "player", "MiYuePlayer")]
        [InlineData("MiYue Browser v1.0.usp", "browser", "MiYueBrowser")]
        public void Usp_calls_only_members_the_facade_declares(string file, string variable, string className)
        {
            string text = Usp(file);
            string facade = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SimplPlus", "SimplModules.cs"));
            Assert.Contains("#USER_SIMPLSHARP_LIBRARY \"MiYue.Crestron\"", text);
            Assert.Matches(new Regex("^" + className + @"\s+" + variable + ";", RegexOptions.Multiline), text);

            int start = facade.IndexOf("public class " + className, StringComparison.Ordinal);
            Assert.True(start >= 0, className + " not found");
            int next = facade.IndexOf("public class ", start + 10, StringComparison.Ordinal);
            string body = facade.Substring(start, (next < 0 ? facade.Length : next) - start);
            var methods = new HashSet<string>(Regex.Matches(body, @"public void (\w+)\(").Select(m => m.Groups[1].Value));

            foreach (Match m in Regex.Matches(text, variable + @"\.(\w+)\("))
                Assert.True(methods.Contains(m.Groups[1].Value), file + " calls " + variable + "." + m.Groups[1].Value + " which " + className + " lacks");
            foreach (Match m in Regex.Matches(text, @"RegisterDelegate\(\s*" + variable + @"\s*,\s*(\w+)\s*,"))
                Assert.Matches(new Regex(@"public \w+ " + m.Groups[1].Value + @" \{ get; set; \}"), facade);
        }

        [Theory]
        [InlineData("MiYue Player v1.0.usp")]
        [InlineData("MiYue Browser v1.0.usp")]
        public void Usp_files_are_ascii(string file)
        {
            var bytes = File.ReadAllBytes(Path.Combine(SimplPlusDir, file));
            Assert.DoesNotContain(bytes, b => b > 0x7F);
        }
    }
}
