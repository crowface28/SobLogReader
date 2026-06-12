using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;

namespace SobLogReader
{
    /// <summary>
    /// Main interaction logic for the combat log parser. 
    /// Handles file monitoring, regex text parsing, and UI data binding.
    /// </summary>
    public partial class MainWindow : Window
    {
        private string _logFilePath;
        private long _lastFilePosition = 0;
        private DispatcherTimer _pollTimer;

        // ObservableCollection automatically updates the bound WPF ListBox when new fights are added
        public ObservableCollection<Fight> Fights { get; set; } = new ObservableCollection<Fight>();

        // Regex: Strips UI hex color codes (e.g., [FFFFFF]) and closing tags ([-]) from raw log lines
        private readonly Regex _colorTagRegex = new Regex(@"\[[A-Fa-f0-9]{6}\]|\[-\]", RegexOptions.Compiled);

        // Regex: Identifies valid combat lines and separates the timestamp from the action text
        private readonly Regex _timestampRegex = new Regex(@"^(\d{1,2}:\d{2}:\d{2} [AP]M)\s+\[Combat\]\s+(.+)$", RegexOptions.Compiled);

        // Regex Patterns: Extracts target mob names and damage/miss values depending on the action type
        private readonly Regex _playerHitRegex = new Regex(@"You swing at (?<mob>.+?)(?: and critically hit)?\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _mobHitRegex = new Regex(@"(?:from (?<mob>.+?)\s+damages you|(?<mob>.+?)\s+swings at you).*?for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _petHitRegex = new Regex(@"Your pet .*? swings at (?<mob>.+?)\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _playerMissRegex = new Regex(@"You miss (?<mob>.+?)$", RegexOptions.Compiled);
        private readonly Regex _mobMissRegex = new Regex(@"(?<mob>.+?) misses you", RegexOptions.Compiled);
        private readonly Regex _petMissRegex = new Regex(@"Your pet .*? misses (?<mob>.+?)$", RegexOptions.Compiled);

        // Regex: Extracts item names from loot actions
        private readonly Regex _lootRegex = new Regex(@"You looted (?<loot>.+)$", RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();
            FightsListBox.ItemsSource = Fights;

            // Initialize a timer to poll the log file for new entries every 2 seconds
            _pollTimer = new DispatcherTimer();
            _pollTimer.Interval = TimeSpan.FromSeconds(2);
            _pollTimer.Tick += PollTimer_Tick;
        }

        /// <summary>
        /// Opens a file dialog to select the target combat log, resets the application state, 
        /// and begins the polling process.
        /// </summary>
        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                _logFilePath = dialog.FileName;
                FileStatusText.Text = $"Monitoring: {Path.GetFileName(_logFilePath)}";

                // Reset application state for the new file
                _lastFilePosition = 0;
                Fights.Clear();
                RawLogBox.Clear();
                StatsPanel.Visibility = Visibility.Hidden;
                WelcomePanel.Visibility = Visibility.Visible;

                ReadNewLogLines();
                _pollTimer.Start();
            }
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (!string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath))
            {
                ReadNewLogLines();
            }
        }

