using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;

namespace SdrCapture;

// Bindings update open windows without losing edits or applying configuration.
sealed class UiStrings:INotifyPropertyChanged
{
    public static readonly UiStrings Shared=new();
    static readonly Dictionary<string,string> russian=Load();
    string language=CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="ru"?"ru":"en";
    public string Language{get=>language;set{string next=value=="ru"?"ru":"en";if(next==language)return;language=next;PropertyChanged?.Invoke(this,new(nameof(Language)));}}
    public event PropertyChangedEventHandler? PropertyChanged;
    static Dictionary<string,string> Load(){using var stream=typeof(UiStrings).Assembly.GetManifestResourceStream("SdrCapture.Strings.ru")!;return JsonSerializer.Deserialize<Dictionary<string,string>>(stream)!;}
    public static string T(string text)
    {
        if(Shared.Language!="ru")return russian.FirstOrDefault(p=>p.Value==text).Key??text;
        if(russian.TryGetValue(text,out var value))return value;
        foreach(string prefix in new[]{"Input", "Output"})if(text.StartsWith(prefix+": ",StringComparison.Ordinal))return T(prefix)+text[prefix.Length..];
        return text;
    }
    public static string F(string text,params object[] args)=>string.Format(CultureInfo.CurrentCulture,T(text),args);
    internal static bool HasTranslation(string text)=>russian.ContainsKey(text);
}
sealed class UiTextConverter:IValueConverter
{
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>UiStrings.T((string)parameter);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
sealed class UiChoiceConverter:IMultiValueConverter
{
    public object Convert(object[] values,Type targetType,object parameter,CultureInfo culture)=>values[0] is string text?UiStrings.T(text):"";
    public object[] ConvertBack(object value,Type[] targetTypes,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}
