namespace Tactile.Core;

public enum Language { Tr, En }

public static class Text
{
    private static volatile Language s_current = Language.En;

    public static Language Current
    {
        get => s_current;
        set => s_current = value;
    }

    public static string Pick(string en, string tr) => Current == Language.Tr ? tr : en;

    public static string English(Exception error) =>
        error is LocalizedException localized
            ? localized.Translation.En
            : error.Data[typeof(LocalizedMessage)] is LocalizedMessage message ? message.En : error.Message;
}

public sealed record LocalizedMessage(string En, string Tr)
{
    public string Value => Text.Pick(En, Tr);

    public T CreateException<T>(Func<string, T> create) where T : Exception
    {
        T exception = create(Value);
        exception.Data[typeof(LocalizedMessage)] = this;
        return exception;
    }
}

public abstract class LocalizedException(LocalizedMessage translation, Exception? inner)
    : Exception(translation.En, inner)
{
    public LocalizedMessage Translation { get; } = translation;
    public override string Message => Translation.Value;
}
