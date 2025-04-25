using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using ColorThiefDotNet;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using System.Windows.Threading;
using System.Diagnostics;
using System.Text;
using System.Net;

namespace SpottyScreen
{
    public partial class MainWindow : Window
    {
        private SpotifyClient spotify;
        private List<LyricLine> lyrics = new List<LyricLine>();
        private int currentLyricIndex = -1;
        private DispatcherTimer lyricTimer = new DispatcherTimer();

        public MainWindow()
        {
            InitializeComponent();
            AuthenticateSpotify();
        }

        private async void AuthenticateSpotify()
        {
            const string redirectUri = "http://127.0.0.1:5000/callback";
            const string clientId = "41033dc65baf42e287b21398aafb4501"; // Replace with yours

            string savedToken = Properties.Settings.Default.SpotifyAccessToken;
            string savedRefreshToken = Properties.Settings.Default.SpotifyRefreshToken;

            if (!string.IsNullOrEmpty(savedToken) && !string.IsNullOrEmpty(savedRefreshToken))
            {
                spotify = new SpotifyClient(savedToken);

                // Test if the token works; if not, refresh it
                try
                {
                    await spotify.Player.GetCurrentPlayback();
                    StartPolling();
                }
                catch (APIUnauthorizedException)
                {
                    await RefreshAccessToken(clientId, savedRefreshToken, redirectUri);
                    StartPolling();
                }
            }
            else
            {
                // Generate code verifier and challenge
                var (verifier, challenge) = PKCEUtil.GenerateCodes();

                // Set up the login request
                var loginRequest = new LoginRequest(
                    new Uri(redirectUri),
                    clientId,
                    LoginRequest.ResponseType.Code
                )
                {
                    CodeChallengeMethod = "S256",
                    CodeChallenge = challenge,
                    Scope = new[] {
                Scopes.UserReadPlaybackState,
                Scopes.UserReadCurrentlyPlaying
            }
                };

                // Start a mini web server to listen for Spotify redirect
                var http = new HttpListener();
                http.Prefixes.Add("http://127.0.0.1:5000/callback/");
                http.Start();

                // Open Spotify login in browser
                Process.Start(new ProcessStartInfo
                {
                    FileName = loginRequest.ToUri().ToString(),
                    UseShellExecute = true
                });

                // Wait for callback
                var context = await http.GetContextAsync();
                var code = context.Request.QueryString["code"];

                // Respond to browser
                string responseString = "<html><body><h1>Login successful!</h1><p>You can now close this window.</p></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseString);
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                http.Stop();

                // Exchange code for access token
                var tokenRequest = new PKCETokenRequest(clientId, code, new Uri(redirectUri), verifier);
                var oauth = new OAuthClient();
                var tokenResponse = await oauth.RequestToken(tokenRequest);

                // Save the access and refresh tokens
                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                Properties.Settings.Default.Save();

                // Initialize Spotify client
                spotify = new SpotifyClient(tokenResponse.AccessToken);
                StartPolling();
            }
        }

