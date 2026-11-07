using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BrokenPhone
{
    public sealed partial class GameWindow
    {
        int rolePage, roleCapacity = 1;
        int chainPage, chainCapacity = 1, lastChainTurn = -1;
        void InitializePagination()
        {
            B("PreviousRolesButton").Click += delegate { rolePage = Math.Max(0, rolePage - 1); UpdateRoleLayout(); };
            B("NextRolesButton").Click += delegate { rolePage++; UpdateRoleLayout(); };
            C<Grid>("RoleViewport").SizeChanged += delegate { UpdateRoleLayout(); };
            B("PreviousChainButton").Click += delegate { chainPage = Math.Max(0, chainPage - 1); UpdateChainPage(); };
            B("NextChainButton").Click += delegate { chainPage++; UpdateChainPage(); };
            C<Grid>("VoiceChainViewport").SizeChanged += delegate { UpdateChainPage(); };
        }
        void UpdateChainPage()
        {
            var panel = C<StackPanel>("TurnList"); double height = C<Grid>("VoiceChainViewport").ActualHeight; if (height < 1) return;
            chainCapacity = Math.Max(1, (int)(height / 60));
            int pages = Math.Max(1, (int)Math.Ceiling(panel.Children.Count / (double)chainCapacity));
            if (state != null && state.turn != lastChainTurn) { lastChainTurn = state.turn; chainPage = Math.Max(0, StudioTurn) / chainCapacity; }
            chainPage = Math.Max(0, Math.Min(pages - 1, chainPage));
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var row = (FrameworkElement)panel.Children[i]; row.Height = 48; row.Visibility = i / chainCapacity == chainPage ? Visibility.Visible : Visibility.Collapsed;
            }
            C<StackPanel>("ChainPagination").Visibility = pages > 1 ? Visibility.Visible : Visibility.Collapsed;
            Text("ChainPageLabel", (chainPage + 1) + " / " + pages); B("PreviousChainButton").IsEnabled = chainPage > 0; B("NextChainButton").IsEnabled = chainPage + 1 < pages;
        }
    }
}
