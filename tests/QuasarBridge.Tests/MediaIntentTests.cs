using QuasarBridge.Services.Media;
using Xunit;

namespace QuasarBridge.Tests;

public class MediaIntentTests
{
    [Theory]
    [InlineData("продолжить сериал", MediaAct.ContinueLast, MediaKind.Series, null)]
    [InlineData("продолжи фильм", MediaAct.ContinueLast, MediaKind.Movie, null)]
    [InlineData("включи сериал", MediaAct.AskTitle, MediaKind.Series, null)]
    [InlineData("включи мне сериал", MediaAct.AskTitle, MediaKind.Series, null)]
    [InlineData("запусти фильм", MediaAct.AskTitle, MediaKind.Movie, null)]
    [InlineData("включи кино", MediaAct.AskTitle, MediaKind.Movie, null)]
    [InlineData("включи последний сериал", MediaAct.ContinueLast, MediaKind.Series, null)]
    [InlineData("включи последний фильм", MediaAct.ContinueLast, MediaKind.Movie, null)]
    [InlineData("включи сериал игра престолов", MediaAct.PlayTitle, MediaKind.Series, "игра престолов")]
    [InlineData("включи фильм последний богатырь", MediaAct.PlayTitle, MediaKind.Movie, "последний богатырь")]
    [InlineData("включи сериал рик морти алиса морси", MediaAct.PlayTitle, MediaKind.Series, "рик морти")]
    [InlineData("возобнови сериал", MediaAct.ContinueLast, MediaKind.Series, null)]
    [InlineData("выключи сериал", MediaAct.Close, MediaKind.Series, null)]
    [InlineData("закрой фильм", MediaAct.Close, MediaKind.Movie, null)]
    [InlineData("закрой плеер", MediaAct.Close, MediaKind.Series, null)]
    [InlineData("выруби кино", MediaAct.Close, MediaKind.Movie, null)]
    [InlineData("пауза", MediaAct.Pause, MediaKind.Series, null)]
    [InlineData("поставь на паузу", MediaAct.Pause, MediaKind.Series, null)]
    [InlineData("останови фильм", MediaAct.Pause, MediaKind.Movie, null)]
    [InlineData("включи следующую серию", MediaAct.NextEpisode, MediaKind.Series, null)]
    [InlineData("включи предыдущую серию", MediaAct.PreviousEpisode, MediaKind.Series, null)]
    [InlineData("включи прошлую серию", MediaAct.PreviousEpisode, MediaKind.Series, null)]
    [InlineData("включи следующий сезон", MediaAct.NextSeason, MediaKind.Series, null)]
    public void Parse_known_phrases(string phrase, MediaAct act, MediaKind kind, string? title)
    {
        var intent = MediaIntents.Parse(phrase);
        Assert.NotNull(intent);
        Assert.Equal(act, intent.Act);
        Assert.Equal(kind, intent.Kind);
        Assert.Equal(title, intent.Title);
    }

    [Theory]
    [InlineData("включи музыку")]
    [InlineData("какой сериал вам включить")]
    [InlineData("продолжить")]
    [InlineData("последний")]
    public void Parse_ignores_other_speech(string phrase)
    {
        Assert.Null(MediaIntents.Parse(phrase));
    }

    [Theory]
    [InlineData("перемотай на 5 минут вперед", 300)]
    [InlineData("отмотай на минуту", -60)]
    [InlineData("на 30 секунд назад", -30)]
    [InlineData("мотни на полминуты назад", -30)]
    [InlineData("перемотай на два часа вперед", 7200)]
    public void Parse_seek(string phrase, int seconds)
    {
        var intent = MediaIntents.Parse(phrase);
        Assert.NotNull(intent);
        Assert.Equal(MediaAct.Seek, intent.Act);
        Assert.Equal(seconds, intent.SeekSeconds);
    }

    [Fact]
    public void Answer_last_title_and_cancel()
    {
        Assert.Equal(AnswerKind.Last, MediaIntents.ParseAnswer("последний").Kind);
        Assert.Equal(AnswerKind.Last, MediaIntents.ParseAnswer("последнюю").Kind);
        Assert.Equal(MediaKind.Movie, MediaIntents.ParseAnswer("последний фильм").KindOverride);

        var title = MediaIntents.ParseAnswer("во все тяжкие");
        Assert.Equal(AnswerKind.Title, title.Kind);
        Assert.Equal("во все тяжкие", title.Title);

        var named = MediaIntents.ParseAnswer("последний богатырь");
        Assert.Equal(AnswerKind.Title, named.Kind);
        Assert.Equal("последний богатырь", named.Title);

        Assert.Equal(AnswerKind.Cancel, MediaIntents.ParseAnswer("не надо").Kind);
        Assert.Equal(AnswerKind.Release, MediaIntents.ParseAnswer("какая погода").Kind);
    }

