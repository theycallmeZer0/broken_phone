using System;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        readonly DispatcherTimer searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        bool showMyPacks, refreshMyPacks;
        void InitializePackSearch()
        {
            TBox("SearchInput").TextChanged += delegate { SchedulePackSearch(); };
            C<ComboBox>("SortCombo").SelectionChanged += delegate { SchedulePackSearch(); };
            C<ComboBox>("LanguageCombo").SelectionChanged += delegate { SchedulePackSearch(); };
            searchTimer.Tick += async delegate { searchTimer.Stop(); if (currentPage == "Workshop") await Guard(Search); };
            B("MyPacksFolderButton").Click += async delegate
            {
                showMyPacks = !showMyPacks; refreshMyPacks = showMyPacks; workshopPage = 1; RenderPackSource(); await Guard(Search);
            };
        }
        void SchedulePackSearch()
        {
            if (currentPage != "Workshop" || disposed) return;
            workshopPage = 1; ++searchGeneration;
            searchTimer.Stop(); searchTimer.Start();
        }
    }
}
