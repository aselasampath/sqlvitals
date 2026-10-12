using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Tests.History;

/// <summary>The choices of how query text is kept (#175), in words, on the command line, and applied.</summary>
public sealed class QueryTextStorageTests
{
    [Theory]
    [InlineData("full", QueryTextStorage.Full)]
    [InlineData("without-literals", QueryTextStorage.WithoutLiterals)]
    [InlineData("WithoutLiterals", QueryTextStorage.WithoutLiterals)]
    [InlineData(" OFF ", QueryTextStorage.Off)]
    public void TheCommandLineWords_AndTheNames_AreRead(string text, QueryTextStorage expected)
    {
        Assert.True(QueryTextStorages.TryParse(text, out var storage));
        Assert.Equal(expected, storage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("hash")]
    [InlineData("2")]
    [InlineData("full,off")]
    [InlineData("without_literals")]
    public void AnythingElse_IsNotAChoice_AndLeavesTheDefault(string? text)
    {
        Assert.False(QueryTextStorages.TryParse(text, out var storage));
        Assert.Equal(QueryTextStorages.Default, storage);
    }

    [Fact]
    public void EachChoice_ReadsBackFromItsOption_AndHasWords()
    {
        foreach (var storage in QueryTextStorages.All)
        {
            Assert.True(QueryTextStorages.TryParse(QueryTextStorages.Option(storage), out var read));
            Assert.Equal(storage, read);
            Assert.False(string.IsNullOrWhiteSpace(QueryTextStorages.Describe(storage)));
        }
        Assert.Equal(QueryTextStorage.WithoutLiterals, QueryTextStorages.All[0]);
        Assert.Equal(QueryTextStorage.WithoutLiterals, QueryTextStorages.Default);
    }

    [Fact]
    public void Apply_KeepsTheTextAsTheChoiceSays()
    {
        IReadOnlyList<QueryTextInfo> texts = [new("0xA", "Sales", "SELECT 1 FROM t WHERE Name = 'Ann'")];

        Assert.Same(texts, QueryTextStorages.Apply(texts, QueryTextStorage.Full));
        Assert.Equal(new QueryTextInfo("0xA", "Sales", "SELECT ? FROM t WHERE Name = ?"),
                     Assert.Single(QueryTextStorages.Apply(texts, QueryTextStorage.WithoutLiterals)));
        Assert.Empty(QueryTextStorages.Apply(texts, QueryTextStorage.Off));
    }
}
