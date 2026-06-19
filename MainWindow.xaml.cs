using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;

namespace SobLogReader
{
    /// <summary>
    /// Main interaction logic for the combat log parser. 
    /// Handles file monitoring, regex text parsing, and UI data binding.
    /// </summary>
    public partial class MainWindow : Window
    {
        public const string AppVersion = "v4.1.0";

        private string _logFilePath;
        private long _lastFilePosition = 0;
        private DispatcherTimer _pollTimer;

        // ObservableCollection automatically updates the bound WPF ListBox when new fights are added
        public ObservableCollection<Fight> Fights { get; set; } = new ObservableCollection<Fight>();

        // Regex: Strips UI hex color codes (e.g., [FFFFFF]) and closing tags ([-]) from raw log lines
        private readonly Regex _colorTagRegex = new Regex(@"\[[A-Fa-f0-9]{6}\]|\[-\]", RegexOptions.Compiled);

        // Regex Patterns: Extracts target mob names and damage/miss values depending on the action type
        private readonly Regex _playerHitRegex = new Regex(@"You swing at (?<mob>.+?)(?: and critically hit)?\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _playerSpellHitRegex = new Regex(@"Your (?<spell>.+?)\s+hits (?<mob>.+?)\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _petSpellHitRegex = new Regex(@"Your pet (?<pet>.+?)\s+(?<spell>[^\s]+)\s+hits (?<mob>.+?)\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _mobHitRegex = new Regex(@"(?:from (?<mob>.+?)\s+damages you|(?<mob>.+?)\s+swings at you).*?for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _mobHitPetRegex = new Regex(@"(?:from (?<mob>.+?)\s+damages your pet|(?<mob>.+?)\s+swings at your pet).*?for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _petHitRegex = new Regex(@"Your pet (?<pet>.+?) swings at (?<mob>.+?)\s+for (?<dmg>\d+)", RegexOptions.Compiled);
        private readonly Regex _playerMissRegex = new Regex(@"You miss (?<mob>.+?)$", RegexOptions.Compiled);
        private readonly Regex _mobMissRegex = new Regex(@"(?<mob>.+?) misses you", RegexOptions.Compiled);
        private readonly Regex _mobMissPetRegex = new Regex(@"(?<mob>.+?) misses your pet", RegexOptions.Compiled);
        private readonly Regex _petMissRegex = new Regex(@"Your pet (?<pet>.+?) misses (?<mob>.+?)$", RegexOptions.Compiled);

        // Regex: Extracts item names from loot actions
        private readonly Regex _lootRegex = new Regex(@"You looted (?<loot>.+)$", RegexOptions.Compiled);

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = AppVersion;
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
                    {
                        _lastFilePosition = 0;
                        Fights.Clear();
                        RawLogBox.Clear();
                        StatsPanel.Visibility = Visibility.Hidden;
                        WelcomePanel.Visibility = Visibility.Visible;
                    }
 
                    fs.Seek(_lastFilePosition, SeekOrigin.Begin);
 
                    using (var reader = new StreamReader(fs))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            try
                            {
                                ParseLogLine(line);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error parsing log line: {ex.Message}");
                            }
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

            string[] parts = line.Split('|');
            if (parts.Length < 7) return; // Ignore old format lines

            string logType = parts[0];
            string timestampStr = parts[1];
            string targetName = parts[parts.Length - 1];
            string targetID = parts[parts.Length - 2];
            string sourceName = parts[parts.Length - 3];
            string sourceID = parts[parts.Length - 4];
            string rawAction = string.Join("|", parts.Skip(2).Take(parts.Length - 6));

            if (!DateTime.TryParse(timestampStr, out DateTime timestamp))
                return;

            // Clean the string of hex colors before parsing against the combat regexes
            string action = _colorTagRegex.Replace(rawAction, "").Trim();
            if (action.StartsWith("[Combat]"))
            {
                action = action.Substring(8).Trim();
            }

            string mobId = null;
            string mobName = null;

            if (!string.IsNullOrEmpty(sourceID) && !string.IsNullOrEmpty(targetID))
            {
                if (logType == "Loot")
                {
                    mobId = targetID;
                    mobName = targetName.Replace(" Corpse", "").Trim();
                }
                else if (_playerHitRegex.IsMatch(action) || _playerSpellHitRegex.IsMatch(action) || _petSpellHitRegex.IsMatch(action) || _playerMissRegex.IsMatch(action) || _petHitRegex.IsMatch(action) || _petMissRegex.IsMatch(action))
                {
                    mobId = targetID;
                    mobName = targetName;
                }
                else if (_mobHitRegex.IsMatch(action) || _mobHitPetRegex.IsMatch(action) || _mobMissRegex.IsMatch(action) || _mobMissPetRegex.IsMatch(action))
                {
                    mobId = sourceID;
                    mobName = sourceName;
                }
            }

            if (string.IsNullOrEmpty(mobName))
            {
                mobName = ExtractMobName(action);
            }

            if (string.IsNullOrEmpty(mobName)) return;

            Fight fight = GetOrCreateFight(mobId, mobName, timestamp);

            fight.RawLogs.Add(line);
            if (logType != "Loot")
            {
                fight.EndTime = timestamp; // Expand the fight duration to this latest event
            }

            // Evaluate the specific event type and record the data
            if (_playerHitRegex.IsMatch(action))
            {
                var m = _playerHitRegex.Match(action);
                fight.PlayerHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_petSpellHitRegex.IsMatch(action))
            {
                var m = _petSpellHitRegex.Match(action);
                int dmg = int.Parse(m.Groups["dmg"].Value);
                fight.PetHits.Add(dmg);
                string petName = !string.IsNullOrEmpty(sourceName) ? sourceName : m.Groups["pet"].Value.Trim();
                string petId = !string.IsNullOrEmpty(sourceID) ? sourceID : null;
                var pet = fight.GetOrCreatePet(petId, petName);
                pet.AddHit(dmg);
            }
            else if (_playerSpellHitRegex.IsMatch(action))
            {
                var m = _playerSpellHitRegex.Match(action);
                fight.PlayerHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_mobHitRegex.IsMatch(action))
            {
                var m = _mobHitRegex.Match(action);
                fight.MobHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_mobHitPetRegex.IsMatch(action))
            {
                var m = _mobHitPetRegex.Match(action);
                fight.MobHits.Add(int.Parse(m.Groups["dmg"].Value));
            }
            else if (_petHitRegex.IsMatch(action))
            {
                var m = _petHitRegex.Match(action);
                int dmg = int.Parse(m.Groups["dmg"].Value);
                fight.PetHits.Add(dmg);
                string petName = !string.IsNullOrEmpty(sourceName) ? sourceName : m.Groups["pet"].Value.Trim();
                string petId = !string.IsNullOrEmpty(sourceID) ? sourceID : null;
                var pet = fight.GetOrCreatePet(petId, petName);
                pet.AddHit(dmg);
            }
            else if (_playerMissRegex.IsMatch(action))
            {
                fight.PlayerMisses++;
            }
            else if (_mobMissRegex.IsMatch(action))
            {
                fight.MobMisses++;
            }
            else if (_mobMissPetRegex.IsMatch(action))
            {
                fight.MobMisses++;
            }
            else if (_petMissRegex.IsMatch(action))
            {
                fight.PetMisses++;
                var m = _petMissRegex.Match(action);
                string petName = !string.IsNullOrEmpty(sourceName) ? sourceName : m.Groups["pet"].Value.Trim();
                string petId = !string.IsNullOrEmpty(sourceID) ? sourceID : null;
                var pet = fight.GetOrCreatePet(petId, petName);
                pet.AddMiss();
            }
            else if (logType == "Loot")
            {
                var m = _lootRegex.Match(action);
                if (m.Success)
                {
                    fight.Loot.Add(m.Groups["loot"].Value.Trim());
                }
            }

            // Real-time UI update if the user is currently viewing this specific encounter
            if (FightsListBox.SelectedItem == fight)
            {
                UpdateStatsView(fight);
            }
        }

        /// <summary>
        /// Determines the target of the action if IDs are missing.
        /// </summary>
        private string ExtractMobName(string action)
        {
            if (_playerHitRegex.IsMatch(action)) return _playerHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_petSpellHitRegex.IsMatch(action)) return _petSpellHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_playerSpellHitRegex.IsMatch(action)) return _playerSpellHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobHitRegex.IsMatch(action)) return _mobHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobHitPetRegex.IsMatch(action)) return _mobHitPetRegex.Match(action).Groups["mob"].Value.Trim();
            if (_petHitRegex.IsMatch(action)) return _petHitRegex.Match(action).Groups["mob"].Value.Trim();
            if (_playerMissRegex.IsMatch(action)) return _playerMissRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobMissRegex.IsMatch(action)) return _mobMissRegex.Match(action).Groups["mob"].Value.Trim();
            if (_mobMissPetRegex.IsMatch(action)) return _mobMissPetRegex.Match(action).Groups["mob"].Value.Trim();
            if (_petMissRegex.IsMatch(action)) return _petMissRegex.Match(action).Groups["mob"].Value.Trim();

            if (_lootRegex.IsMatch(action) && Fights.Any())
            {
                return Fights.FirstOrDefault()?.MobName;
            }
            return null;
        }

        /// <summary>
        /// Retrieves an existing encounter or instantiates a new one based on Mob ID.
        /// </summary>
        private Fight GetOrCreateFight(string mobId, string mobName, DateTime timestamp)
        {
            Fight existingFight = null;

            if (!string.IsNullOrEmpty(mobId))
            {
                existingFight = Fights.FirstOrDefault(f => f.Id == mobId);
            }
            else if (!string.IsNullOrEmpty(mobName))
            {
                // Fallback for lines without ID: match the most recent active, unlooted fight within a 15s window
                existingFight = Fights.FirstOrDefault(f => f.MobName == mobName && !f.Loot.Any() && (timestamp - f.EndTime).TotalSeconds < 15);

                if (existingFight == null)
                {
                    // If none match, look for any fight of that name within a 15s window
                    existingFight = Fights.FirstOrDefault(f => f.MobName == mobName && (timestamp - f.EndTime).TotalSeconds < 15);
                }
            }

            if (existingFight != null)
            {
                return existingFight;
            }

            var newFight = new Fight { Id = mobId, MobName = mobName, StartTime = timestamp, EndTime = timestamp };

            // Insert at index 0 so the newest encounters appear at the top of the UI list
            Fights.Insert(0, newFight);

            return newFight;
        }

        private void FightsListBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            int selectedCount = FightsListBox.SelectedItems.Count;

            if (selectedCount > 1)
            {
                SessionStatsButton.Visibility = Visibility.Visible;
                SessionStatsButton.Content = $"Summarize Session ({selectedCount} fights)";
            }
            else
            {
                SessionStatsButton.Visibility = Visibility.Collapsed;
            }

            if (selectedCount == 1)
            {
                SessionStatsPanel.Visibility = Visibility.Collapsed;
                StatsPanel.Visibility = Visibility.Visible;
                WelcomePanel.Visibility = Visibility.Collapsed;
                if (FightsListBox.SelectedItem is Fight selectedFight)
                {
                    UpdateStatsView(selectedFight);
                }
            }
            else if (selectedCount == 0)
            {
                SessionStatsPanel.Visibility = Visibility.Collapsed;
                StatsPanel.Visibility = Visibility.Collapsed;
                WelcomePanel.Visibility = Visibility.Visible;
            }
        }

        private void SessionStatsButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedFights = FightsListBox.SelectedItems.Cast<Fight>().ToList();
            if (selectedFights.Count < 2) return;

            StatsPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Collapsed;
            SessionStatsPanel.Visibility = Visibility.Visible;

            UpdateSessionStatsView(selectedFights);
        }

        private void UpdateSessionStatsView(List<Fight> selectedFights)
        {
            int fightCount = selectedFights.Count;
            double totalDurationSeconds = selectedFights.Sum(f => f.DurationSeconds);
            string totalDurationStr = $"{(int)selectedFights.Sum(f => (f.EndTime - f.StartTime).TotalSeconds)}s";

            SessionTitleText.Text = $"{fightCount} Fights | Total Duration: {totalDurationStr}";

            // Sort selected fights chronologically by start time
            var sortedFights = selectedFights.OrderBy(f => f.StartTime).ToList();
            DateTime firstFightTime = sortedFights.First().StartTime;
            DateTime lastFightTime = sortedFights.Last().EndTime;
            TimeSpan sessionDuration = lastFightTime - firstFightTime;

            // Calculate total gold from loot
            int totalGold = 0;
            foreach (var lootItem in selectedFights.SelectMany(f => f.Loot))
            {
                string trimmed = lootItem.Trim();
                var match = Regex.Match(trimmed, @"^(?<qty>\d+)\s+(?<name>.+)$");
                if (match.Success)
                {
                    int qty = int.Parse(match.Groups["qty"].Value);
                    string name = match.Groups["name"].Value.Trim();
                    string key = GetLootGroupKey(name);
                    if (key == "coins")
                    {
                        totalGold += qty;
                    }
                }
                else
                {
                    string key = GetLootGroupKey(trimmed);
                    if (key == "coins")
                    {
                        totalGold += 1;
                    }
                }
            }

            // Calculate gold/hour rate
            double goldPerHour = 0;
            if (sessionDuration.TotalSeconds > 0)
            {
                goldPerHour = totalGold / sessionDuration.TotalHours;
            }

            // Display time range and gold stats
            string timeRangeStr = firstFightTime.Date == lastFightTime.Date
                ? $"{firstFightTime:HH:mm:ss} - {lastFightTime:HH:mm:ss}"
                : $"{firstFightTime:yyyy-MM-dd HH:mm:ss} - {lastFightTime:yyyy-MM-dd HH:mm:ss}";

            SessionGoldDurationText.Text = $"Farming Time: {FormatDuration(sessionDuration)} ({timeRangeStr})";
            SessionTotalGoldText.Text = $"{totalGold:N0} Gold";
            SessionGoldPerHourText.Text = $"{goldPerHour:N1} / hr";

            // 1. Player Damage Stats Processing
            var allPlayerHits = selectedFights.SelectMany(f => f.PlayerHits).ToList();
            double playerTotal = allPlayerHits.Sum();
            int playerMax = allPlayerHits.Any() ? allPlayerHits.Max() : 0;
            int playerMin = allPlayerHits.Any() ? allPlayerHits.Min() : 0;
            double playerAvg = allPlayerHits.Any() ? allPlayerHits.Average() : 0;
            int playerMisses = selectedFights.Sum(f => f.PlayerMisses);
            int playerSwings = allPlayerHits.Count + playerMisses;
            double playerHitRate = playerSwings > 0 ? (double)allPlayerHits.Count / playerSwings * 100 : 0;

            SessionPlayerDmg.Text = $"Max: {playerMax} | Min: {playerMin} | Avg: {playerAvg:F1}";
            SessionPlayerDps.Text = $"DPS: {(totalDurationSeconds > 0 ? playerTotal / totalDurationSeconds : playerTotal):F2}";
            SessionPlayerAcc.Text = $"Hit Rate: {playerHitRate:F1}% ({allPlayerHits.Count} Hits / {playerMisses} Misses)";

            // 2. Mob Damage Stats Processing
            var allMobHits = selectedFights.SelectMany(f => f.MobHits).ToList();
            double mobTotal = allMobHits.Sum();
            int mobMax = allMobHits.Any() ? allMobHits.Max() : 0;
            int mobMin = allMobHits.Any() ? allMobHits.Min() : 0;
            double mobAvg = allMobHits.Any() ? allMobHits.Average() : 0;
            int mobMisses = selectedFights.Sum(f => f.MobMisses);
            int mobSwings = allMobHits.Count + mobMisses;
            double mobHitRate = mobSwings > 0 ? (double)allMobHits.Count / mobSwings * 100 : 0;

            SessionMobDmg.Text = $"Max: {mobMax} | Min: {mobMin} | Avg: {mobAvg:F1}";
            SessionMobDps.Text = $"DPS: {(totalDurationSeconds > 0 ? mobTotal / totalDurationSeconds : mobTotal):F2}";
            SessionMobAcc.Text = $"Hit Rate: {mobHitRate:F1}% ({allMobHits.Count} Hits / {mobMisses} Misses)";

            // 3. Pet Damage Stats Processing
            var allPetHits = selectedFights.SelectMany(f => f.PetHits).ToList();
            double petTotal = allPetHits.Sum();
            int petMax = allPetHits.Any() ? allPetHits.Max() : 0;
            int petMin = allPetHits.Any() ? allPetHits.Min() : 0;
            double petAvg = allPetHits.Any() ? allPetHits.Average() : 0;
            int petMisses = selectedFights.Sum(f => f.PetMisses);
            int petSwings = allPetHits.Count + petMisses;
            double petHitRate = petSwings > 0 ? (double)allPetHits.Count / petSwings * 100 : 0;

            SessionPetDmg.Text = $"Max: {petMax} | Min: {petMin} | Avg: {petAvg:F1}";
            SessionPetDps.Text = $"DPS: {(totalDurationSeconds > 0 ? petTotal / totalDurationSeconds : petTotal):F2}";
            SessionPetAcc.Text = $"Hit Rate: {petHitRate:F1}% ({allPetHits.Count} Hits / {petMisses} Misses)";

            // 4. Session Individual Pets Breakdown
            var sessionPets = new List<PetStats>();
            var allPetsAcrossFights = selectedFights.SelectMany(f => f.Pets).ToList();
            var groupedPets = allPetsAcrossFights
                .GroupBy(p => !string.IsNullOrEmpty(p.Id) ? p.Id : p.Name)
                .ToList();

            foreach (var group in groupedPets)
            {
                var samplePet = group.First();
                var aggregatedPet = new PetStats
                {
                    Id = samplePet.Id,
                    Name = samplePet.Name,
                    FightDurationSeconds = totalDurationSeconds
                };
                
                foreach (var petInstance in group)
                {
                    aggregatedPet.Hits.AddRange(petInstance.Hits);
                    aggregatedPet.Misses += petInstance.Misses;
                }
                
                aggregatedPet.NotifyAllChanged();
                sessionPets.Add(aggregatedPet);
            }

            SessionPetBreakdownList.ItemsSource = null;
            SessionPetBreakdownList.ItemsSource = sessionPets;

            // 5. Consolidated Loot Processing
            var lootCounts = new Dictionary<string, (string DisplayName, int Quantity, bool IsStacked)>();

            foreach (var lootItem in selectedFights.SelectMany(f => f.Loot))
            {
                string trimmed = lootItem.Trim();
                var match = Regex.Match(trimmed, @"^(?<qty>\d+)\s+(?<name>.+)$");
                if (match.Success)
                {
                    int qty = int.Parse(match.Groups["qty"].Value);
                    string name = match.Groups["name"].Value.Trim();
                    string key = GetLootGroupKey(name);
                    if (key == "coins")
                    {
                        name = "coins";
                    }
                    
                    if (lootCounts.TryGetValue(key, out var current))
                    {
                        string displayName = current.DisplayName;
                        if (!displayName.EndsWith("s", StringComparison.OrdinalIgnoreCase) && name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = name;
                        }
                        lootCounts[key] = (displayName, current.Quantity + qty, true);
                    }
                    else
                    {
                        lootCounts[key] = (name, qty, true);
                    }
                }
                else
                {
                    string key = GetLootGroupKey(trimmed);
                    string name = trimmed;
                    if (key == "coins")
                    {
                        name = "coins";
                    }
                    if (lootCounts.TryGetValue(key, out var current))
                    {
                        string displayName = current.DisplayName;
                        if (!displayName.EndsWith("s", StringComparison.OrdinalIgnoreCase) && name.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                        {
                            displayName = name;
                        }
                        lootCounts[key] = (displayName, current.Quantity + 1, current.IsStacked);
                    }
                    else
                    {
                        lootCounts[key] = (name, 1, false);
                    }
                }
            }

            var consolidatedLoot = new List<string>();
            foreach (var kvp in lootCounts.Values.OrderBy(v => v.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                string name = kvp.DisplayName;
                int qty = kvp.Quantity;
                bool isStacked = kvp.IsStacked;

                if (isStacked)
                {
                    consolidatedLoot.Add($"{qty} {name}");
                }
                else
                {
                    consolidatedLoot.Add(qty > 1 ? $"{name} (x{qty})" : name);
                }
            }

            SessionLootList.ItemsSource = null;
            SessionLootList.ItemsSource = consolidatedLoot;
        }

        private static string FormatDuration(TimeSpan duration)
        {
            int hours = (int)duration.TotalHours;
            int minutes = duration.Minutes;
            int seconds = duration.Seconds;

            if (hours > 0)
                return $"{hours}h {minutes}m {seconds}s";
            if (minutes > 0)
                return $"{minutes}m {seconds}s";
            return $"{seconds}s";
        }

        private static string GetLootGroupKey(string name)
        {
            string trimmed = name.Trim();
            if (trimmed.Equals("gold pieces", StringComparison.OrdinalIgnoreCase) || 
                trimmed.Equals("gold piece", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("gold coins", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("gold coin", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("coins", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("coin", StringComparison.OrdinalIgnoreCase))
            {
                return "coins";
            }
            if (trimmed.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(0, trimmed.Length - 3).ToLowerInvariant() + "y";
            }
            if (trimmed.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !trimmed.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(0, trimmed.Length - 1).ToLowerInvariant();
            }
            return trimmed.ToLowerInvariant();
        }

        private void FilterTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (FilterTextBox == null) return;
            
            string filterText = FilterTextBox.Text.Trim();
            ICollectionView view = CollectionViewSource.GetDefaultView(Fights);

            if (string.IsNullOrEmpty(filterText))
            {
                view.Filter = null;
                return;
            }

            try
            {
                var regex = new Regex(filterText, RegexOptions.IgnoreCase);
                view.Filter = obj =>
                {
                    if (obj is Fight fight)
                    {
                        return regex.IsMatch(fight.MobName);
                    }
                    return false;
                };
            }
            catch (ArgumentException)
            {
                // Fallback to basic case-insensitive substring search for malformed/partial regex
                view.Filter = obj =>
                {
                    if (obj is Fight fight)
                    {
                        return fight.MobName.Contains(filterText, StringComparison.OrdinalIgnoreCase);
                    }
                    return false;
                };
            }
        }

        private void ListBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!e.Handled)
            {
                e.Handled = true;
                var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender
                };
                var parent = ((FrameworkElement)sender).Parent as UIElement;
                parent?.RaiseEvent(eventArg);
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

            // Pet Breakdown Binding
            PetBreakdownList.ItemsSource = null; // Force UI refresh
            PetBreakdownList.ItemsSource = fight.Pets;

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
    public class Fight : INotifyPropertyChanged
    {
        public string Id { get; set; }
        private string _mobName = string.Empty;
        private DateTime _startTime;
        private DateTime _endTime;

        public string MobName
        {
            get => _mobName;
            set
            {
                if (_mobName != value)
                {
                    _mobName = value;
                    OnPropertyChanged();
                }
            }
        }

        public DateTime StartTime
        {
            get => _startTime;
            set
            {
                if (_startTime != value)
                {
                    _startTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DurationSeconds));
                    OnPropertyChanged(nameof(DurationStr));
                    OnPropertyChanged(nameof(StartTimeStr));
                    OnPropertyChanged(nameof(DurationAndStartStr));
                    foreach (var pet in Pets)
                    {
                        pet.FightDurationSeconds = DurationSeconds;
                    }
                }
            }
        }

        public DateTime EndTime
        {
            get => _endTime;
            set
            {
                if (_endTime != value)
                {
                    _endTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DurationSeconds));
                    OnPropertyChanged(nameof(DurationStr));
                    OnPropertyChanged(nameof(DurationAndStartStr));
                    foreach (var pet in Pets)
                    {
                        pet.FightDurationSeconds = DurationSeconds;
                    }
                }
            }
        }

        // Prevents divide-by-zero exceptions for instantaneous encounters
        public double DurationSeconds => Math.Max((EndTime - StartTime).TotalSeconds, 1.0);
        public string DurationStr => $"{(int)(EndTime - StartTime).TotalSeconds}s";
        public string StartTimeStr => StartTime.ToString("yyyy-MM-dd HH:mm:ss");
        public string DurationAndStartStr => $"{DurationStr} - {StartTimeStr}";

        public List<int> PlayerHits { get; set; } = new List<int>();
        public List<int> MobHits { get; set; } = new List<int>();
        public List<int> PetHits { get; set; } = new List<int>();

        public int PlayerMisses { get; set; }
        public int MobMisses { get; set; }
        public int PetMisses { get; set; }

        public ObservableCollection<PetStats> Pets { get; set; } = new ObservableCollection<PetStats>();

        public List<string> Loot { get; set; } = new List<string>();
        public List<string> RawLogs { get; set; } = new List<string>();

        public PetStats GetOrCreatePet(string? petId, string petName)
        {
            PetStats? pet = null;
            if (!string.IsNullOrEmpty(petId))
            {
                pet = Pets.FirstOrDefault(p => p.Id == petId);
            }
            else if (!string.IsNullOrEmpty(petName))
            {
                pet = Pets.FirstOrDefault(p => p.Name == petName);
            }

            if (pet == null)
            {
                pet = new PetStats
                {
                    Id = petId ?? string.Empty,
                    Name = petName ?? "Unknown Pet",
                    FightDurationSeconds = DurationSeconds
                };
                Pets.Add(pet);
            }
            else
            {
                if ((string.IsNullOrEmpty(pet.Name) || pet.Name == "Unknown Pet") && !string.IsNullOrEmpty(petName))
                {
                    pet.Name = petName;
                }
                if (string.IsNullOrEmpty(pet.Id) && !string.IsNullOrEmpty(petId))
                {
                    pet.Id = petId;
                }
            }
            return pet;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Tracks combat stats for a single pet in a fight.
    /// </summary>
    public class PetStats : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private string _name = string.Empty;
        private int _misses;
        private double _fightDurationSeconds = 1.0;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public List<int> Hits { get; set; } = new List<int>();

        public int Misses
        {
            get => _misses;
            set
            {
                _misses = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AccuracyDetails));
            }
        }

        public double FightDurationSeconds
        {
            get => _fightDurationSeconds;
            set
            {
                _fightDurationSeconds = value;
                OnPropertyChanged(nameof(DpsStr));
            }
        }

        public int TotalDamage => Hits.Sum();
        public int MaxDamage => Hits.Any() ? Hits.Max() : 0;
        public int MinDamage => Hits.Any() ? Hits.Min() : 0;
        public double AvgDamage => Hits.Any() ? Hits.Average() : 0;

        public string TotalDamageStr => $"{TotalDamage} dmg";
        public string DpsStr => $"DPS: {(FightDurationSeconds > 0 ? TotalDamage / FightDurationSeconds : TotalDamage):F2}";

        public string DamageDetails => $"Max: {MaxDamage} | Min: {MinDamage} | Avg: {AvgDamage:F1}";

        public string AccuracyDetails
        {
            get
            {
                int swings = Hits.Count + Misses;
                double hitRate = swings > 0 ? (double)Hits.Count / swings * 100 : 0;
                return $"Hit Rate: {hitRate:F1}% ({Hits.Count} Hits / {Misses} Misses)";
            }
        }

        public void AddHit(int dmg)
        {
            Hits.Add(dmg);
            NotifyAllChanged();
        }

        public void AddMiss()
        {
            Misses++;
            NotifyAllChanged();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public void NotifyAllChanged()
        {
            OnPropertyChanged(nameof(TotalDamage));
            OnPropertyChanged(nameof(TotalDamageStr));
            OnPropertyChanged(nameof(MaxDamage));
            OnPropertyChanged(nameof(MinDamage));
            OnPropertyChanged(nameof(AvgDamage));
            OnPropertyChanged(nameof(DpsStr));
            OnPropertyChanged(nameof(DamageDetails));
            OnPropertyChanged(nameof(AccuracyDetails));
        }
    }
}