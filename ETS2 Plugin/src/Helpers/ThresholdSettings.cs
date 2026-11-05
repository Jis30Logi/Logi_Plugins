namespace Loupedeck.ETS2Plugin.Helpers
{
    using System;
    using System.IO;
    using Newtonsoft.Json;

    /// <summary>
    /// Persists user-configurable warning thresholds to a JSON file in AppData.
    /// Call Init() once during Plugin.Load() before any action reads a threshold.
    /// Settings file: %LocalAppData%\Logi\LogiPluginService\Plugins\ETS2\thresholds.json
    /// </summary>
    public static class ThresholdSettings
    {
        private static string _filePath;
        private static SettingsData _data;

        public static void Init()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Logi", "LogiPluginService", "Plugins", "ETS2");

            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "thresholds.json");
            Load();
        }

        private static void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                    _data = JsonConvert.DeserializeObject<SettingsData>(File.ReadAllText(_filePath)) ?? new SettingsData();
                else
                    _data = new SettingsData();
            }
            catch
            {
                _data = new SettingsData();
            }
        }

        private static void Save()
        {
            try { File.WriteAllText(_filePath, JsonConvert.SerializeObject(_data, Formatting.Indented)); }
            catch { /* non-critical */ }
        }

        // ── Fuel ──────────────────────────────────────────────────────────────
        public static float FuelLow  { get => _data.FuelLow;  set { _data.FuelLow  = value; Save(); } }
        public static float FuelHalf { get => _data.FuelHalf; set { _data.FuelHalf = value; Save(); } }

        // ── Engine Damage ─────────────────────────────────────────────────────
        public static float EngineDamageWarn { get => _data.EngineDamageWarn; set { _data.EngineDamageWarn = value; Save(); } }
        public static float EngineDamageCrit { get => _data.EngineDamageCrit; set { _data.EngineDamageCrit = value; Save(); } }

        // ── Truck Damage ──────────────────────────────────────────────────────
        public static float TruckDamageLight { get => _data.TruckDamageLight; set { _data.TruckDamageLight = value; Save(); } }
        public static float TruckDamageHeavy { get => _data.TruckDamageHeavy; set { _data.TruckDamageHeavy = value; Save(); } }

        // ── Speed Fallback ────────────────────────────────────────────────────
        public static float SpeedFallback { get => _data.SpeedFallback; set { _data.SpeedFallback = value; Save(); } }

        private class SettingsData
        {
            public float FuelLow          { get; set; } = 15f;
            public float FuelHalf         { get; set; } = 50f;
            public float EngineDamageWarn { get; set; } = 5f;
            public float EngineDamageCrit { get; set; } = 50f;
            public float TruckDamageLight { get; set; } = 10f;
            public float TruckDamageHeavy { get; set; } = 40f;
            public float SpeedFallback    { get; set; } = 80f;
        }
    }
}
