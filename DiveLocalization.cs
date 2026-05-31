using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ServerSyncModTemplate;

internal static class DiveLocalization
{
    internal const string FastSwimOnKey = "$divein_fast_swim_on";
    internal const string FastSwimOffKey = "$divein_fast_swim_off";
    internal const string DescendKey = "$divein_descend";
    internal const string AscendKey = "$divein_ascend";

    private const string FastSwimOnWord = "divein_fast_swim_on";
    private const string FastSwimOffWord = "divein_fast_swim_off";
    private const string DescendWord = "divein_descend";
    private const string AscendWord = "divein_ascend";
    private const string EnglishLanguage = "english";

    private static readonly DiveHintTranslation English = new("Fast Swim On", "Fast Swim Off", "Descend", "Ascend");

    private static readonly Dictionary<string, DiveHintTranslation> Translations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = English,
        ["swedish"] = new("Snabbsim p\u00e5", "Snabbsim av", "Ner", "Upp"),
        ["french"] = new("Nage rapide ON", "Nage rapide OFF", "Descendre", "Monter"),
        ["italian"] = new("Nuoto veloce ON", "Nuoto veloce OFF", "Scendi", "Sali"),
        ["german"] = new("Schnellschwimmen an", "Schnellschwimmen aus", "Abtauchen", "Auftauchen"),
        ["spanish"] = new("Nado r\u00e1pido ON", "Nado r\u00e1pido OFF", "Bajar", "Subir"),
        ["russian"] = new("\u0411\u044b\u0441\u0442\u0440\u043e\u0435 \u043f\u043b\u0430\u0432\u0430\u043d\u0438\u0435 \u0432\u043a\u043b.", "\u0411\u044b\u0441\u0442\u0440\u043e\u0435 \u043f\u043b\u0430\u0432\u0430\u043d\u0438\u0435 \u0432\u044b\u043a\u043b.", "\u0412\u043d\u0438\u0437", "\u0412\u0432\u0435\u0440\u0445"),
        ["finnish"] = new("Nopea uinti p\u00e4\u00e4ll\u00e4", "Nopea uinti pois", "Alas", "Yl\u00f6s"),
        ["danish"] = new("Hurtig sv\u00f8mning til", "Hurtig sv\u00f8mning fra", "Ned", "Op"),
        ["norwegian"] = new("Rask sv\u00f8mming p\u00e5", "Rask sv\u00f8mming av", "Ned", "Opp"),
        ["turkish"] = new("H\u0131zl\u0131 y\u00fczme a\u00e7\u0131k", "H\u0131zl\u0131 y\u00fczme kapal\u0131", "Al\u00e7al", "Y\u00fcksel"),
        ["lithuanian"] = new("Greitas plaukimas \u012fj.", "Greitas plaukimas i\u0161j.", "\u017demyn", "Auk\u0161tyn"),
        ["czech"] = new("Rychl\u00e9 plav\u00e1n\u00ed zap.", "Rychl\u00e9 plav\u00e1n\u00ed vyp.", "Dol\u016f", "Nahoru"),
        ["hungarian"] = new("Gyors \u00fasz\u00e1s be", "Gyors \u00fasz\u00e1s ki", "Le", "Fel"),
        ["slovak"] = new("R\u00fdchle pl\u00e1vanie zap.", "R\u00fdchle pl\u00e1vanie vyp.", "Dole", "Hore"),
        ["polish"] = new("Szybkie p\u0142ywanie w\u0142.", "Szybkie p\u0142ywanie wy\u0142.", "W d\u00f3\u0142", "W g\u00f3r\u0119"),
        ["dutch"] = new("Snel zwemmen aan", "Snel zwemmen uit", "Omlaag", "Omhoog"),
        ["portuguese_european"] = new("Nado r\u00e1pido ligado", "Nado r\u00e1pido desligado", "Descer", "Subir"),
        ["portuguese_brazilian"] = new("Nado r\u00e1pido ligado", "Nado r\u00e1pido desligado", "Descer", "Subir"),
        ["chinese"] = new("\u5feb\u901f\u6e38\u6cf3\u5f00", "\u5feb\u901f\u6e38\u6cf3\u5173", "\u4e0b\u6f5c", "\u4e0a\u6d6e"),
        ["chinese_trad"] = new("\u5feb\u901f\u6e38\u6cf3\u958b", "\u5feb\u901f\u6e38\u6cf3\u95dc", "\u4e0b\u6f5b", "\u4e0a\u6d6e"),
        ["japanese"] = new("\u9ad8\u901f\u6cf3\u304e ON", "\u9ad8\u901f\u6cf3\u304e OFF", "\u6f5c\u308b", "\u6d6e\u4e0a"),
        ["korean"] = new("\ube60\ub978 \uc218\uc601 \ucf2c", "\ube60\ub978 \uc218\uc601 \ub054", "\ud558\uac15", "\uc0c1\uc2b9"),
        ["thai"] = new("\u0e27\u0e48\u0e32\u0e22\u0e40\u0e23\u0e47\u0e27 \u0e40\u0e1b\u0e34\u0e14", "\u0e27\u0e48\u0e32\u0e22\u0e40\u0e23\u0e47\u0e27 \u0e1b\u0e34\u0e14", "\u0e25\u0e07", "\u0e02\u0e36\u0e49\u0e19"),
        ["greek"] = new("\u0393\u03c1\u03ae\u03b3\u03bf\u03c1\u03b7 \u03ba\u03bf\u03bb\u03cd\u03bc\u03b2\u03b7\u03c3\u03b7 ON", "\u0393\u03c1\u03ae\u03b3\u03bf\u03c1\u03b7 \u03ba\u03bf\u03bb\u03cd\u03bc\u03b2\u03b7\u03c3\u03b7 OFF", "\u039a\u03ac\u03c4\u03c9", "\u03a0\u03ac\u03bd\u03c9"),
        ["ukrainian"] = new("\u0428\u0432\u0438\u0434\u043a\u0435 \u043f\u043b\u0430\u0432\u0430\u043d\u043d\u044f \u0443\u0432\u0456\u043c\u043a.", "\u0428\u0432\u0438\u0434\u043a\u0435 \u043f\u043b\u0430\u0432\u0430\u043d\u043d\u044f \u0432\u0438\u043c\u043a.", "\u0412\u043d\u0438\u0437", "\u0412\u0433\u043e\u0440\u0443"),
        ["latvian"] = new("\u0100tra peld\u0113\u0161ana iesl.", "\u0100tra peld\u0113\u0161ana izsl.", "Lejup", "Aug\u0161up"),
    };

    private readonly struct DiveHintTranslation
    {
        internal DiveHintTranslation(string fastSwimOn, string fastSwimOff, string descend, string ascend)
        {
            FastSwimOn = fastSwimOn;
            FastSwimOff = fastSwimOff;
            Descend = descend;
            Ascend = ascend;
        }

        internal string FastSwimOn { get; }
        internal string FastSwimOff { get; }
        internal string Descend { get; }
        internal string Ascend { get; }
    }

    internal static void Register()
    {
        if (Localization.instance != null)
        {
            Register(Localization.instance);
        }
    }

    internal static void Register(Localization localization)
    {
        if (localization == null)
        {
            return;
        }

        DiveHintTranslation translation = GetTranslation(NormalizeLanguageName(localization.GetSelectedLanguage()));
        localization.AddWord(FastSwimOnWord, translation.FastSwimOn);
        localization.AddWord(FastSwimOffWord, translation.FastSwimOff);
        localization.AddWord(DescendWord, translation.Descend);
        localization.AddWord(AscendWord, translation.Ascend);
    }

    internal static string Localize(string key)
    {
        if (Localization.instance == null)
        {
            return GetEnglishText(key);
        }

        string localized = Localization.instance.Localize(key);
        return localized.Contains("$") ? GetEnglishText(key) : localized;
    }

    private static DiveHintTranslation GetTranslation(string languageName)
    {
        return Translations.TryGetValue(languageName, out DiveHintTranslation translation) ? translation : English;
    }

    private static string NormalizeLanguageName(string languageName)
    {
        return string.IsNullOrWhiteSpace(languageName)
            ? EnglishLanguage
            : languageName.Trim().Replace(" ", string.Empty).ToLowerInvariant();
    }

    private static string GetEnglishText(string key)
    {
        return key switch
        {
            FastSwimOnKey => English.FastSwimOn,
            FastSwimOffKey => English.FastSwimOff,
            DescendKey => English.Descend,
            AscendKey => English.Ascend,
            _ => key
        };
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class LocalizationSetupLanguageDivePatch
{
    private static void Postfix(Localization __instance)
    {
        DiveLocalization.Register(__instance);
    }
}