    [Fact]
    public void Asr_joins_prefixed_tokens()
    {
        var tokens = new List<string>();
        AsrText.Collect(Prefixed("включи"), tokens);
        AsrText.Collect(Prefixed("мне", "сериал"), tokens);
        Assert.Equal("включи мне сериал", AsrText.Join(tokens));
    }

    [Fact]
    public void Asr_keeps_the_longer_phrase()
    {
        var tokens = new List<string>();
        AsrText.Collect(Prefixed("вкл", "включи"), tokens);
        AsrText.Collect(Prefixed("включи мне сериал"), tokens);
        Assert.Equal("включи мне сериал", AsrText.Join(tokens));
    }

    [Fact]
    public void CompactSearchTitle_strips_yandex_synopsis_keeps_tail()
    {
        var blob =
            "сверхъестественн впечатляющие спецэффек рассказывает о пр и дина винчестеро путешествуют по черном автомоб " +
            "расследуют паранормаль многие которых основ американских г легендах и фолькл и сражаются порождени такими как демоны рик морти";
        Assert.Equal("рик морти", MediaIntents.CompactSearchTitle(blob));
    }

    [Fact]
    public void CompactSearchTitle_ui_chatter()
    {
        var blob = "результаты поиска видео и в все серии подряд и мультфил рик морти";
        Assert.Equal("рик морти", MediaIntents.CompactSearchTitle(blob));
    }

    [Fact]
    public void Messy_title_tokens_parse_without_waiting_for_the_model()
    {
        var tokens = new List<string>
        {
            "включи сериал", "рик", "морти", "чтобы", "смотреть", "видеоролики",
            "фильмы", "сериалы", "нужно", "подключить", "яндекс", "станцию", "экрану"
        };

        Assert.False(AsrText.NeedsModel(tokens));
        Assert.Equal("включи сериал рик морти", AsrText.JoinContent(tokens));
        Assert.Equal(MediaAct.PlayTitle, MediaIntents.Parse(AsrText.JoinContent(tokens))!.Act);
        Assert.Equal("рик морти", MediaIntents.Parse(AsrText.JoinContent(tokens))!.Title);
        Assert.Equal("включи сериал · рик · морти", AsrText.Show(tokens));
    }

    [Theory]
    [InlineData("включи сериал")]
    [InlineData("выключи сериал")]
    [InlineData("включи сериал рик и морти")]
    public void Clean_phrase_stays_local(string phrase)
    {
        Assert.False(AsrText.NeedsModel(new[] { phrase }));
    }

    [Fact]
    public void Screen_hint_alone_does_not_need_the_model()
    {
        var tokens = new List<string> { "включи сериал", "чтобы", "смотреть", "подключить", "яндекс", "станцию", "экрану" };
        Assert.False(AsrText.NeedsModel(tokens));
        Assert.Equal("включи сериал", AsrText.JoinContent(tokens));
    }

    [Fact]
    public void Model_json_becomes_a_command()
    {
        var titled = FreeLlmCommandDecoder.Read("""{"act":"play_title","kind":"series","title":"рик и морти"}""");
        Assert.NotNull(titled);
        Assert.Equal(MediaAct.PlayTitle, titled.Act);
        Assert.Equal(MediaKind.Series, titled.Kind);
        Assert.Equal("рик и морти", titled.Title);

        var last = FreeLlmCommandDecoder.Read("```json\n{\"act\":\"play_last\",\"kind\":\"movie\",\"title\":null}\n```");
        Assert.Equal(MediaAct.ContinueLast, last!.Act);
        Assert.Equal(MediaKind.Movie, last.Kind);

        var closed = FreeLlmCommandDecoder.Read("""{"act":"close","kind":"series","title":null}""");
        Assert.Equal(MediaAct.Close, closed!.Act);

        Assert.Null(FreeLlmCommandDecoder.Read("""{"act":"none"}"""));
    }

    [Fact]
    public void Cloud_question_is_not_a_command()
    {
        Assert.True(MediaIntents.CloudAsks("Какой сериал вам включить?", MediaKind.Series));
        Assert.False(MediaIntents.CloudAsks("Чтобы смотреть фильмы и сериалы, подключите станцию к экрану.", MediaKind.Series));
        Assert.True(MediaIntents.CloudAsks("Какой фильм вам включить?", MediaKind.Movie));
    }

    private static byte[] Prefixed(params string[] words)
    {
        using var ms = new MemoryStream();
        foreach (var word in words)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(word);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        return ms.ToArray();
    }
}
