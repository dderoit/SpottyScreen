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
            const string clientId = "41033dc65baf42e287b21398aafb4501"; // Replace with your client ID

            string savedAccessToken = Properties.Settings.Default.SpotifyAccessToken;
            string savedRefreshToken = Properties.Settings.Default.SpotifyRefreshToken;

            var oauth = new OAuthClient();

            if (!string.IsNullOrEmpty(savedAccessToken) && !string.IsNullOrEmpty(savedRefreshToken))
            {
                spotify = new SpotifyClient(savedAccessToken);

                try
                {
                    await spotify.Player.GetCurrentPlayback(); // Test if token works
                    StartPolling();
                    return;
                }
                catch (APIUnauthorizedException)
                {
                    // Access token expired – try refreshing
                    var success = await RefreshAccessToken(clientId, savedRefreshToken, oauth);
                    if (success)
                    {
                        StartPolling();
                        return;
                    }

                    // If refresh failed, fall through to re-auth
                }
            }

            // Begin authentication flow with PKCE
            var (verifier, challenge) = PKCEUtil.GenerateCodes();

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

            using (var http = new HttpListener())
            {
                http.Prefixes.Add("http://127.0.0.1:5000/callback/");
                http.Start();


                Process.Start(new ProcessStartInfo
                {
                    FileName = loginRequest.ToUri().ToString(),
                    UseShellExecute = true
                });

                var context = await http.GetContextAsync();
                var code = context.Request.QueryString["code"];

                string responseHtml = "<html><body><h1>Login successful!</h1><p>You can close this window.</p></body></html>";
                byte[] buffer = Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                context.Response.OutputStream.Close();
                http.Stop();

                var tokenRequest = new PKCETokenRequest(clientId, code, new Uri(redirectUri), verifier);
                var tokenResponse = await oauth.RequestToken(tokenRequest);

                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                Properties.Settings.Default.Save();

                spotify = new SpotifyClient(tokenResponse.AccessToken);
                StartPolling();
            }
        }

        private async Task<bool> RefreshAccessToken(string clientId, string refreshToken, OAuthClient oauth)
        {
            try
            {
                var refreshRequest = new PKCETokenRefreshRequest(clientId, refreshToken);
                var tokenResponse = await oauth.RequestToken(refreshRequest);

                // Save new access token
                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;

                // Save new refresh token if provided
                if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
                {
                    Properties.Settings.Default.SpotifyRefreshToken = tokenResponse.RefreshToken;
                }

                Properties.Settings.Default.Save();

                spotify = new SpotifyClient(tokenResponse.AccessToken);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Token refresh failed: {ex.Message}");
                MessageBox.Show("Spotify session expired. Please log in again.", "Authentication Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Properties.Settings.Default.SpotifyRefreshToken = null;
                Properties.Settings.Default.Save();
                return false;
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
                    await RefreshAccessToken("41033dc65baf42e287b21398aafb4501", Properties.Settings.Default.SpotifyRefreshToken, new OAuthClient());
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
                        Margin = new Thickness(0, 4, 0, 4),
                        TextWrapping = TextWrapping.Wrap
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
            if (LyricsScrollViewer == null || currentIndex < 0 || currentIndex >= LyricsPanel.Children.Count)
                return;

            var currentLyric = LyricsPanel.Children[currentIndex] as TextBlock;
            if (currentLyric == null || !currentLyric.IsLoaded)
                return;

            // Make sure layout is ready before measuring
            currentLyric.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Get current offset
                    double currentOffset = LyricsScrollViewer.VerticalOffset;

                    // Transform lyric position relative to the ScrollViewer
                    GeneralTransform transform = currentLyric.TransformToAncestor(LyricsScrollViewer);
                    Point position = transform.Transform(new Point(0, 0));

                    // Calculate centered target offset
                    double targetOffset = currentOffset + position.Y - (LyricsScrollViewer.ViewportHeight / 2) + (currentLyric.ActualHeight / 2);

                    // Clamp the value to avoid negative scrolls
                    targetOffset = Math.Max(0, targetOffset);

                    // Remove any existing handler
                    CompositionTarget.Rendering -= SmoothScrollHandler;

                    // Animate toward the target offset
                    SmoothScrollHandler = (s, e) =>
                    {
                        currentOffset += (targetOffset - currentOffset) * 0.2;
                        LyricsScrollViewer.ScrollToVerticalOffset(currentOffset);

                        if (Math.Abs(targetOffset - currentOffset) < 1)
                        {
                            LyricsScrollViewer.ScrollToVerticalOffset(targetOffset);
                            CompositionTarget.Rendering -= SmoothScrollHandler;
                        }
                    };

                    CompositionTarget.Rendering += SmoothScrollHandler;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Scroll error: " + ex.Message);
                }
            }, DispatcherPriority.Background);
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
