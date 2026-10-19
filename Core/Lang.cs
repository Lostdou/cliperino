using System.Globalization;

namespace Cliperino.Core;

public static class Lang
{
    public static bool English { get; private set; } = IsSystemEnglish();

    public static void Use(Language language) => English = language switch
    {
        Language.English => true,
        Language.Spanish => false,
        _ => IsSystemEnglish(),
    };

    public static string T(string spanish, string english) => English ? english : spanish;

    static bool IsSystemEnglish() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "es";
}
