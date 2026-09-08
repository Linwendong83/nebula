using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text.RegularExpressions;
using NebulaModel.Utils;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace NebulaTests.NebulaModel;

[TestClass]
public class LocalizationTest
{
    [TestMethod]
    public void ChineseCatalogueIsEmbeddedAndReadable()
    {
        TestAssert.IsTrue(NebulaLocalization.TryTranslate("Multiplayer", true, out var text));
        TestAssert.AreEqual("多人联机", text);
    }

    [TestMethod]
    public void OtherLanguagesAndUnknownKeysFallBackToTheGame()
    {
        TestAssert.IsFalse(NebulaLocalization.TryTranslate("Multiplayer", false, out var text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaLocalization.TryTranslate("Nebula.Unknown.Key", true, out text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaLocalization.TryTranslate(null!, true, out text));
        TestAssert.IsNull(text);
        TestAssert.IsFalse(NebulaLocalization.TryTranslate("开始游戏", true, out text));
    }

    [TestMethod]
    public void TranslationsPreserveFormatArgumentsAndNumericFormats()
    {
        using var stream = typeof(NebulaLocalization).Assembly.GetManifestResourceStream("NebulaModel.Localization.zh-CN.json");
        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        var catalogue = (Dictionary<string, string>)serializer.ReadObject(stream!)!;
        var placeholder = new Regex(@"\{\d+(?:,[^}:]+)?(?::[^}]+)?\}");
        foreach (var pair in catalogue)
        {
            var source = placeholder.Matches(pair.Key).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            var translated = placeholder.Matches(pair.Value).Cast<Match>().Select(match => match.Value).OrderBy(value => value).ToArray();
            CollectionAssert.AreEqual(source, translated, pair.Key);
            if (source.Length == 0) continue;
            // Also exercise the actual .NET formatter, including reordered placeholders.
            var count = source.Select(value => int.Parse(Regex.Match(value, @"\d+").Value)).Max() + 1;
            var arguments = Enumerable.Repeat<object>(12, count).ToArray();
            TestAssert.IsFalse(string.IsNullOrEmpty(string.Format(pair.Value, arguments)), pair.Key);
        }
    }
}
