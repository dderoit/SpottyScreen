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
            if (!string.IsNullOrEmpty(savedToken))
            {
                // Use the saved token
                spotify = new SpotifyClient(savedToken);
                StartPolling();
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

                // Save token for future use
                Properties.Settings.Default.SpotifyAccessToken = tokenResponse.AccessToken;
                Properties.Settings.Default.Save();

                // Initialize Spotify client
                spotify = new SpotifyClient(tokenResponse.AccessToken);
                StartPolling();
            }
        }

        private FullTrack currentTrack;

        private async void StartPolling()
        {
            while (true)
            {
                var playback = await spotify.Player.GetCurrentPlayback();
                if (playback?.Item is FullTrack track && track.Id != currentTrack?.Id)
                {
                    currentTrack = track; // Update current track
                    UpdateUI(track); // Only call UpdateUI when the song changes
                    await LoadLyricsAsync(track); // Load lyrics for the new song
                }
                await Task.Delay(5000); // Poll every 5 seconds
            }
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

                    // Log the raw API response for debugging
                    Console.WriteLine("API Response: " + json);

                    var data = JArray.Parse(json);

                    if (data.Count > 0)
                    {
                        var syncedLyricsToken = data[0]["syncedLyrics"]?.ToString();

                        // Log the synced lyrics for debugging
                        Console.WriteLine("Synced Lyrics: " + syncedLyricsToken);

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
                                    var minute = int.Parse(match.Groups[1].Value);  // Minutes
                                    var seconds = double.Parse(match.Groups[2].Value);  // Seconds + tenths
                                    var lyric = match.Groups[3].Value.Trim();  // Lyric text

                                    var time = new TimeSpan(0, 0, minute, (int)seconds, (int)((seconds - (int)seconds) * 1000));  // Convert to TimeSpan

                                    lyrics.Add(new LyricLine
                                    {
                                        Time = time,
                                        Text = lyric
                                    });
                                }
                            }

                            lyrics.Sort((a, b) => a.Time.CompareTo(b.Time));  // Sort lyrics based on time
                        }
                        else
                        {
                            // No synced lyrics found, display "No lyrics found"
                            Console.WriteLine("No syncedLyrics found in the response.");
                            ShowNoLyricsMessage();
                        }
                    }
                    else
                    {
                        // No data found in the API response, display "No lyrics found"
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

            // If there are any lyrics, start syncing
            if (lyrics.Count > 0)
            {
                StartLyricSync();
            }
            else
            {
                // If no lyrics found, show "No lyrics found" message
                ShowNoLyricsMessage();
            }
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

        private void UpdateLyricsDisplay()
        {
            var now = DateTime.Now.TimeOfDay; // Replace with actual track time if available
            int idx = lyrics.FindLastIndex(l => l.Time <= now);

            // Debug log: Check if a new lyric line is found
            if (idx != currentLyricIndex)
            {
                currentLyricIndex = idx;

                // Clear the current lyrics panel
                LyricsPanel.Children.Clear();

                // Add new lyrics to the panel
                for (int i = 0; i < lyrics.Count; i++)
                {
                    var tb = new TextBlock
                    {
                        Text = lyrics[i].Text,
                        Foreground = i == idx ? Brushes.White : Brushes.Gray,
                        FontSize = i == idx ? 32 : 24,
                        Opacity = i == idx ? 1 : 0.5,
                        FontFamily = new FontFamily("Segoe UI Variable"),
                        Margin = new Thickness(0, 4, 0, 4)
                    };

                    // Debug log: Check if lyrics are being added to the panel
                    Console.WriteLine($"Adding lyric: {lyrics[i].Text}");

                    LyricsPanel.Children.Add(tb);
                }
            }
        }

        private void StartLyricSync()
        {
            lyricTimer.Stop();
            lyricTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            lyricTimer.Tick += (s, e) => UpdateLyricsDisplay();
            lyricTimer.Start();
        }

        private class LyricLine
        {
            public TimeSpan Time { get; set; }
            public string Text { get; set; }
        }
    }
}
