namespace Loupedeck.ETS2Plugin.Services
{
    using System;
    using System.IO;
    using System.IO.MemoryMappedFiles;
    using System.Runtime.InteropServices;
    using System.Threading;

    /// <summary>
    /// Service that reads ETS2 telemetry data directly from shared memory.
    /// Uses .NET's built-in MemoryMappedFile - no external dependencies!
    /// </summary>
    public class TelemetryService : IDisposable
    {
        private const string SHARED_MEMORY_NAME = "Local\\SCSTelemetry";
        private const int POLL_INTERVAL_MS = 150;

        // Thresholds for warnings
        private const float SPEED_THRESHOLD = 90.0f; // km/h
        private const float FUEL_THRESHOLD = 15.0f; // percentage
        private const float ENGINE_DAMAGE_THRESHOLD = 0.8f; // 0.0-1.0 scale
        private const float FATIGUE_THRESHOLD = 80.0f; // percentage

        private Timer _pollTimer;
        private bool _isConnected = false;
        private bool _isDisposed = false;
        private int _diagTickCount = 0;

        // Current telemetry values
        private float _currentSpeed = 0f;
        private float _currentSpeedLimit = 0f;
        private float _currentFuelPercentage = 100f;
        private bool _parkingBrakeEngaged = false;
        private float _engineDamage = 0f;
        private float _truckChassisWear = 0f;
        private float _wearTransmission = 0f;
        private float _wearCabin = 0f;
        private float _wearWheels = 0f;
        private float _maxTruckWear = 0f;

        public float WearEngine       => _engineDamage;
        public float WearTransmission => _wearTransmission;
        public float WearCabin        => _wearCabin;
        public float WearChassis      => _truckChassisWear;
        public float WearWheels       => _wearWheels;
        public float MaxTruckWear     => _maxTruckWear;
        private bool _blinkerLeft = false;
        private bool _blinkerRight = false;
        private bool _hazardOn = false;
        private Timer _blinkerLeftLatchTimer;
        private Timer _blinkerRightLatchTimer;
        private Timer _hazardLatchTimer;
        private float _fatigue = 0f;
        private int _restStop = -1; // -1 = unknown/no countdown

        // Events for telemetry changes
        public event EventHandler<float> SpeedChanged;
        public event EventHandler<float> SpeedLimitChanged;
        public event EventHandler<float> FuelChanged;
        public event EventHandler<bool> ParkingBrakeChanged;
        public event EventHandler<float> EngineDamageChanged;
        public event EventHandler<float> TruckChassisWearChanged;
        public event EventHandler<float> TruckMaxWearChanged;
        public event EventHandler<bool> HazardChanged;
        public event EventHandler<bool> BlinkerLeftChanged;
        public event EventHandler<bool> BlinkerRightChanged;
        public event EventHandler<float> FatigueChanged;
        public event EventHandler<int> RestStopChanged;
        public event EventHandler<bool> ConnectionStatusChanged;

        public bool IsConnected => _isConnected;
        public int CurrentRestStop => _restStop;
        public float CurrentFuelPercent => _currentFuelPercentage;
        public float CurrentSpeed => _currentSpeed;
        public float CurrentSpeedLimit => _currentSpeedLimit;
        public bool BlinkerLeft  => _blinkerLeft;
        public bool BlinkerRight => _blinkerRight;
        public string DiagInfo { get; private set; } = "no data";