        /// <summary>
        /// Reads new lines appended to the log file since the last poll.
        /// Uses FileShare.ReadWrite to prevent locking the file, allowing the game client to continue writing.
        /// </summary>
        private void ReadNewLogLines()
        {
            try
            {
                using (var fs = new FileStream(_logFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    // Handle edge case where the file was cleared or rolled over by the game client
                    if (fs.Length < _lastFilePosition)
                        _lastFilePosition = 0;

                    fs.Seek(_lastFilePosition, SeekOrigin.Begin);

                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            ParseLogLine(line);
                        }

                        // Cache the position before the stream is disposed so the next poll resumes correctly
                        _lastFilePosition = fs.Position;
                    }
                }
            }
            catch (Exception ex)
            {
                FileStatusText.Text = $"Error reading file: {ex.Message}";
            }
        }

        /// <summary>
        /// Core parsing logic. Evaluates a single log line, associates it with a specific encounter (Fight),
        /// and updates the relevant statistics.
        /// </summary>
        private void ParseLogLine(string line)
        {
            if (!line.Contains("[Combat]")) return;

            var match = _timestampRegex.Match(line);
            if (!match.Success) return;

            DateTime timestamp = DateTime.Parse(match.Groups[1].Value);
            string rawAction = match.Groups[2].Value;

            // Clean the string of hex colors before parsing against the combat regexes
            string action = _colorTagRegex.Replace(rawAction, "").Trim();

            string targetMob = ExtractMobName(action);
            if (string.IsNullOrEmpty(targetMob)) return;

            // Determine if the action signifies the end of a fight (looting) vs active combat
            bool isLootAction = _lootRegex.IsMatch(action);

            Fight fight = GetOrCreateFight(targetMob, timestamp, !isLootAction);

            fight.RawLogs.Add(line);
            fight.EndTime = timestamp; // Expand the fight duration to this latest event

            // Evaluate the specific event type and record the data
            if (_playerHitRegex.IsMatch(action))
            {
                var m = _playerHitRegex.Match(action);
                fight.PlayerHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_mobHitRegex.IsMatch(action))
            {
                var m = _mobHitRegex.Match(action);
                fight.MobHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_petHitRegex.IsMatch(action))
            {
                var m = _petHitRegex.Match(action);
                fight.PetHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_playerMissRegex.IsMatch(action))
            {
                fight.PlayerMisses++;
            }
            else if (_mobMissRegex.IsMatch(action))
            {
                fight.MobMisses++;
            }
            else if (_petMissRegex.IsMatch(action))
            {
                fight.PetMisses++;
            }
            else if (isLootAction)
            {
                var m = _lootRegex.Match(action);
                fight.Loot.Add(m.Groups["loot"].Value.Trim());
            }

            // Real-time UI update if the user is currently viewing this specific encounter
            if (FightsListBox.SelectedItem == fight)
            {
                UpdateStatsView(fight);
            }
        }

        /// <summary>
        /// Determines the target of the action. Loot actions lack an explicit target in the log, 
        /// so they default to the most recently engaged entity.
        /// </summary>
        private string ExtractMobName(string action)
        {
            if (_playerHitRegex.IsMatch(action)) return _playerHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobHitRegex.IsMatch(action)) return _mobHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_petHitRegex.IsMatch(action)) return _petHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_playerMissRegex.IsMatch(action)) return _playerMissRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobMissRegex.IsMatch(action)) return _mobMissRegex.Match(action).Groups["mob"].Value.Trim();
            if (_petMissRegex.IsMatch(action)) return _petMissRegex.Match(action).Groups["mob"].Value.Trim();

            // Fallback for loot: Attach to the most recently updated combat encounter
            if (_lootRegex.IsMatch(action) && Fights.Any())
            {
                return Fights.OrderByDescending(f => f.EndTime).FirstOrDefault()?.MobName;
            }
            return null;
        }

        /// <summary>
        /// Retrieves an existing encounter or instantiates a new one based on time and loot heuristics.
        /// </summary>
        private Fight GetOrCreateFight(string mobName, DateTime timestamp, bool isCombatAction)
        {
            // Look for an existing encounter with this mob name that was active recently (within 10 seconds).
            // Changed to FirstOrDefault because the newest fights are now at the beginning of the collection.
            var existingFight = Fights.FirstOrDefault(f => f.MobName == mobName && (timestamp - f.EndTime).TotalSeconds < 10);

            if (existingFight != null)
            {
                // Heuristic: If we are actively swinging at a mob, but its existing record already contains loot, 
                // it implies the previous mob died and this is a new spawn with the exact same name.
                if (isCombatAction && existingFight.Loot.Any())
                {
                    // Fall through to create a new Fight object below
                }
                else
                {
                    return existingFight;
                }
            }

            var newFight = new Fight { MobName = mobName, StartTime = timestamp, EndTime = timestamp };

            // Insert at index 0 so the newest encounters appear at the top of the UI list
            Fights.Insert(0, newFight);

            return newFight;
        }

        private void FightsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (FightsListBox.SelectedItem is Fight selectedFight)
            {
                StatsPanel.Visibility = Visibility.Visible;
                WelcomePanel.Visibility = Visibility.Collapsed;
                UpdateStatsView(selectedFight);
            }
        }

        /// <summary>
        /// Calculates math statistics (Min, Max, Avg, DPS) from the active fight's raw data lists 
        /// and applies them to the UI elements.
        /// </summary>
        private void UpdateStatsView(Fight fight)
        {
            StatMobName.Text = $"{fight.MobName} ({fight.DurationStr})";

            // Player Stats Processing
            double pTotal = fight.PlayerHits.Sum();
            int pMax = fight.PlayerHits.Any() ? fight.PlayerHits.Max() : 0;
            int pMin = fight.PlayerHits.Any() ? fight.PlayerHits.Min() : 0;
            double pAvg = fight.PlayerHits.Any() ? fight.PlayerHits.Average() : 0;
            int pSwings = fight.PlayerHits.Count + fight.PlayerMisses;
            double pHitRate = pSwings > 0 ? (double)fight.PlayerHits.Count / pSwings * 100 : 0;

            StatPlayerDmg.Text = $"Max: {pMax} | Min: {pMin} | Avg: {pAvg:F1}";
            StatPlayerDps.Text = $"DPS: {(fight.DurationSeconds > 0 ? pTotal / fight.DurationSeconds : pTotal):F2}";
            StatPlayerAcc.Text = $"Hit Rate: {pHitRate:F1}% ({fight.PlayerHits.Count} Hits / {fight.PlayerMisses} Misses)";

            // Mob Stats Processing
            double mTotal = fight.MobHits.Sum();
            int mMax = fight.MobHits.Any() ? fight.MobHits.Max() : 0;
            int mMin = fight.MobHits.Any() ? fight.MobHits.Min() : 0;
            double mAvg = fight.MobHits.Any() ? fight.MobHits.Average() : 0;
            int mSwings = fight.MobHits.Count + fight.MobMisses;
            double mHitRate = mSwings > 0 ? (double)fight.MobHits.Count / mSwings * 100 : 0;

            StatMobDmg.Text = $"Max: {mMax} | Min: {mMin} | Avg: {mAvg:F1}";
            StatMobDps.Text = $"DPS: {(fight.DurationSeconds > 0 ? mTotal / fight.DurationSeconds : mTotal):F2}";
            StatMobAcc.Text = $"Hit Rate: {mHitRate:F1}% ({fight.MobHits.Count} Hits / {fight.MobMisses} Misses)";

            // Pet Stats Processing
            double petTotal = fight.PetHits.Sum();
            int petMax = fight.PetHits.Any() ? fight.PetHits.Max() : 0;
            int petMin = fight.PetHits.Any() ? fight.PetHits.Min() : 0;
            double petAvg = fight.PetHits.Any() ? fight.PetHits.Average() : 0;
            int petSwings = fight.PetHits.Count + fight.PetMisses;
            double petHitRate = petSwings > 0 ? (double)fight.PetHits.Count / petSwings * 100 : 0;

            StatPetDmg.Text = $"Max: {petMax} | Min: {petMin} | Avg: {petAvg:F1}";
            StatPetDps.Text = $"DPS: {(fight.DurationSeconds > 0 ? petTotal / fight.DurationSeconds : petTotal):F2}";
            StatPetAcc.Text = $"Hit Rate: {petHitRate:F1}% ({fight.PetHits.Count} Hits / {fight.PetMisses} Misses)";

            // Loot Binding
            StatLoot.ItemsSource = null; // Force UI refresh
            StatLoot.ItemsSource = fight.Loot;

            // Raw Log Window Formatting
            RawLogBox.Text = string.Join(Environment.NewLine, fight.RawLogs);
            RawLogBox.ScrollToEnd();
        }
    }

    /// <summary>
    /// Data model representing a single, distinct combat encounter.
    /// Tracks time boundaries and maintains isolated lists of combat events for stat generation.
    /// </summary>
    public class Fight
    {
        public string MobName { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }

        // Prevents divide-by-zero exceptions for instantaneous encounters
        public double DurationSeconds => Math.Max((EndTime - StartTime).TotalSeconds, 1.0);
        public string DurationStr => $"{(int)(EndTime - StartTime).TotalSeconds}s";

        public List<int> PlayerHits { get; set; } = new List<int>();
        public List<int> MobHits { get; set; } = new List<int>();
        public List<int> PetHits { get; set; } = new List<int>();

        public int PlayerMisses { get; set; }
        public int MobMisses { get; set; }
        public int PetMisses { get; set; }

        public List<string> Loot { get; set; } = new List<string>();
        public List<string> RawLogs { get; set; } = new List<string>();
    }
}