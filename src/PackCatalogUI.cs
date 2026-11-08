using System;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        void RenderPackSource()
        {
            bool library = showMyPacks;
            Text("WorkshopTitle", library ? "Мои паки" : "Мастерская");
            Text("WorkshopSubtitle", library ? "Загруженные паки · твоя коллекция" : "Steam Workshop · Dub Together");
            C<StackPanel>("PackDownloadToolbar").Visibility = library ? Visibility.Collapsed : Visibility.Visible;
            C<ComboBox>("SortCombo").Visibility = library ? Visibility.Collapsed : Visibility.Visible;
            C<ColumnDefinition>("SortColumn").Width = new GridLength(library ? 0 : 220);
            B("PreviousPage").Visibility = B("NextPage").Visibility = library ? Visibility.Collapsed : Visibility.Visible;
            Text("MyPacksLabel", "Мои паки"); Text("WorkshopPacksLabel", "Мастерская");
            C<TextBlock>("MyPacksLabel").Foreground = Brush(library ? "#D7CBFF" : "#ABA1B7");
            C<TextBlock>("WorkshopPacksLabel").Foreground = Brush(library ? "#ABA1B7" : "#D7CBFF");
            C<TextBlock>("MyPacksLabel").FontWeight = library ? FontWeights.Bold : FontWeights.Normal;
            C<TextBlock>("WorkshopPacksLabel").FontWeight = library ? FontWeights.Normal : FontWeights.Bold;
            B("MyPacksFolderButton").ToolTip = library ? "Открыть мастерскую" : "Открыть мои паки";
            TBox("SearchInput").Tag = library ? "Название пака" : "Название пака или ссылка Steam";
            TBox("SearchInput").ToolTip = library ? "Поиск по загруженным пакам" : "Название пака, ссылка Steam или ID";
            Text("PageLabel", library ? "" : "Страница " + workshopPage);
            Text("WorkshopStatus", "");
        }
        static bool LocalLanguage(string value, int language)
        {
            value = (value ?? "").Trim().ToLowerInvariant();
            return language <= 0 || (language == 1 ? value == "ru" || value.StartsWith("rus") || value.StartsWith("рус") : value == "en" || value.StartsWith("eng") || value.StartsWith("анг"));
        }
    }
}
