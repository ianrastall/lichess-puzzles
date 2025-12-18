using System.IO;
using System.Text.Json;

namespace Lichess_Puzzles.Services;

public record UserProfile
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "Player";
    public double Rating { get; init; } = 1500;
    public double RatingDeviation { get; init; } = 350;
    public double Volatility { get; init; } = 0.06;
    public DateTime LastUpdatedUtc { get; init; } = DateTime.UtcNow;
}

public class UserProfileService
{
    private const string ProfilesFileName = "user_profiles.json";
    private readonly string _profilesPath;
    private List<UserProfile> _profiles = [];

    public UserProfileService()
    {
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LichessPuzzles");

        Directory.CreateDirectory(appDataPath);
        _profilesPath = Path.Combine(appDataPath, ProfilesFileName);

        _profiles = LoadInternal();
        if (_profiles.Count == 0)
        {
            _profiles.Add(CreateDefaultProfile());
            SaveInternal();
        }
    }

    public IReadOnlyList<UserProfile> GetProfiles() => _profiles;

    public UserProfile AddProfile(string name)
    {
        var profile = new UserProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "Player" : name.Trim(),
            Rating = 1500,
            RatingDeviation = 350,
            Volatility = 0.06,
            LastUpdatedUtc = DateTime.UtcNow
        };

        _profiles.Add(profile);
        SaveInternal();
        return profile;
    }

    public void UpdateProfile(UserProfile updated)
    {
        var index = _profiles.FindIndex(p => p.Id == updated.Id);
        if (index >= 0)
        {
            _profiles[index] = updated;
            SaveInternal();
        }
    }

    public UserProfile? GetProfile(Guid id) => _profiles.FirstOrDefault(p => p.Id == id);

    private List<UserProfile> LoadInternal()
    {
        try
        {
            if (!File.Exists(_profilesPath))
                return [];

            var json = File.ReadAllText(_profilesPath);
            var data = JsonSerializer.Deserialize<List<UserProfile>>(json);
            return data ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveInternal()
    {
        try
        {
            var json = JsonSerializer.Serialize(_profiles, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(_profilesPath, json);
        }
        catch
        {
            // Non-fatal if settings fail to persist
        }
    }

    private static UserProfile CreateDefaultProfile()
    {
        return new UserProfile
        {
            Id = Guid.NewGuid(),
            Name = "Player 1",
            Rating = 1500,
            RatingDeviation = 350,
            Volatility = 0.06,
            LastUpdatedUtc = DateTime.UtcNow
        };
    }
}