        public int ScanByte(int offset)
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(SHARED_MEMORY_NAME))
                using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                    return accessor.ReadByte(offset);
            }
            catch { return -1; }
        }

        public TelemetryService()
        {
            PluginLog.Info("TelemetryService: Initializing direct shared memory access");
            _blinkerLeftLatchTimer  = new Timer(OnBlinkerLeftLatchExpired,  null, Timeout.Infinite, Timeout.Infinite);
            _blinkerRightLatchTimer = new Timer(OnBlinkerRightLatchExpired, null, Timeout.Infinite, Timeout.Infinite);
            _hazardLatchTimer       = new Timer(OnHazardLatchExpired,       null, Timeout.Infinite, Timeout.Infinite);
        }

        private void OnBlinkerLeftLatchExpired(object state)
        {
            if (_blinkerLeft)
            {
                _blinkerLeft = false;
                BlinkerLeftChanged?.Invoke(this, false);
            }
        }

        private void OnBlinkerRightLatchExpired(object state)
        {
            if (_blinkerRight)
            {
                _blinkerRight = false;
                BlinkerRightChanged?.Invoke(this, false);
            }
        }

        private void OnHazardLatchExpired(object state)
        {
            if (_hazardOn)
            {
                _hazardOn = false;
                HazardChanged?.Invoke(this, false);
            }
        }

        public void Start()
        {
            if (_isDisposed)
            {
                PluginLog.Warning("TelemetryService: Cannot start - service is disposed");
                return;
            }

            PluginLog.Info($"TelemetryService: Starting shared memory polling from {SHARED_MEMORY_NAME}");
            
            // Start polling timer
            _pollTimer = new Timer(OnPollTick, null, 0, POLL_INTERVAL_MS);
            
            PluginLog.Info("TelemetryService: Shared memory polling started");
        }

        private void OnPollTick(object state)
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                // Try to open the shared memory
                using (var mmf = MemoryMappedFile.OpenExisting(SHARED_MEMORY_NAME))
                using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    // Update connection status - shared memory exists means game is running
                    if (!_isConnected)
                    {
                        _isConnected = true;
                        ConnectionStatusChanged?.Invoke(this, true);
                        PluginLog.Info("TelemetryService: Connected to shared memory - icons should turn GREEN");
                    }

                    // Read telemetry data (will handle sdkActive check internally)
                    ReadTelemetryData(accessor);
                }
            }
            catch (FileNotFoundException)
            {
                // Shared memory doesn't exist - game not running
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                    PluginLog.Info("TelemetryService: Disconnected - shared memory not found");
                    ResetTelemetryValues();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"TelemetryService: Error reading shared memory: {ex.Message}");
                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                    ResetTelemetryValues();
                }
            }
        }

        private void ReadTelemetryData(MemoryMappedViewAccessor accessor)
        {
            try
            {
                // Based on scsTelemetryMap_s structure from scs-telemetry-common.hpp
                // All offsets are documented in the SCS SDK header file
                
                // Read engine + all wear components — valid even when at menu/garage
                var newEngineDmg = accessor.ReadSingle(1036) * 100f;
                var newTransmission = accessor.ReadSingle(1040) * 100f;
                var newCabin        = accessor.ReadSingle(1044) * 100f;
                var newChassis      = accessor.ReadSingle(1048) * 100f;
                var newWheels       = accessor.ReadSingle(1052) * 100f;

                if (Math.Abs(newEngineDmg - _engineDamage) > 0.5f)
                {
                    _engineDamage = newEngineDmg;
                    EngineDamageChanged?.Invoke(this, _engineDamage);
                }
                _wearTransmission = newTransmission;
                _wearCabin        = newCabin;
                _wearWheels       = newWheels;

                if (Math.Abs(newChassis - _truckChassisWear) > 0.5f)
                {
                    _truckChassisWear = newChassis;
                    TruckChassisWearChanged?.Invoke(this, _truckChassisWear);
                }

                var newMaxWear = Math.Max(_engineDamage,
                                 Math.Max(_wearTransmission,
                                 Math.Max(_wearCabin,
                                 Math.Max(_truckChassisWear, _wearWheels))));
                if (Math.Abs(newMaxWear - _maxTruckWear) > 0.5f)
                {
                    _maxTruckWear = newMaxWear;
                    TruckMaxWearChanged?.Invoke(this, _maxTruckWear);
                }

                // Check if SDK is active (offset 0)
                var sdkActive = accessor.ReadBoolean(0);
                if (!sdkActive)
                    return;

                // SDK is active - read driving telemetry
                // Read speed (m/s) at CORRECT offset 948 (truck_f.speed) - convert to km/h
                var speedMs = accessor.ReadSingle(948);
                var newSpeed = Math.Abs(speedMs * 3.6f);
                
                if (Math.Abs(newSpeed - _currentSpeed) > 0.1f)
                {
                    _currentSpeed = newSpeed;
                    SpeedChanged?.Invoke(this, _currentSpeed);
                }

                // Read speed limit (m/s) at offset 1068 (truck_f.speedLimit) - convert to km/h
                var speedLimitMs = accessor.ReadSingle(1068);
                var newSpeedLimit = Math.Abs(speedLimitMs * 3.6f);

                // Diagnostic: log nearby offsets every ~5s to help find correct speed limit offset
                if (_diagTickCount == 5)
                {
                    var sb = new System.Text.StringBuilder("SpeedLimit scan: ");
                    for (var off = 1060; off <= 1100; off += 4)
                        sb.Append($"[{off}]={accessor.ReadSingle(off)*3.6f:F1}kmh ");
                    PluginLog.Info(sb.ToString());
                }
                
                if (Math.Abs(newSpeedLimit - _currentSpeedLimit) > 0.1f)
                {
                    _currentSpeedLimit = newSpeedLimit;
                    SpeedLimitChanged?.Invoke(this, _currentSpeedLimit);
                }

                // Read fuel data
                var fuelAmount = accessor.ReadSingle(1000);     // truck_f.fuel at offset 1000
                var fuelCapacity = accessor.ReadSingle(704);    // config_f.fuelCapacity at offset 704
                var newFuelPercentage = fuelCapacity > 0 ? (fuelAmount / fuelCapacity) * 100f : 100f;
                
                if (Math.Abs(newFuelPercentage - _currentFuelPercentage) > 0.5f)
                {
                    _currentFuelPercentage = newFuelPercentage;
                    FuelChanged?.Invoke(this, _currentFuelPercentage);
                    PluginLog.Info($"TelemetryService: Fuel changed to {_currentFuelPercentage:F1}% (amount={fuelAmount:F1}, capacity={fuelCapacity:F1})");
                }

                // Read parking brake at CORRECT offset 1566 (truck_b.parkBrake)
                var newParkingBrake = accessor.ReadBoolean(1566);
                if (newParkingBrake != _parkingBrakeEngaged)
                {
                    _parkingBrakeEngaged = newParkingBrake;
                    ParkingBrakeChanged?.Invoke(this, _parkingBrakeEngaged);
                    PluginLog.Info($"TelemetryService: Parking brake changed to {(_parkingBrakeEngaged ? "ON" : "OFF")}");
                }


                // Blinker flash states at 1578/1579 pulse on/off with each blink cycle.
                // Latch keeps the state true for 600 ms after the last flash seen.
                if (accessor.ReadByte(1578) != 0)
                {
                    _blinkerLeftLatchTimer.Change(600, Timeout.Infinite);
                    if (!_blinkerLeft)
                    {
                        _blinkerLeft = true;
                        BlinkerLeftChanged?.Invoke(this, true);
                    }
                }

                if (accessor.ReadByte(1579) != 0)
                {
                    _blinkerRightLatchTimer.Change(600, Timeout.Infinite);
                    if (!_blinkerRight)
                    {
                        _blinkerRight = true;
                        BlinkerRightChanged?.Invoke(this, true);
                    }
                }

                // Hazard flash state at offset 1588 (pulses on/off with each blink cycle).
                // Latch _hazardOn = true for 1.5 s after the last flash seen.
                if (accessor.ReadByte(1588) != 0)
                {
                    _hazardLatchTimer.Change(600, Timeout.Infinite);
                    if (!_hazardOn)
                    {
                        _hazardOn = true;
                        HazardChanged?.Invoke(this, true);
                    }
                }


                // Read rest/fatigue data at offset 500 (common_i.restStop)
                // Counts down from ~270 game-minutes (4.5 hrs) to 0 (must rest now)
                var restStop = accessor.ReadInt32(500);

                // Periodic diagnostic: scan offsets around 500 every 5 seconds (10 ticks × 500ms)
                _diagTickCount++;
                if (_diagTickCount >= 10)
                {
                    _diagTickCount = 0;
                    var sb = new System.Text.StringBuilder();
                    for (var off = 480; off <= 520; off += 4)
                    {
                        var asInt = accessor.ReadInt32(off);
                        var asFloat = accessor.ReadSingle(off);
                        sb.Append($"[{off}] i={asInt} f={asFloat:F1}  ");
                    }
                    DiagInfo = $"i={restStop} f={accessor.ReadSingle(500):F1}";
                    PluginLog.Info($"[DIAG] offset scan: {sb}");
                }

                if (restStop != _restStop)
                {
                    _restStop = restStop;
                    RestStopChanged?.Invoke(this, _restStop);
                    PluginLog.Info($"TelemetryService: RestStop changed to {_restStop}");
                }

                // Keep legacy fatigue percentage for any other subscribers
                var newFatigue = (restStop >= 0 && restStop <= 11) ? (11 - restStop) * 9.09f : 0f;
                if (Math.Abs(newFatigue - _fatigue) > 0.5f)
                {
                    _fatigue = newFatigue;
                    FatigueChanged?.Invoke(this, _fatigue);
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error($"TelemetryService: Error parsing telemetry data: {ex.Message}");
            }
        }

        private void ResetTelemetryValues()
        {
            _currentSpeed = 0f;
            _currentFuelPercentage = 100f;
            _parkingBrakeEngaged = false;
            _engineDamage = 0f;
            _truckChassisWear = 0f;
            _wearTransmission = 0f;
            _wearCabin = 0f;
            _wearWheels = 0f;
            _maxTruckWear = 0f;
            _blinkerLeft  = false;
            _blinkerRight = false;
            _fatigue = 0f;
            _restStop = -1;

            // Notify all subscribers of reset values
            SpeedChanged?.Invoke(this, _currentSpeed);
            FuelChanged?.Invoke(this, _currentFuelPercentage);
            ParkingBrakeChanged?.Invoke(this, _parkingBrakeEngaged);
            EngineDamageChanged?.Invoke(this, _engineDamage);
            TruckChassisWearChanged?.Invoke(this, _truckChassisWear);
            BlinkerLeftChanged?.Invoke(this, _blinkerLeft);
            BlinkerRightChanged?.Invoke(this, _blinkerRight);
            FatigueChanged?.Invoke(this, _fatigue);
            RestStopChanged?.Invoke(this, _restStop);
        }

        public void Stop()
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                PluginLog.Info("TelemetryService: Stopping");

                _pollTimer?.Dispose();
                _pollTimer = null;
                _blinkerLeftLatchTimer?.Dispose();
                _blinkerLeftLatchTimer = null;
                _blinkerRightLatchTimer?.Dispose();
                _blinkerRightLatchTimer = null;
                _hazardLatchTimer?.Dispose();
                _hazardLatchTimer = null;

                if (_isConnected)
                {
                    _isConnected = false;
                    ConnectionStatusChanged?.Invoke(this, false);
                }

                PluginLog.Info("TelemetryService: Stopped");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"TelemetryService: Error stopping: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            try
            {
                Stop();
                PluginLog.Info("TelemetryService: Disposed");
            }
            catch (Exception ex)
            {
                PluginLog.Error($"TelemetryService: Error during disposal: {ex.Message}");
            }
        }
    }
}
