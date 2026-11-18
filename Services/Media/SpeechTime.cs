using System.Globalization;

namespace QuasarBridge.Services.Media;

/// <summary>Reads a signed offset from phrases like «на пять минут назад».</summary>
public static class SpeechTime
{
    public static bool TryDelta(string[] words, out int deltaSeconds)
    {
        deltaSeconds = 0;
        if (words.Length == 0)
            return false;

        var rewind = words.Any(word => word.StartsWith("отмот", StringComparison.Ordinal));
        var seek = rewind || words.Any(word =>
            word.StartsWith("перемот", StringComparison.Ordinal)
            || word.StartsWith("промот", StringComparison.Ordinal)
            || word is "мотай" or "мотать" or "мотни" or "мотани");
        var backwardWord = words.Any(word => word.StartsWith("назад", StringComparison.Ordinal) || word.StartsWith("обратн", StringComparison.Ordinal));
        var forwardWord = words.Any(word => word.StartsWith("вперед", StringComparison.Ordinal));
        if (!seek && !backwardWord && !forwardWord)
            return false;
        if (!TryDuration(words, out var seconds))
            return false;

        var backward = backwardWord || (rewind && !forwardWord);
        if (forwardWord && !backwardWord)
            backward = false;
        deltaSeconds = backward ? -seconds : seconds;
        return true;
    }

    private static bool TryDuration(string[] words, out int seconds)
    {
        seconds = 0;
        var total = 0;
        var pending = 0;
        var found = false;
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word is "полминуты" or "полминуту")
            {
                total += 30;
                found = true;
                continue;
            }

            if (word is "полчаса" or "полчасу")
            {
                total += 30 * 60;
                found = true;
                continue;
            }

            if (word is "пол" && UnitScale(words, AfterFillers(words, i + 1)) is int halfScale)
            {
                total += halfScale / 2;
                found = true;
                i = AfterFillers(words, i + 1);
                continue;
            }

            if (word is "полторы" or "полтора" && UnitScale(words, AfterFillers(words, i + 1)) is int moreScale)
            {
                total += moreScale + moreScale / 2;
                found = true;
                i = AfterFillers(words, i + 1);
                continue;
            }

            if (word.Contains('.')
                && double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var fraction)
                && fraction > 0
                && UnitScale(words, AfterFillers(words, i + 1)) is int fractionScale)
            {
                total += (int)Math.Round(fraction * fractionScale);
                found = true;
                i = AfterFillers(words, i + 1);
                continue;
            }

            if (TryReadNumber(words, i, out var value, out var next))
            {
                var unitAt = AfterFillers(words, next);
                if (UnitScale(words, unitAt) is int scale)
                {
                    total += value * scale;
                    found = true;
                    i = unitAt;
                    continue;
                }

                if (found)
                    total += value;
                else
                    pending = value;
                i = next - 1;
                continue;
            }

            if (UnitScale(words, i) is int bare)
            {
                var numberAt = AfterFillers(words, i + 1);
                if (TryReadNumber(words, numberAt, out var after, out var afterNext))
                {
                    total += after * bare;
                    found = true;
                    i = afterNext - 1;
                    continue;
                }

                total += bare;
                found = true;
            }
        }

        if (!found && pending > 0)
        {
            total = pending;
            found = true;
        }

        seconds = total;
        return found && seconds > 0;
    }

    private static bool TryReadNumber(string[] words, int start, out int value, out int next)
    {
        value = 0;
        next = start;
        if ((uint)start >= (uint)words.Length)
            return false;
        if (int.TryParse(words[start], NumberStyles.Integer, CultureInfo.InvariantCulture, out var digit) && digit >= 0)
        {
            value = digit;
            next = start + 1;
            return true;
        }

        var current = 0;
        var total = 0;
        var any = false;
        var i = start;
        for (; i < words.Length; i++)
        {
            var word = words[i];
            if (word is "тысяча" or "тысячи" or "тысяч")
            {
                total += (current == 0 ? 1 : current) * 1000;
                current = 0;
                any = true;
                continue;
            }

            if (!Numbers.TryGetValue(word, out var part))
                break;
            if (any && part < 10 && current % 10 != 0)
                break;
            if (part is >= 20 and < 100 && current % 100 >= 20)
                break;
            current += part;
            any = true;
        }

        if (!any)
            return false;
        value = total + current;
        next = i;
        return true;
    }

    private static int? UnitScale(string[] words, int index)
    {
        if ((uint)index >= (uint)words.Length)
            return null;
        var word = words[index];
        if (word is "сек" or "секунда" or "секунду" or "секунды" or "секунд" or "секундок")
            return 1;
        if (word is "мин" or "минута" or "минуту" or "минуты" or "минут" or "минутку" or "минутки")
            return 60;
        if (word is "час" or "часа" or "часов" or "часу" or "часик")
            return 3600;
        return null;
    }

    private static int AfterFillers(string[] words, int index)
    {
        while (index < words.Length && words[index] is "на" or "и" or "примерно" or "около" or "пожалуйста")
            index++;
        return index;
    }

    private static readonly Dictionary<string, int> Numbers = new(StringComparer.Ordinal)
    {
        ["ноль"] = 0, ["один"] = 1, ["одна"] = 1, ["одно"] = 1, ["одну"] = 1, ["одной"] = 1,
        ["два"] = 2, ["две"] = 2, ["пара"] = 2, ["пару"] = 2, ["парочку"] = 2,
        ["три"] = 3, ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9,
        ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13, ["четырнадцать"] = 14,
        ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60,
        ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["сто"] = 100, ["двести"] = 200, ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500,
        ["шестьсот"] = 600, ["семьсот"] = 700, ["восемьсот"] = 800, ["девятьсот"] = 900
    };
}