        private async Task RefreshAccessToken(string clientId, string refreshToken, string redirectUri)
        {
            var refreshRequest = new PKCETokenRefreshRequest(clientId, refreshToken);
            var oauth = new OAuthClient();

            try
            {
                var tokenResponse = await oauth.RequestToken(refreshRequest);

                // Save the new access token
                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                Properties.Settings.Default.Save();

                // Update the Spotify client
                spotify = new SpotifyClient(tokenResponse.AccessToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to refresh token: {ex.Message}");
                MessageBox.Show("Failed to refresh Spotify token. Please re-authenticate.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private FullTrack currentTrack;

        private async void StartPolling()
        {
            while (true)
            {
                try
                {
                    // Get current playback info
                    var playback = await spotify.Player.GetCurrentPlayback();
                    if (playback?.Item is FullTrack track && track.Id != currentTrack?.Id)
                    {
                        currentTrack = track;

                        // Reset the UI and scroll logic for the new song
                        ResetLyricsUI();
                        UpdateUI(track);
                        await LoadLyricsAsync(track);
                    }

                    // Get the current playback position
                    var playbackProgress = playback?.ProgressMs ?? 0;
                    var playbackTime = TimeSpan.FromMilliseconds(playbackProgress);

                    // Sync lyrics with playback time
                    SyncLyricsWithPlayback(playbackTime);

                    // Shorter delay for smoother updates
                    await Task.Delay(100); // Reduced to 100ms for better sync
                }
                catch (APIUnauthorizedException)
                {
                    Console.WriteLine("Access token expired, refreshing...");
                    await RefreshAccessToken("41033dc65baf42e287b21398aafb4501", Properties.Settings.Default.SpotifyRefreshToken, "http://127.0.0.1:5000/callback");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error during polling: {ex.Message}");
                }
            }
        }

        private void ResetLyricsUI()
        {
            // Stop any ongoing scrolling logic
            CompositionTarget.Rendering -= SmoothScrollHandler;

            // Clear lyrics and reset scroll state
            LyricsPanel.Children.Clear();
            LyricsScrollViewer.ScrollToVerticalOffset(0);

            // Reset the current lyric index
            currentLyricIndex = -1;
        }

        private void SyncLyricsWithPlayback(TimeSpan playbackTime)
        {
            // Find the closest lyric to the current playback time
            int idx = lyrics.FindLastIndex(l => l.Time <= playbackTime);

            if (idx != currentLyricIndex)
            {
                currentLyricIndex = idx;

                // Update UI to highlight the current lyric
                Application.Current.Dispatcher.Invoke(() =>
                {
                    UpdateLyricsDisplay(idx);
                });
            }
        }

        private void UpdateLyricsDisplay(int currentIndex)
        {
            // Clear existing children only if necessary
            if (LyricsPanel.Children.Count != lyrics.Count)
            {
                LyricsPanel.Children.Clear();

                foreach (var lyric in lyrics)
                {
                    var tb = new TextBlock
                    {
                        Text = lyric.Text,
                        Foreground = Brushes.Gray, // Default color
                        FontSize = 24,
                        Opacity = 0.5,
                        FontFamily = new FontFamily("Segoe UI Variable"),
                        Margin = new Thickness(0, 4, 0, 4)
                    };

                    LyricsPanel.Children.Add(tb);
                }
            }

            // Update the highlighting
            for (int i = 0; i < LyricsPanel.Children.Count; i++)
            {
                var tb = (TextBlock)LyricsPanel.Children[i];
                if (i == currentIndex)
                {
                    tb.Foreground = Brushes.White; // Highlight current lyric
                    tb.FontSize = 32;
                    tb.Opacity = 1;
                }
                else
                {
                    tb.Foreground = Brushes.Gray;
                    tb.FontSize = 24;
                    tb.Opacity = 0.5;
                }
            }

            // Smoothly scroll to the current lyric
            ScrollToCurrentLyric(currentIndex);
        }

        private async void UpdateUI(FullTrack track)
        {
            TrackName.Text = track.Name;
            ArtistName.Text = string.Join(", ", track.Artists.Select(a => a.Name));
            AlbumName.Text = track.Album.Name;

            var imageUrl = track.Album.Images.FirstOrDefault()?.Url;
            if (!string.IsNullOrEmpty(imageUrl))
            {
                var bitmap = new BitmapImage(new Uri(imageUrl));

                // Ensure the image is loaded before animation starts
                bitmap.DownloadCompleted += (s, e) =>
                {
                    AlbumCover.Source = bitmap;
                    BlurredBackground.Source = bitmap;

                    // Now that the image is loaded, start the animation
                    StartImageTransition();
                };

                // Start loading the image
                AlbumCover.Source = bitmap;
                BlurredBackground.Source = bitmap;
            }
        }

        private void StartImageTransition()
        {
            // Create your image transition animation here.
            // For example, fade the image in and out or create a zoom-in effect.
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1));
            AlbumCover.BeginAnimation(OpacityProperty, animation);
            BlurredBackground.BeginAnimation(OpacityProperty, animation);
        }

        private async Task LoadLyricsAsync(FullTrack track)
        {
            var artist = track.Artists.FirstOrDefault()?.Name ?? "";
            var title = track.Name;
            var album = track.Album.Name;

            // Clear previous lyrics
            lyrics.Clear();
            LyricsPanel.Children.Clear();

            using (var client = new HttpClient())
            {
                var url = $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}&album_name={Uri.EscapeDataString(album)}";

                try
                {
                    // Fetch the response
                    var json = await client.GetStringAsync(url);

                    var data = JArray.Parse(json);

                    if (data.Count > 0)
                    {
                        var syncedLyricsToken = data[0]["syncedLyrics"]?.ToString();

                        if (!string.IsNullOrEmpty(syncedLyricsToken))
                        {
                            // Use regular expression to match timestamp and lyrics
                            var regex = new System.Text.RegularExpressions.Regex(@"\[(\d{2}):(\d{2}\.\d{2})\](.*)");

                            // Match all lines in the syncedLyrics
                            var matches = regex.Matches(syncedLyricsToken);

                            foreach (System.Text.RegularExpressions.Match match in matches)
                            {
                                if (match.Groups.Count >= 4)
                                {
                                    try
                                    {
                                        var minute = int.Parse(match.Groups[1].Value);  // Minutes
                                        var seconds = double.Parse(match.Groups[2].Value);  // Seconds + tenths
                                        var lyric = match.Groups[3].Value.Trim();  // Lyric text

                                        // Convert to TimeSpan
                                        var time = new TimeSpan(0, 0, minute, (int)seconds, (int)((seconds - (int)seconds) * 1000));  // Convert to TimeSpan

                                        lyrics.Add(new LyricLine
                                        {
                                            Time = time,
                                            Text = lyric
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"Error parsing lyric line: {match.Value}. Exception: {ex.Message}");
                                    }
                                }
                            }

                            // Sort lyrics based on time
                            lyrics.Sort((a, b) => a.Time.CompareTo(b.Time));
                        }
                        else
                        {
                            Console.WriteLine("No syncedLyrics found in the response.");
                            ShowNoLyricsMessage();
                        }
                    }
                    else
                    {
                        Console.WriteLine("No data found in the API response.");
                        ShowNoLyricsMessage();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error while fetching lyrics: " + ex.Message);
                    ShowNoLyricsMessage();
                }
            }

            // Debug log: Ensure lyrics are loaded correctly
            Console.WriteLine($"Loaded {lyrics.Count} lyric lines.");

            StartLyricSync();
        }

        // Method to show a "No lyrics found" message
        private void ShowNoLyricsMessage()
        {
            var noLyricsTextBlock = new TextBlock
            {
                Text = "No lyrics found",
                Foreground = Brushes.White,
                FontSize = 24,
                FontFamily = new FontFamily("Segoe UI Variable"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            };

            // Add the message to the lyrics panel
            LyricsPanel.Children.Add(noLyricsTextBlock);
        }

        private async void UpdateLyricsDisplay()
        {
            var playback = await spotify.Player.GetCurrentlyPlaying(new PlayerCurrentlyPlayingRequest());
            var playbackTime = TimeSpan.FromMilliseconds(playback?.ProgressMs ?? 0); // Ensure playback time is calculated correctly

            int idx = lyrics.FindLastIndex(l => l.Time <= playbackTime);

            if (idx != currentLyricIndex)
            {
                currentLyricIndex = idx;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    LyricsPanel.Children.Clear();

                    for (int i = 0; i < lyrics.Count; i++)
                    {
                        var tb = new TextBlock
                        {
                            Text = lyrics[i].Text,
                            Foreground = i == idx ? Brushes.White : Brushes.Gray, // Highlight current lyric
                            FontSize = i == idx ? 32 : 24,
                            Opacity = i == idx ? 1 : 0.5,
                            FontFamily = new FontFamily("Segoe UI Variable"),
                            Margin = new Thickness(0, 4, 0, 4)
                        };

                        LyricsPanel.Children.Add(tb);
                    }

                    ScrollToCurrentLyric(idx);
                });
            }
        }

        private void ScrollToCurrentLyric(int currentIndex)
        {
            var scrollViewer = LyricsScrollViewer;

            if (scrollViewer == null)
            {
                Console.WriteLine("ScrollViewer not found!");
                return;
            }

            // Get the current lyric's TextBlock
            var currentLyric = LyricsPanel.Children[currentIndex] as TextBlock;

            if (currentLyric != null)
            {
                // Calculate the desired offset
                var lyricHeight = currentLyric.ActualHeight;
                var targetOffset = currentIndex * lyricHeight - (scrollViewer.ActualHeight / 2);

                // Smoothly scroll using CompositionTarget.Rendering
                double currentOffset = scrollViewer.VerticalOffset;

                // Ensure no multiple handlers are attached
                CompositionTarget.Rendering -= SmoothScrollHandler;

                // Attach a new handler for smooth scrolling
                SmoothScrollHandler = (s, e) =>
                {
                    // Gradually move toward the target offset
                    currentOffset += (targetOffset - currentOffset) * 0.2;

                    // Scroll to the new offset
                    scrollViewer.ScrollToVerticalOffset(currentOffset);

                    // Stop scrolling when close enough to the target
                    if (Math.Abs(targetOffset - currentOffset) < 1)
                    {
                        scrollViewer.ScrollToVerticalOffset(targetOffset);
                        CompositionTarget.Rendering -= SmoothScrollHandler;
                    }
                };

                CompositionTarget.Rendering += SmoothScrollHandler;
            }
        }

        // Event handler reference for smooth scrolling
        private EventHandler SmoothScrollHandler;

        private void StartLyricSync()
        {
            lyricTimer.Stop();
            lyricTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) }; // Adjusted to a reasonable interval
            lyricTimer.Tick += (s, e) => Application.Current.Dispatcher.Invoke(UpdateLyricsDisplay);
            lyricTimer.Start();
        }

        private class LyricLine
        {
            public TimeSpan Time { get; set; }
            public string Text { get; set; }
        }
    }
}
