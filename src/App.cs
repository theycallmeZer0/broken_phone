using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BrokenPhone
{
    public static class Program
    {
        public static bool EnableSteam;
        [STAThread]
        public static int Main(string[] args)
        {
            try { return Launch(args); }
            catch (Exception error) { File.WriteAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "startup-error.txt"), error.ToString()); return 1; }
        }
        static int Launch(string[] args)
        {
            Paths.Prepare(); EnableSteam = args.Length == 0 || args.Contains("+connect_lobby") || args.Contains("--steam-ui-test");
            if (args.Contains("--transfer-child")) { Thread.Sleep(30000); return 0; }
            if (args.Contains("--transfer-worker") || args.Contains("--transfer-worker-exits"))
            {
                var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--transfer-child") { UseShellExecute = false, CreateNoWindow = true });
                File.WriteAllText(System.IO.Path.Combine(Paths.Cache, "cancel-child.pid"), child.Id.ToString()); if (!args.Contains("--transfer-worker-exits")) Thread.Sleep(30000); return 0;
            }
            if (args.Contains("--steam-test")) return SelfTests.SteamIntegration();
            if (args.Contains("--design-test")) return DesignTests.Run();
            if (args.Contains("--download-test"))
            {
                string log = System.IO.Path.Combine(Paths.Base, "download-test.txt"); File.WriteAllText(log, "");
                try
                {
                    string id = args[Array.IndexOf(args, "--download-test") + 1]; var service = new WorkshopService();
                    var pack = service.Download(new WorkshopItem { Id = id }, status => File.AppendAllText(log, status + Environment.NewLine)).GetAwaiter().GetResult();
                    File.AppendAllText(log, "PASS: direct Steam download -> " + pack.root + " / " + pack.DisplayLines + " lines / " + pack.DisplayRoles + " roles"); return 0;
                }
                catch (Exception e) { File.AppendAllText(log, "FAIL: " + e.GetBaseException().Message); return 1; }
            }
            if (args.Contains("--self-test")) return SelfTests.Run();
            if (args.Contains("--pack-sync-test")) return SelfTests.LocalPackReuse();
            if (args.Contains("--role-lines-test")) return SelfTests.RoleLinesRegression();
            if (args.Contains("--role-mix-test")) return SelfTests.RoleMixRegression();
            if (args.Contains("--roles-network-test")) return SelfTests.RolesNetworkRegression();
            if (args.Contains("--pack-test")) return SelfTests.NativePack(args[Array.IndexOf(args, "--pack-test") + 1]);
            if (args.Contains("--workshop-test"))
            {
                try
                {
                    var service = new WorkshopService(); var items = service.Browse("", 1, 0, 1);
                    if (items.Count == 0) throw new Exception("В живом каталоге нет распознанных паков.");
                    var details = service.Details(items[0].Id);
                    File.WriteAllText(System.IO.Path.Combine(Paths.Base, "workshop-test.txt"), "PASS: live Steam catalog: " + items.Count + " packs\nPASS: official item metadata: " + details.Id + " " + details.Title);
                    return 0;
                }
                catch (Exception error) { File.WriteAllText(System.IO.Path.Combine(Paths.Base, "workshop-test.txt"), "FAIL: " + error); return 1; }
            }
            var app = new Application();
            app.DispatcherUnhandledException += delegate(object sender, DispatcherUnhandledExceptionEventArgs e)
            {
                File.AppendAllText(System.IO.Path.Combine(Paths.Data, "error.log"), DateTime.Now + " " + e.Exception + Environment.NewLine);
                if (args.Contains("--screening-test") || args.Contains("--steam-ui-test") || args.Contains("--end-test") || args.Contains("--ui-smoke") || args.Contains("--microphone-ui-test") || args.Contains("--folder-packs-ui-test") || args.Contains("--roles-ui-test") || args.Contains("--presentation-ui-test")) { File.WriteAllText(System.IO.Path.Combine(Paths.Base, "ui-error.txt"), e.Exception.ToString()); e.Handled = true; app.Shutdown(1); return; }
                MessageBox.Show(e.Exception.Message, "Broken Phone"); e.Handled = true;
            };
            Window window;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            var controller = new GameWindow(window);
            window.Loaded += async delegate
            {
                await controller.Start();
                if (args.Contains("--folder-packs-ui-test")) await controller.FolderPacksSmoke();
                if (args.Contains("--roles-ui-test")) await controller.RolesSmoke();
                if (args.Contains("--presentation-ui-test")) await controller.PresentationSmoke();
                if (args.Contains("+connect_lobby")) { ulong lobby; int index = Array.IndexOf(args, "+connect_lobby"); if (index + 1 < args.Length && UInt64.TryParse(args[index + 1], out lobby)) await controller.JoinSteam(lobby); }
                if (args.Contains("--pack-downloads-ui-test")) await controller.PackDownloadsSmoke();
                if (args.Contains("--input-ui-test")) await controller.InputSmoke();
                if (args.Contains("--microphone-ui-test")) await controller.MicrophonePanelSmoke();
                if (args.Contains("--ui-smoke")) await controller.Smoke(args.Contains("--ui-pack") ? args[Array.IndexOf(args, "--ui-pack") + 1] : null);
                if (args.Contains("--screening-test")) await controller.ScreeningSmoke();
                if (args.Contains("--steam-ui-test")) await controller.ScreeningSmoke(false, true);
                if (args.Contains("--end-test")) await controller.ScreeningSmoke(true);
                if (args.Contains("--workshop-screen")) await controller.WorkshopScreen();
                if (args.Contains("--settings-screen")) { controller.SettingsScreen(); window.Close(); }
                if (args.Contains("--lobby-pack")) await controller.PackScreen(args[Array.IndexOf(args, "--lobby-pack") + 1]);
                if (args.Contains("--role-pack")) await controller.PackScreen(args[Array.IndexOf(args, "--role-pack") + 1], true);
            };
            window.Closed += delegate { controller.Dispose(); };
            if (args.Contains("--screenshot") || args.Contains("--presentation-ui-test") || args.Contains("--roles-ui-test") || args.Contains("--role-pack") || args.Contains("--microphone-ui-test") || args.Contains("--input-ui-test") || args.Contains("--folder-packs-ui-test") || args.Contains("--pack-downloads-ui-test") || args.Contains("--ui-smoke") || args.Contains("--screening-test") || args.Contains("--steam-ui-test") || args.Contains("--end-test") || args.Contains("--workshop-screen") || args.Contains("--settings-screen") || args.Contains("--lobby-pack"))
            {
                window.ShowInTaskbar = false; window.WindowStartupLocation = WindowStartupLocation.Manual; window.Left = -10000;
            }
            if (args.Contains("--screenshot"))
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += delegate
                {
                    timer.Stop(); var root = (FrameworkElement)window.FindName("Root"); root.UpdateLayout();
                    var image = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); image.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                    using (var output = File.Create(System.IO.Path.Combine(Paths.Base, "lobby-preview.png"))) encoder.Save(output);
                    window.Close();
                }; timer.Start();
            }
            app.Run(window); return 0;
        }
    }

    public sealed class Settings
    {
        public string Name = Environment.UserName;
        public int Microphone;
        public double Volume = .8;
        public string SteamAccount = "anonymous";
        public bool Streamer;
        public int VoiceEffect;
        public bool NoiseGate;
        public bool LocalPacks = true;
        public string PacksFolder = "";
        public double ResultVoiceVolume = 1, ResultBackgroundVolume = 1, ResultOriginalVolume;
    }

    public sealed partial class GameWindow : IDisposable
    {
        readonly Window window;
        readonly WorkshopService workshop = new WorkshopService();
        readonly MicrophoneRecorder microphone = new MicrophoneRecorder();
        readonly MediaPlayer voice = new MediaPlayer();
        readonly DispatcherTimer tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        readonly DispatcherTimer toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        Settings settings = new Settings();
        RoomServer server;
        RoomClient client;
        RoomState state;
        List<Pack> installed = new List<Pack>();
        string currentPage = "Lobby", turnKey = "", sourceFile, finalFile, previousFile, localTakeFile;
        bool recordPending;
        CancellationTokenSource downloadCancellation;
        string remoteInvitation;
        byte[] take;
        byte[] rawTake, voicePcm;
        float[] traceWave = new float[0];
        CancellationTokenSource effectCancellation;
        bool processingTake, sourcePrepared, finalMediaPrepared, voicePlaying, draggingPlayback, updatingPlaybackSlider;
        string videoMode = "source", videoRound;
        int selectedEffect, takeRevision, voiceRevision, playbackAppliedVersion, endedPlaybackVersion;
        long lastPlaybackCheck;
        double traceDuration;
        bool fullScreen;
        bool preparingFinal;
        string finalLoadingRound;
        Task<string> videoDownload;
        RoomClient videoDownloadClient;
        WindowState savedWindowState; WindowStyle savedWindowStyle;
        int workshopPage = 1, searchGeneration;
        DateTime recordStart, playbackEnd;
        Action playbackFinished;
        double recordDuration;
        string expectedRound;
        int expectedTurn;
        bool disposed;
        string posterKey;
        public GameWindow(Window value, bool enableSteam = true)
        {
            window = value;
            Theme.ApplyWindow(window);
            var logo = new BitmapImage(new Uri(System.IO.Path.Combine(Paths.Base, "Assets", "Brand", "logo.png"))); logo.Freeze();
            window.Icon = logo; C<Image>("BrandLogo").Source = logo; C<Image>("PosterLogo").Source = logo; C<Image>("WaitingLogo").Source = logo;
            if (File.Exists(SettingsFile)) try { settings = Json.Read<Settings>(File.ReadAllText(SettingsFile)); } catch { }
            selectedEffect = Math.Max(0, Math.Min(5, settings.VoiceEffect));
            for (int i = 0; i < VoiceEffects.Names.Length; i++)
            {
                int effect = i; var button = SmallButton(VoiceEffects.Names[i], async delegate { selectedEffect = effect; settings.VoiceEffect = effect; Persist(); UpdateEffects(); if (rawTake != null) await Guard(ApplyEffect); });
                button.Margin = new Thickness(0, 0, 7, 0); button.Padding = new Thickness(11, 6, 11, 6); button.FontWeight = FontWeights.Bold; C<StackPanel>("VoiceEffectButtons").Children.Add(button);
            }
            C<CheckBox>("NoiseGateCheck").IsChecked = settings.NoiseGate;
            RoutedEventHandler noiseChanged = async delegate { settings.NoiseGate = C<CheckBox>("NoiseGateCheck").IsChecked == true; Persist(); if (rawTake != null) await Guard(ApplyEffect); };
            C<CheckBox>("NoiseGateCheck").Checked += noiseChanged; C<CheckBox>("NoiseGateCheck").Unchecked += noiseChanged; UpdateEffects();
            workshop.Account = settings.SteamAccount; InitializeSteam(enableSteam);
            B("LobbyTab").Click += delegate { LobbyNavigation(); };
            B("WorkshopTab").Click += delegate { OpenWorkshop(); };
            B("SettingsTab").Click += delegate { OpenSettings(); };
            B("HelpButton").Click += delegate { MessageBox.Show(window, Connection.Help, "Подключение к лобби"); RefreshConnection(); };
            B("ConnectionHelpButton").Click += delegate { B("HelpButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
            C<ComboBox>("NetworkCombo").SelectionChanged += delegate { RenderConnection(); };
            B("CancelDownloadButton").Click += delegate { CancelDownload(); };
            B("ReadyButton").Click += delegate { if (Me != null) Send(new Message { kind = "ready", flag = !Me.ready }); };
            B("StartButton").Click += delegate { Send(new Message { kind = "start" }); };
            B("CancelPreparationButton").Click += delegate { if (IsHost && state.phase == "loading") Send(new Message { kind = "reset" }); };
            B("InviteButton").Click += delegate { InviteFriends(); };
            B("CopyCodeButton").Click += delegate { Invite(); };
            B("JoinButton").Click += async delegate { await Guard(Join); };
            B("ChoosePackButton").Click += delegate { OpenWorkshop(); };
            B("SendChatButton").Click += delegate { SendChat(); };
            TBox("ChatInput").KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SendChat(); };
            B("SaveSettingsButton").Click += async delegate { await Guard(SaveSettings); };
            InitializeMicrophonePanel(); InitializeWorkshopDownloads(); InitializeRoles(); InitializeFinalPlayer();
            B("SearchButton").Click += async delegate { workshopPage = 1; await Guard(Search); };
            TBox("SearchInput").KeyDown += async delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { workshopPage = 1; await Guard(Search); } };
            B("NextPage").Click += async delegate { workshopPage++; await Guard(Search); };
            B("PreviousPage").Click += async delegate { workshopPage = Math.Max(1, workshopPage - 1); await Guard(Search); };
            B("ImportButton").Click += async delegate { await Guard(Import); };
            B("MyPacksFolderButton").Click += delegate { OpenPacksFolder(); };
            B("SteamLoginButton").Click += async delegate { if ((downloadCancellation != null || packDownloads.Busy)) { Toast("Сначала дождись загрузок или отмени их."); return; } await Guard(async delegate { settings.SteamAccount = TBox("SteamAccountInput").Text.Trim(); workshop.Account = settings.SteamAccount; Persist(); await workshop.Login(status => window.Dispatcher.BeginInvoke(new Action(() => Text("SteamLoginStatus", status)))); }); };
            C<WrapPanel>("PackCards").SizeChanged += delegate { UpdateWorkshopLayout(); };
            B("ListenButton").Click += async delegate { await Guard(Listen); };
            B("RecordButton").Click += delegate { Record(); };
            B("ReviewButton").Click += delegate { PreviewTake(); };
            B("OriginalButton").Click += async delegate { await Guard(Original); };
            B("PauseButton").Click += delegate { ControlPlayback(state != null && state.playing ? "pause" : "resume", 0); };
            B("FullscreenButton").Click += delegate { FullScreen(); };
            window.PreviewKeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase || Keyboard.FocusedElement is ComboBox) return;
                if (e.Key == Key.F11) { FullScreen(); e.Handled = true; }
                else if (e.Key == Key.Escape && fullScreen) { FullScreen(); e.Handled = true; }
                else if (currentPage == "Round" && e.Key == Key.R && IsTurn) { Record(); e.Handled = true; }
                else if (currentPage == "Round" && e.Key == Key.Enter && B("SubmitButton").IsEnabled) { B("SubmitButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); e.Handled = true; }
                else if (currentPage == "Round" && e.Key == Key.Space) { if (state != null && state.phase == "result") ControlPlayback(state.playing ? "pause" : "resume", 0); else PreviewTake(); e.Handled = true; }
            };
            var slider = C<Slider>("PlaybackSlider");
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent, new System.Windows.Controls.Primitives.DragStartedEventHandler(delegate { draggingPlayback = true; }));
            slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent, new System.Windows.Controls.Primitives.DragCompletedEventHandler(delegate { draggingPlayback = false; ControlPlayback("seek", slider.Value); }));
            slider.ValueChanged += delegate { if (!updatingPlaybackSlider && !draggingPlayback && IsHost && state.phase == "result" && state.playbackVersion > 0) ControlPlayback("seek", slider.Value); };
            B("SubmitButton").Click += async delegate { await Guard(Submit); };
            C<Slider>("VolumeSlider").ValueChanged += delegate { voice.Volume = C<Slider>("VolumeSlider").Value; checkPlayback.Volume = C<Slider>("VolumeSlider").Value; C<MediaElement>("Video").Volume = videoMode == "result" ? 0 : C<Slider>("VolumeSlider").Value; UpdateFinalVolumes(); };
            C<ComboBox>("SortCombo").SelectedIndex = 0; C<ComboBox>("LanguageCombo").SelectedIndex = 1;
            InitializeFolderPacks();
            var surface = C<Grid>("VideoSurface"); surface.SizeChanged += delegate { surface.Clip = new RectangleGeometry(new Rect(0, 0, surface.ActualWidth, surface.ActualHeight), 18, 18); };
            var video = C<MediaElement>("Video"); video.Volume = settings.Volume; C<Slider>("VolumeSlider").Value = settings.Volume;
            var preview = C<Image>("PackPreview"); preview.SizeChanged += delegate { preview.Clip = new RectangleGeometry(new Rect(0, 0, preview.ActualWidth, preview.ActualHeight), 12, 12); };
            video.MediaOpened += delegate
            {
                C<TextBlock>("VideoPlaceholder").Visibility = Visibility.Collapsed;
                if (state == null || videoRound != state.round) return;
                if (videoMode == "source" && (state.phase == "loading" || state.phase == "recording")) { sourcePrepared = true; video.Position = TimeSpan.FromSeconds(state.pack.lines[state.turn].start); video.Pause(); RenderRound(); AcknowledgePrepared(); }
                else if (videoMode == "result" && state.phase == "result") { finalVideoOpened = true; video.Position = TimeSpan.Zero; video.Pause(); FinalPrepared(); }
            };
            video.MediaFailed += delegate(object sender, ExceptionRoutedEventArgs e) { C<TextBlock>("VideoPlaceholder").Text = "Видео не открылось. Проверьте кодек или импортируйте MP4 H.264."; C<TextBlock>("VideoPlaceholder").Visibility = Visibility.Visible; Toast("Ошибка видео: " + e.ErrorException.Message); if (state != null && state.phase == "loading") Send(new Message { kind = "pack_failed", round = state.round, text = "Не удалось открыть видео на этом компьютере." }); };
            video.MediaEnded += delegate { if (state != null && state.phase == "result" && videoMode == "result" && playbackAppliedVersion == state.playbackVersion) { video.Pause(); endedPlaybackVersion = state.playbackVersion; if (IsHost && state.playing) Send(new Message { kind = "playback", text = "end", round = state.round, index = state.playbackVersion }); } };
            voice.MediaFailed += delegate { voicePlaying = false; playbackFinished = null; Toast("Не удалось открыть аудиозапись."); };
            voice.MediaEnded += delegate { voicePlaying = false; var action = playbackFinished; playbackFinished = null; if (action != null) action(); };
            toastTimer.Tick += delegate { toastTimer.Stop(); C<Border>("Toast").Visibility = Visibility.Collapsed; };
            tick.Tick += delegate { Tick(); }; tick.Start();
        }
        string SettingsFile { get { return System.IO.Path.Combine(Paths.Data, "settings.json"); } }
        T C<T>(string name) where T : class { return window.FindName(name) as T; }
        Button B(string name) { return C<Button>(name); }
        TextBox TBox(string name) { return C<TextBox>(name); }
        void Text(string name, string value) { C<TextBlock>(name).Text = value; }
        Player Me { get { return state == null || client == null ? null : state.players.FirstOrDefault(p => p.id == client.Id); } }
        bool IsHost { get { return Me != null && Me.host; } }
        bool IsTurn { get { return state != null && client != null && state.phase == "recording" && state.Active != null && state.Active.owner == client.Id; } }
        public async Task Start()
        {
            try { installed = await Task.Run(() => PackLibrary.Installed()); if (installed.Count == 0) throw new InvalidOperationException("Не найден пробный пак в Assets/Demo."); await CreateRoom(); }
            catch (Exception error) { Toast(error.Message); Text("FooterStatus", "Не удалось открыть комнату. Проверьте FFmpeg и папку Assets."); }
        }
        public async Task Smoke(string packPath)
        {
            var guest = new RoomClient(); string preparingTest = null;
            guest.Received += message => { if (message.state != null && message.state.phase == "loading" && preparingTest != message.state.round) { preparingTest = message.state.round; var ignored = PrepareTestPeer(guest, message.state); } };
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (Me == null && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (Me == null) throw new Exception("Лобби не подключилось.");
                if (packPath != null) { var selected = PackLibrary.Load(packPath, System.IO.Path.GetFileName(packPath)); Send(new Message { kind = "pack", pack = selected }); while (state.pack.id != selected.id && DateTime.UtcNow < deadline) await Task.Delay(50); }
                await Task.Run(() => guest.Connect("127.0.0.1", server.Port, server.Code, "Сетевой друг", ""));
                while (state.players.Count < 2 && DateTime.UtcNow < deadline) await Task.Delay(50);
                guest.Send(new Message { kind = "chat", text = "Сетевой чат работает. Готов к озвучке!" });
                while (!state.chat.Any(c => c.text.Contains("Сетевой чат")) && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (!C<StackPanel>("ChatMessages").Children.OfType<TextBlock>().Any(line => line.Inlines.OfType<System.Windows.Documents.Run>().Any(run => run.Text.Contains("Сетевой чат")))) throw new Exception("Сообщение сетевого игрока не попало в историю чата.");
                Send(new Message { kind = "ready", flag = true }); guest.Send(new Message { kind = "ready", flag = true });
                while (!state.players.All(p => p.ready) && DateTime.UtcNow < deadline) await Task.Delay(50);
                SaveScreen("network-lobby-preview.png");
                B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => state.phase == "casting", "Не открылся обязательный выбор ролей.");
                B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => RoleCasting.Complete(state), "Роли не распределились.");
                B("RolesReadyButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); guest.Send(new Message { kind = "role_ready", flag = true, round = state.round, index = state.roleVersion });
                await Until(() => state.players.All(player => player.ready), "Роли не подтверждены.");
                B("BeginDubbingButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(150); if (state.phase == "loading") SaveScreen("pack-loading-preview.png");
                while ((state.phase != "recording" || sourceFile == null) && DateTime.UtcNow < deadline) await Task.Delay(50);
                await Task.Delay(1800);
                var video = C<MediaElement>("Video");
                if (state.phase != "recording" || !IsTurn || currentPage != "Round" || video.NaturalVideoWidth == 0 || !B("RecordButton").IsEnabled) throw new Exception("Экран раунда или видеодекодер не подготовился. Video width: " + video.NaturalVideoWidth);
                File.WriteAllText(System.IO.Path.Combine(Paths.Base, "ui-smoke.txt"), "PASS: two TCP clients -> ready -> start -> video prepared (" + video.NaturalVideoWidth + "x" + video.NaturalVideoHeight + ") -> record action enabled");
                var root = C<Grid>("Root"); root.UpdateLayout(); var bitmap = new RenderTargetBitmap(1440, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(System.IO.Path.Combine(Paths.Base, "round-preview.png"))) encoder.Save(output);
                Send(new Message { kind = "take", index = 0, round = state.round, data = Convert.ToBase64String(Audio.Wave(new byte[44100])) });
                while (state.turn != 1 && DateTime.UtcNow < deadline) await Task.Delay(50);
                guest.Send(new Message { kind = "heard", index = 1, round = state.round });
                while (!state.heard && DateTime.UtcNow < deadline) await Task.Delay(50);
                guest.Send(new Message { kind = "recording", index = 1, round = state.round, flag = true });
                while (!state.performing && DateTime.UtcNow < deadline) await Task.Delay(50);
                if (C<Border>("WaitingOverlay").Visibility != Visibility.Visible || !C<TextBlock>("WaitingTitle").Text.Contains("Скоро") || !C<TextBlock>("WaitingDetail").Text.Contains("Сетевой друг") || C<StackPanel>("RoundActions").Visibility != Visibility.Collapsed) throw new Exception("На чужом ходу не показано ожидание очереди.");
                SaveScreen("waiting-preview.png"); File.AppendAllText(System.IO.Path.Combine(Paths.Base, "ui-smoke.txt"), "\nPASS: other player's network recording -> waiting overlay with next-turn notice");
                string selectedId = state.pack.id; var cancellationWatch = Stopwatch.StartNew();
                var pending = DownloadPack(async token => { await Task.Delay(15000, token); return installed[0]; }, "Проверка отмены загрузки…");
                SaveScreen("download-preview.png"); B("CancelDownloadButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await pending;
                if (downloadCancellation != null || C<Border>("DownloadBar").Visibility != Visibility.Collapsed || state.pack.id != selectedId || cancellationWatch.ElapsedMilliseconds > 2500) throw new Exception("Кнопка отмены не остановила загрузку или изменила пак.");
                File.AppendAllText(System.IO.Path.Combine(Paths.Base, "ui-smoke.txt"), "\nPASS: Cancel button stops operation promptly and preserves selected pack");
            }
            catch (Exception e) { File.WriteAllText(System.IO.Path.Combine(Paths.Base, "ui-smoke.txt"), "FAIL: " + e); }
            finally { guest.Dispose(); window.Close(); }
        }
        public async Task WorkshopScreen()
        {
            try
            {
                Page("Workshop"); await Search(); await Task.Delay(2500); SaveScreen("workshop-live-preview.png");
                var cards = C<WrapPanel>("PackCards"); if (cards.Children.Count < (settings.LocalPacks ? 1 : 3)) throw new Exception("Недостаточно карточек для проверки.");
                File.WriteAllText(System.IO.Path.Combine(Paths.Base, "workshop-ui-test.txt"), "PASS: " + (settings.LocalPacks ? "local folder catalog" : "live Steam catalog") + ", responsive cards, dark controls and pack metadata");
            }
            catch (Exception e) { File.WriteAllText(System.IO.Path.Combine(Paths.Base, "workshop-ui-test.txt"), "FAIL: " + e); }
            finally { window.Close(); }
        }
        public void SettingsScreen() { OpenSettings(); SaveScreen("settings-preview.png"); }
        public async Task PackScreen(string path, bool roles = false)
        {
            RoomClient previewGuest = roles ? new RoomClient() : null;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(10); while (Me == null && DateTime.UtcNow < deadline) await Task.Delay(50);
                var selected = await Task.Run(() => PackLibrary.Load(path, System.IO.Path.GetFileName(path))); SelectPack(selected);
                while ((state == null || state.pack.id != selected.id) && DateTime.UtcNow < deadline) await Task.Delay(50);
                await Task.Delay(500);
                if (roles)
                {
                    await Task.Run(() => previewGuest.Connect("127.0.0.1", server.Port, server.Code, "Предпросмотр", "")); await Until(() => state.players.Count == 2, "Не подключился тестовый участник.");
                    Send(new Message { kind = "ready", flag = true }); previewGuest.Send(new Message { kind = "ready", flag = true }); await Until(() => state.players.All(player => player.ready), "Нет готовности.");
                    B("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => state.phase == "casting", "Не начался выбор ролей.");
                    B("AutoRolesButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Until(() => RoleCasting.Complete(state), "Роли не распределились.");
                    window.UpdateLayout(); SaveScreen("roles-pack-preview.png");
                }
                else SaveScreen("selected-pack-preview.png");
                if (C<Image>("PackPreview").Source == null || !C<TextBlock>("PackMeta").Text.Contains("Ролей: " + selected.DisplayRoles)) throw new Exception("Превью или роли выбранного пака не отображаются.");
                File.WriteAllText(System.IO.Path.Combine(Paths.Base, "selected-pack-test.txt"), "PASS: selected pack cover visible; source lines=" + selected.DisplayLines + "; roles=" + selected.DisplayRoles);
            }
            catch (Exception e) { File.WriteAllText(System.IO.Path.Combine(Paths.Base, "selected-pack-test.txt"), "FAIL: " + e); }
            finally { if (previewGuest != null) previewGuest.Dispose(); window.Close(); }
        }
        void SaveScreen(string file)
        {
            var root = C<Grid>("Root"); root.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(System.IO.Path.Combine(Paths.Base, file))) encoder.Save(stream);
        }
        async Task CreateRoom()
        {
            Disconnect(); sourceFile = null; finalFile = null; turnKey = "";
            try { server = new RoomServer(installed[0], Connection.DefaultPort); }
            catch (System.Net.Sockets.SocketException) { server = new RoomServer(installed[0], 0); }
            await Connect("127.0.0.1", server.Port, server.Code, server.Secret);
            if (steam.Available) try { await steam.HostRoom(server.Code, server.Port); } catch (Exception e) { Toast("Комната работает по LAN/Radmin. " + e.Message); }
            RefreshConnection(); Text("SteamNetworkStatus", steam.Status);
        }
        async Task Connect(string host, int port, string code, string secret, string invitation = null)
        {
            remoteInvitation = invitation ?? Connection.Invitation(host, port, code);
            var next = new RoomClient(); client = next;
            next.Received += message => window.Dispatcher.BeginInvoke(new Action(() => { if (client == next && !disposed) Receive(message); }));
            next.Disconnected += reason => window.Dispatcher.BeginInvoke(new Action(async delegate { if (client != next || disposed) return; Toast(reason); await Guard(CreateRoom); }));
            string playerName = PlayerName;
            await Task.Run(() => next.Connect(host, port, code, playerName, secret));
        }
        void Disconnect()
        {
            ResetFinalPlayer(); StopMicrophonePanel(); if (preparationCancel != null) preparationCancel.Cancel(); preparedAckRound = null;
            if (steam != null) steam.LeaveRoom();
            if (effectCancellation != null) effectCancellation.Cancel(); takeRevision++; voiceRevision++;
            if (microphone.Recording) microphone.Stop(); voice.Stop(); playbackFinished = null; voicePlaying = false; C<MediaElement>("Video").Stop();
            sourceFile = null; sourcePrepared = false; videoDownload = null; finalFile = null; finalMediaPrepared = false; turnKey = "";
            if (client != null) client.Dispose(); if (server != null) server.Dispose(); client = null; server = null; state = null;
        }
        async Task Join()
        {
            string address = Prompt("Войти в комнату", "Приглашение Steam или IP:порт/КОД от ведущего", ""); if (String.IsNullOrWhiteSpace(address)) return;
            uint appId; ulong lobby;
            if (SteamBridge.TryInvitation(address, out appId, out lobby)) { if (!steam.Available) throw new IOException("Сначала подключи Steam в настройках."); if (appId != steam.AppId) throw new IOException("У участников должен совпадать Steam App ID."); await JoinSteam(lobby); return; }
            Uri uri = Connection.Parse(address);
            Disconnect();
            Exception failure = null;
            try { await Connect(uri.Host, uri.Port, uri.AbsolutePath.Trim('/'), ""); Page("Lobby"); }
            catch (Exception error) { failure = error; }
            if (failure != null) { await CreateRoom(); throw failure; }
        }
        void Send(Message message) { try { if (client != null) client.Send(message); } catch (Exception e) { Toast(e.Message); } }
        void Receive(Message message)
        {
            if (message.kind == "error" || message.kind == "kicked") { Toast(message.text); return; }
            if (message.kind != "state") return;
            if (state != null && state.pack.contentKey != message.state.pack.contentKey) { sourceFile = null; sourcePrepared = false; videoDownload = null; }
            string previousPhase = state == null ? "" : state.phase;
            state = message.state; if (state.phase == "lobby" && preparationCancel != null) preparationCancel.Cancel(); if (steam.Available) steam.SetJoinable(state.phase == "lobby");
            B("LobbyTab").IsEnabled = state.phase == "lobby" || state.phase == "result" && IsHost; RenderLobby();
            B("WorkshopTab").IsEnabled = state.phase != "casting";
            if (state.phase == "casting")
            {
                if (previousPhase != "casting" || currentPage != "Settings") Page("Roles");
                return;
            }
            if (state.phase != "lobby")
            {
                Page("Round"); string key = state.round + ":" + state.turn + ":" + state.phase;
                if (key != turnKey)
                {
                    if (!turnKey.StartsWith(state.round + ":", StringComparison.Ordinal)) { ResetFinalPlayer(); finalFile = null; finalMediaPrepared = false; playbackAppliedVersion = 0; endedPlaybackVersion = 0; }
                    if (effectCancellation != null) effectCancellation.Cancel(); takeRevision++;
                    turnKey = key; take = null; rawTake = null; voicePcm = null; processingTake = false; localTakeFile = null; previousFile = null; playbackFinished = null; playbackEnd = DateTime.MinValue; voiceRevision++; voice.Stop(); voicePlaying = false;
                    traceWave = new float[0]; Text("RecordingTimer", "");
                    if (microphone.Recording) microphone.Stop(); C<MediaElement>("Video").Pause();
                    RenderRound(); var ignored = Guard(PrepareRound);
                }
                else RenderRound();
            }
            else if (currentPage == "Round" || currentPage == "Roles") { if (microphone.Recording) microphone.Stop(); if (effectCancellation != null) effectCancellation.Cancel(); takeRevision++; voiceRevision++; voice.Stop(); voicePlaying = false; playbackFinished = null; C<MediaElement>("Video").Stop(); Page("Lobby"); }
        }
        void RenderLobby()
        {
            Text("PlayerCount", state.players.Count + " / 8"); Text("RoomCode", settings.Streamer ? "••••••" : state.code);
            Text("PackTitle", state.pack.title); Text("PackMeta", "Реплик: " + state.pack.DisplayLines + "   ·   Ролей: " + state.pack.DisplayRoles + "   ·   " + Math.Ceiling(state.pack.lines.Max(line => line.end)) + " сек."); Text("PackDescription", state.pack.description);
            Text("PosterBadge", !String.IsNullOrEmpty(state.pack.workshopId) ? "WORKSHOP" : state.pack.id == "Demo" ? "ПРОБНЫЙ ПАК" : "ВАШ ПАК");
            string previewKey = state.pack.id + ":" + state.pack.preview + ":" + (state.pack.thumbnail ?? "").GetHashCode();
            if (posterKey != previewKey)
            {
                posterKey = previewKey; var image = C<Image>("PackPreview"); image.Source = Cover(state.pack.thumbnail, state.pack.preview); image.Visibility = image.Source == null ? Visibility.Collapsed : Visibility.Visible;
                C<FrameworkElement>("PosterArt").Visibility = image.Source == null ? Visibility.Visible : Visibility.Collapsed;
            }
            var players = C<StackPanel>("PlayersList"); players.Children.Clear();
            for (int i = 0; i < 8; i++)
            {
                var row = new Grid { Height = 39, Margin = new Thickness(0, 0, 0, 3) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(57) });
                if (i < state.players.Count)
                {
                    var p = state.players[i];
                    var avatar = Avatar(p.color, 32); avatar.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(avatar);
                    var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(label, 1);
                    label.Children.Add(new TextBlock { Text = p.name + (p.id == client.Id ? " · ты" : ""), FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                    label.Children.Add(new TextBlock { Text = p.host ? "Ведущий" : "По сети", FontSize = 9, Foreground = Brush("#858478") }); row.Children.Add(label);
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(actions, 2);
                    actions.Children.Add(new TextBlock { Text = p.ready ? "✓" : "·", Foreground = Brush(p.ready ? "#638352" : "#A19B8E"), FontSize = 19, VerticalAlignment = VerticalAlignment.Center });
                    if (IsHost && !p.host) { var kick = SmallButton("×", delegate { Send(new Message { kind = "kick", id = p.id }); }); kick.Padding = new Thickness(5, 0, 0, 0); kick.BorderThickness = new Thickness(0); actions.Children.Add(kick); }
                    row.Children.Add(actions);
                }
                else
                {
                    var empty = new Border { Width = 31, Height = 31, CornerRadius = new CornerRadius(10), BorderBrush = Brush("#D8D2C6"), BorderThickness = new Thickness(1), Child = new TextBlock { Text = "+", FontSize = 17, Foreground = Brush("#C2BCB0"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } }; row.Children.Add(empty);
                    var text = new TextBlock { Text = "Место для друга", Foreground = Brush("#B0A99A"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(text, 1); row.Children.Add(text);
                }
                players.Children.Add(row);
            }
            B("ChoosePackButton").IsEnabled = IsHost; B("StartButton").IsEnabled = IsHost && state.phase == "lobby" && downloadCancellation == null && state.players.Count >= 2 && state.players.All(p => p.ready);
            B("ReadyButton").Content = Me != null && Me.ready ? "✓  Готов" : "Я готов"; B("ReadyButton").IsEnabled = state.phase == "lobby";
            Text("StartHint", state.players.Count < 2 ? "Пригласи друга по сети или через Radmin VPN" : state.players.All(p => p.ready) ? "Вся команда в сборе. Можно начинать!" : "Готовность: " + state.players.Count(p => p.ready) + " из " + state.players.Count);
            Text("ConnectionLabel", IsHost ? "Твоя комната · " + state.players.Count + "/8" : "В комнате · " + state.players.Count + "/8");
            RenderChat(); RenderConnection(); RenderRoles();
        }
        void RenderChat()
        {
            var scroll = C<ScrollViewer>("ChatScroll"); bool bottom = scroll.ScrollableHeight - scroll.VerticalOffset < 18;
            var messages = C<StackPanel>("ChatMessages"); messages.Children.Clear();
            foreach (var chat in state.chat)
            {
                var line = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 6, 4) };
                line.Inlines.Add(new System.Windows.Documents.Run(chat.time + "  ") { Foreground = Brush("#78849E"), FontSize = 10 });
                if (chat.name != "Студия") line.Inlines.Add(new System.Windows.Documents.Run(chat.name + ": ") { Foreground = Brush("#C4B1FF"), FontWeight = FontWeights.SemiBold });
                line.Inlines.Add(new System.Windows.Documents.Run(chat.text) { Foreground = Brush(chat.name == "Студия" ? "#8B97B1" : "#E6EAF4") }); messages.Children.Add(line);
            }
            if (bottom) scroll.ScrollToEnd();
        }
        void RefreshConnection()
        {
            var combo = C<ComboBox>("NetworkCombo"); combo.Tag = null; RenderConnection();
        }
        void RenderConnection()
        {
            if (state == null) return;
            Text("SteamNetworkStatus", steam.Status);
            B("ConnectSteamButton").IsEnabled = state.phase == "lobby" && (!steam.Available || steam.LobbyId == 0);
            B("ConnectSteamButton").Content = steam.Available && steam.LobbyId != 0 ? "Steam подключён" : "Подключить Steam";
            var combo = C<ComboBox>("NetworkCombo"); string mode = (IsHost ? "host" : "guest") + ":" + steam.LobbyId;
            if (!Object.Equals(combo.Tag, mode))
            {
                combo.Tag = mode;
                if (IsHost) { var addresses = Connection.Addresses(); if (steam.Available && steam.Hosting && steam.LobbyId != 0) addresses.Insert(0, new NetworkAddress { Steam = true, Name = "Steam · без VPN", Address = "Steam P2P" }); combo.ItemsSource = addresses; }
                else { uint app; ulong lobby; if (SteamBridge.TryInvitation(remoteInvitation, out app, out lobby)) combo.ItemsSource = new[] { new NetworkAddress { Steam = true, Name = "Steam · без VPN", Address = "Steam P2P" } }; else { var uri = Connection.Parse(remoteInvitation); combo.ItemsSource = new[] { new NetworkAddress { Address = uri.Host, Name = "Ведущий" } }; } }
                combo.SelectedIndex = 0;
            }
            combo.IsEnabled = IsHost; var selected = combo.SelectedItem as NetworkAddress;
            if (state.players.Count < 2) Text("StartHint", SteamRoute ? "Пригласи друга через Steam — VPN не требуется" : "Пригласи друга по локальной сети или через Radmin VPN");
            Text("RoomAddress", SteamRoute ? (settings.Streamer ? "Steam: комната скрыта" : "Steam P2P · комната " + state.code) : settings.Streamer ? "Адрес скрыт для стрима" : IsHost && server != null ? (selected == null ? RoomServer.Address() : selected.Address) + ":" + server.Port : "Подключено к " + (selected == null ? "ведущему" : selected.Address));
        }
        void Invite()
        {
            if (state == null) return;
            var selected = C<ComboBox>("NetworkCombo").SelectedItem as NetworkAddress;
            string invitation = SteamRoute ? steam.Invitation : server == null ? remoteInvitation : Connection.Invitation(selected == null ? RoomServer.Address() : selected.Address, server.Port, server.Code); Clipboard.SetText(invitation);
            Toast("Приглашение скопировано: " + (settings.Streamer ? "код скрыт" : invitation) + ". Друг вставляет его в «Войти в комнату» на своём ПК.");
        }
        void SendChat() { string value = TBox("ChatInput").Text.Trim(); if (value.Length > 0) { Send(new Message { kind = "chat", text = value }); TBox("ChatInput").Clear(); } }
        void Page(string name)
        {
            if (state != null && name == "Lobby" && state.phase == "casting") name = "Roles";
            else if (state != null && (name == "Lobby" || name == "Roles") && state.phase != "lobby" && state.phase != "casting") name = "Round";
            else if (name == "Roles" && (state == null || state.phase != "casting")) name = "Lobby";
            if (microphone.Recording && name != "Round") { Toast("Сначала заверши запись реплики."); return; }
            if (name != "Settings") StopMicrophonePanel();
            currentPage = name;
            C<Grid>("LobbyPanel").Visibility = name == "Lobby" ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("RolesPanel").Visibility = name == "Roles" ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("WorkshopPanel").Visibility = name == "Workshop" ? Visibility.Visible : Visibility.Collapsed;
            C<Grid>("RoundPanel").Visibility = name == "Round" ? Visibility.Visible : Visibility.Collapsed;
            C<Border>("SettingsPanel").Visibility = name == "Settings" ? Visibility.Visible : Visibility.Collapsed;
            foreach (string page in new[] { "Lobby", "Workshop", "Settings" }) { bool selected = page == name || page == "Lobby" && name == "Roles"; B(page + "Tab").Background = Brush(selected ? "#34304D" : "#00FFFFFF"); B(page + "Tab").Foreground = Brush(selected ? "#D4C6FF" : "#AAB4CC"); }
            if (name != "Round") { voice.Stop(); C<MediaElement>("Video").Pause(); finalVoices.Pause(); finalMovie.Pause(); playbackAppliedVersion = 0; playbackFinished = null; }
            if (name == "Roles") RenderRoles();
        }
        void OpenSettings()
        {
            if (microphone.Recording) { Toast("Сначала заверши запись."); return; }
            Page("Settings"); changingMicDevice = true; RenderPlayerName(); C<ComboBox>("MicrophoneCombo").ItemsSource = MicrophoneRecorder.Devices(); C<ComboBox>("MicrophoneCombo").SelectedIndex = Math.Min(settings.Microphone, C<ComboBox>("MicrophoneCombo").Items.Count - 1); changingMicDevice = false; if (checkDevice != C<ComboBox>("MicrophoneCombo").SelectedIndex) { ClearMicCheck(); checkDevice = C<ComboBox>("MicrophoneCombo").SelectedIndex; }
            C<Slider>("VolumeSlider").Value = settings.Volume; TBox("SteamAccountInput").Text = settings.SteamAccount == "anonymous" ? "" : settings.SteamAccount; C<CheckBox>("StreamerCheck").IsChecked = settings.Streamer;
            TBox("PacksFolderInput").Text = PacksFolder; C<ComboBox>("PackSourceCombo").SelectedIndex = settings.LocalPacks ? 0 : 1; RenderPackSourceSettings();
        }
        async Task SaveSettings()
        {
            bool local = C<ComboBox>("PackSourceCombo").SelectedIndex != 1; string folder = FolderPacks.Normalize(TBox("PacksFolderInput").Text);
            if (local && !Directory.Exists(folder)) throw new DirectoryNotFoundException("Папка с паками не найдена. Проверь путь или нажми «Обзор…».");
            if (!steam.Available) settings.Name = String.IsNullOrWhiteSpace(TBox("NameInput").Text) ? Environment.UserName : TBox("NameInput").Text.Trim(); settings.Microphone = Math.Max(0, C<ComboBox>("MicrophoneCombo").SelectedIndex); settings.Volume = C<Slider>("VolumeSlider").Value;
            settings.SteamAccount = String.IsNullOrWhiteSpace(TBox("SteamAccountInput").Text) ? "anonymous" : TBox("SteamAccountInput").Text.Trim(); settings.Streamer = C<CheckBox>("StreamerCheck").IsChecked == true;
            workshop.Account = settings.SteamAccount; await ApplyPackSource(local, folder); if (state != null && state.phase == "lobby") Send(new Message { kind = "rename", text = PlayerName }); Toast("Настройки сохранены."); if (state != null) RenderLobby(); Page("Lobby");
        }
        void Persist() { File.WriteAllText(SettingsFile, Json.Write(settings)); }
        void OpenWorkshop() { Page("Workshop"); if (C<WrapPanel>("PackCards").Children.Count == 0) { var ignored = Guard(Search); } }
        async Task Search()
        {
            int generation = ++searchGeneration; string query = TBox("SearchInput").Text; int sort = C<ComboBox>("SortCombo").SelectedIndex, language = C<ComboBox>("LanguageCombo").SelectedIndex;
            if (folderScan != null) folderScan.Cancel();
            Text("WorkshopStatus", "Загружаем каталог…"); B("SearchButton").IsEnabled = false;
            try
            {
                var cards = C<WrapPanel>("PackCards"); packControls.Clear(); cards.Children.Clear();
                if (settings.LocalPacks) { await SearchFolder(query, language, generation); return; }
                if (sort == 3)
                {
                    var found = await Task.Run(() => workshop.InstalledPacks()); if (generation != searchGeneration || disposed) return; installed = found;
                    foreach (var pack in installed.Where(p => p.title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)) LocalCard(pack);
                    Text("WorkshopStatus", "В папке этой игры: " + installed.Count + " паков. " + Paths.Packs);
                }
                else
                {
                    var items = await Task.Run(() => workshop.Browse(query, workshopPage, sort, language)); if (generation != searchGeneration || disposed) return;
                    foreach (var item in items) WorkshopCard(item);
                    Text("WorkshopStatus", items.Count == 0 ? "По этим фильтрам ничего не найдено." : "Найдено на странице: " + items.Count + ". Паки скачиваются из Steam в папку этой игры.");
                }
                Text("PageLabel", "Страница " + workshopPage);
            }
            catch (Exception error)
            {
                if (generation != searchGeneration) return;
                Text("WorkshopStatus", "Каталог недоступен: " + error.Message); foreach (var pack in installed) LocalCard(pack);
                Toast("Не удалось получить каталог Steam. Можно выбрать установленный пак или импортировать свой.");
            }
            finally { if (generation == searchGeneration && !disposed) B("SearchButton").IsEnabled = true; }
        }
        void LocalCard(Pack pack)
        {
            var body = CardBase(pack.title, FolderPacks.InLibrary(pack) ? "В МОЕЙ КОЛЛЕКЦИИ" : "ИЗ ВЫБРАННОЙ ПАПКИ", "Реплик: " + pack.DisplayLines + "   ·   Ролей: " + pack.DisplayRoles, pack.preview);
            ((Border)body.Parent).ToolTip = pack.root;
            var cover = ((Border)body.Children[0]); var source = Cover(pack.thumbnail, pack.preview); if (source != null) cover.Child = new Image { Source = source, Stretch = Stretch.UniformToFill };
            body.Children.Add(SmallButton("Выбрать для лобби  →", async delegate
            {
                await Guard(async delegate
                {
                    if (!IsHost) { Toast("Пак выбирает ведущий."); return; }
                    if (state == null || state.phase != "lobby") { Toast("Выбор пака доступен ведущему в лобби."); return; }
                    if (!FolderPacks.InLibrary(pack) || System.IO.Path.GetExtension(pack.video).ToLowerInvariant() != ".mp4")
                    {
                        await DownloadPack(token => Task.Run(() => FolderPacks.Prepare(pack, token), token), "Копируем пак в игру и подготавливаем видео…"); return;
                    }
                    SelectPack(pack);
                });
            }));
        }
        StackPanel CardBase(string title, string meta, string description, string preview)
        {
            var border = new Border { Style = (Style)window.Resources["Card"], Width = 400, Height = 400, Margin = new Thickness(0, 0, 16, 16), Padding = new Thickness(18) };
            var body = new StackPanel(); border.Child = body;
            var image = new Border { Height = 196, CornerRadius = new CornerRadius(10), Background = Brush("#252D42"), Margin = new Thickness(0, 0, 0, 14), ClipToBounds = true };
            image.SizeChanged += delegate { image.Clip = new RectangleGeometry(new Rect(0, 0, image.ActualWidth, image.ActualHeight), 10, 10); };
            if (!String.IsNullOrEmpty(preview))
            {
                try { var uri = new Uri(preview); if (uri.Scheme == "https") image.Child = new Image { Source = new BitmapImage(uri), Stretch = Stretch.UniformToFill }; } catch { }
            }
            if (image.Child == null)
            {
                var fallback = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                fallback.Children.Add(new Image { Source = C<Image>("BrandLogo").Source, Width = 64, Height = 64, Opacity = .7 });
                fallback.Children.Add(new TextBlock { Text = "Превью пака", Foreground = Brush("#78849E"), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) }); image.Child = fallback;
            }
            body.Children.Add(image); body.Children.Add(new TextBlock { Text = meta, FontSize = 9, Foreground = Brush("#858478"), FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
            body.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeights.Bold, Height = 46, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis });
            body.Children.Add(new TextBlock { Text = description, FontSize = 12, Foreground = Brush("#AAB4CC"), Height = 28, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 3, 0, 7) }); C<WrapPanel>("PackCards").Children.Add(border); UpdateWorkshopLayout(); return body;
        }
        void UpdateWorkshopLayout()
        {
            var panel = C<WrapPanel>("PackCards"); double width = panel.ActualWidth; if (width < 100) return;
            int columns = Math.Max(1, (int)Math.Floor((width + 18) / 358)); double tileWidth = Math.Floor((width - (columns - 1) * 18) / columns);
            for (int i = 0; i < panel.Children.Count; i++)
            {
                var card = panel.Children[i] as Border; if (card == null) continue;
                card.Width = tileWidth; card.Margin = new Thickness(0, 0, (i + 1) % columns == 0 ? 0 : 18, 18);
                var body = card.Child as StackPanel; if (body == null || body.Children.Count == 0) continue;
                double imageHeight = Math.Round((tileWidth - 39) * 9 / 16); ((Border)body.Children[0]).Height = imageHeight; card.Height = imageHeight + (Object.Equals(card.Tag, "download-card") ? 268 : 202);
            }
        }
        static ImageSource Cover(string encoded, string url)
        {
            try
            {
                if (!String.IsNullOrEmpty(encoded))
                    using (var stream = new MemoryStream(Convert.FromBase64String(encoded))) { var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 640; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); return bitmap; }
                Uri uri; if (!String.IsNullOrEmpty(url) && Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.Scheme == "https") return new BitmapImage(uri);
            }
            catch (Exception error) { Trace.WriteLine(error); }
            return null;
        }
        async Task Import()
        {
            if (!IsHost) { Toast("Пак выбирает ведущий."); return; }
            var picker = new OpenFileDialog { Filter = "Пак или видео|*.zip;*.dubpack;*.mp4;*.webm;*.mkv;*.mov;*.avi;*.json|Все файлы|*.*", Title = "Выберите ZIP, видео или pack.json из папки пака" };
            if (picker.ShowDialog(window) != true) return;
            string source = System.IO.Path.GetExtension(picker.FileName).ToLowerInvariant() == ".json" ? System.IO.Path.GetDirectoryName(picker.FileName) : picker.FileName;
            await DownloadPack(token => Task.Run(() => PackLibrary.Import(source, "", token), token), "Проверяем и импортируем пак…");
        }
        async Task DownloadPack(Func<CancellationToken, Task<Pack>> load, string description)
        {
            if (downloadCancellation != null) { Toast("Уже идёт загрузка. Дождись её или нажми «Отменить»."); return; }
            var source = new CancellationTokenSource(); downloadCancellation = source; C<ProgressBar>("DownloadProgress").IsIndeterminate = true;
            C<Border>("DownloadBar").Visibility = Visibility.Visible; B("CancelDownloadButton").IsEnabled = true; B("CancelDownloadButton").Content = "Отменить"; Text("DownloadStatus", description);
            if (state != null) RenderLobby();
            try { var pack = await load(source.Token); source.Token.ThrowIfCancellationRequested(); if (!disposed) SelectPack(pack); }
            catch (OperationCanceledException) { if (!disposed) { Toast("Загрузка отменена. Выбранный пак не изменён."); Text("WorkshopStatus", "Загрузка отменена."); } }
            catch (Exception error) { if (!disposed) { Toast(error.GetBaseException().Message); Text("WorkshopStatus", error.GetBaseException().Message); } }
            finally
            {
                downloadCancellation = null; source.Dispose();
                if (!disposed) { C<Border>("DownloadBar").Visibility = Visibility.Collapsed; RenderDownloadJobs(); if (state != null) RenderLobby(); }
            }
        }
        void ReportDownload(string status)
        {
            window.Dispatcher.BeginInvoke(new Action(() => { if (!disposed && downloadCancellation != null && !downloadCancellation.IsCancellationRequested) { Text("DownloadStatus", status); Text("WorkshopStatus", status); } }));
        }
        void CancelDownload()
        {
            if (downloadCancellation == null) { var active = packDownloads.Snapshot().FirstOrDefault(j => j.Busy); if (active != null) packDownloads.Cancel(active.Item.Id); return; }
            downloadCancellation.Cancel(); B("CancelDownloadButton").IsEnabled = false; B("CancelDownloadButton").Content = "Отменяем…"; Text("DownloadStatus", "Останавливаем загрузку и подготовку пака…");
        }
        void SelectPack(Pack pack)
        {
            if (!IsHost || state.phase != "lobby") { Toast("Выбор пака доступен ведущему в лобби."); return; }
            if (preparationCancel != null) preparationCancel.Cancel(); sourceFile = null; videoDownload = null; sourcePrepared = false; Send(new Message { kind = "pack", pack = pack }); Page("Lobby"); Toast(pack.generatedTiming ? "Пак загружен. Реплики разбиты автоматически примерно по 4 секунды." : "Пак выбран. Отметьте готовность заново.");
        }
        async Task Guard(Func<Task> action) { try { await action(); } catch (Exception e) { Toast(e.GetBaseException().Message); } }
        void Toast(string value) { Text("ToastText", value); C<Border>("Toast").Visibility = Visibility.Visible; toastTimer.Stop(); toastTimer.Start(); }
        static SolidColorBrush Brush(string value) { return Theme.Brush(value); }
        Button SmallButton(string text, Action action)
        {
            var button = new Button { Content = text, FontSize = 11, Padding = new Thickness(12, 7, 12, 7) }; button.Click += delegate { action(); }; return button;
        }
        Border Avatar(int color, int size)
        {
            string[] colors = { "#F3A680", "#B6C99E", "#B5B0D7", "#EACB80", "#A0C4D0", "#D4ACC0", "#C1B096", "#D5D89C" };
            var face = new Canvas { Width = size, Height = size };
            var eyes = new TextBlock { Text = "•  •", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brush("#1D2130") }; Canvas.SetLeft(eyes, 7); Canvas.SetTop(eyes, 4); face.Children.Add(eyes);
            var mouth = new TextBlock { Text = "⌣", FontSize = 15, Foreground = Brush("#1D2130") }; Canvas.SetLeft(mouth, 10); Canvas.SetTop(mouth, 12); face.Children.Add(mouth);
            return new Border { Width = size, Height = size, Background = Brush(colors[color % colors.Length]), BorderBrush = Brush("#292923"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = face, ClipToBounds = true };
        }
        string Prompt(string title, string label, string initial)
        {
            var popup = new Window { Title = title, Width = 530, Height = 220, Owner = window, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Background = Brush("#F5F1E8"), Foreground = Brush("#EFF1FA"), FontFamily = window.FontFamily };
            Theme.ApplyWindow(popup); popup.Resources.MergedDictionaries.Add(window.Resources);
            var panel = new StackPanel { Margin = new Thickness(25) }; popup.Content = panel; panel.Children.Add(new TextBlock { Text = label, FontSize = 14, Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap });
            var input = new TextBox { Text = initial, Padding = new Thickness(10), FontSize = 16 }; panel.Children.Add(input);
            var ok = new Button { Content = "Продолжить  →", Background = Brush("#E4ECCA"), Margin = new Thickness(0, 15, 0, 0), Padding = new Thickness(12, 8, 12, 8), IsDefault = true }; ok.Click += delegate { popup.DialogResult = true; }; panel.Children.Add(ok); popup.Loaded += delegate { input.Focus(); input.SelectAll(); }; return popup.ShowDialog() == true ? input.Text : null;
        }
        public void Dispose() { disposed = true; if (folderScan != null) folderScan.Cancel(); if (downloadCancellation != null) downloadCancellation.Cancel(); if (effectCancellation != null) effectCancellation.Cancel(); tick.Stop(); toastTimer.Stop(); if (packDownloads != null) packDownloads.Dispose(); Disconnect(); microphone.Dispose(); checkMicrophone.Dispose(); checkPlayback.Close(); DeleteMicFile(); voice.Close(); if (steam != null) steam.Dispose(); }
    }
}
