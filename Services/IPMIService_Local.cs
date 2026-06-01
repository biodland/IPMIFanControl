using System.Diagnostics;
using System.Text;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace DellFanControl.Services;

/// <summary>
/// Local in-band IPMI service using direct BMC access
/// More reliable for Dell R720 XD servers
/// </summary>
public class IPMIService_Local : IIPMIService
{
    private readonly ILogger<IPMIService_Local> _logger;
    private readonly string? _idracIp;
    private readonly string? _idracUser;
    private readonly string? _idracPassword;
    private readonly string _ipmiInterface;
    private readonly int _cipherSuite;

    public IPMIService_Local(ILogger<IPMIService_Local> logger, IConfiguration configuration)
    {
        _logger = logger;
        _idracIp = configuration["Idrac:Ip"];
        _idracUser = configuration["Idrac:User"];
        _idracPassword = configuration["Idrac:Password"];
        _ipmiInterface = configuration["Idrac:Interface"] ?? "lanplus";
        _cipherSuite = configuration.GetValue<int>("Idrac:CipherSuite", 3);
    }

    /// <summary>
    /// Get CPU temperatures from IPMI sensors
    /// </summary>
    public async Task<TemperatureStatus> GetTemperaturesAsync(CancellationToken cancellationToken = default)
    {
        var temperatures = new List<int>();

        try
        {
            var command = BuildCommand("sensor reading");
            var output = await ExecuteCommandAsync(command);

            // Parse temperature readings
            // Look for CPU temperature sensors in Dell R720
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Dell R720 typical CPU temperature sensors:
                // "CPU1 Temp" | na      | 38.000    | degrees C
                // "CPU2 Temp" | na      | 40.000    | degrees C
                if (line.Contains("Temp") && line.Contains("degrees C"))
                {
                    var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var sensorName = parts[0].Trim();
                        var tempValue = parts[1].Trim();

                        if (double.TryParse(tempValue, out var temp))
                        {
                            temperatures.Add((int)Math.Round(temp));
                        }
                    }
                }
            }

            _logger.LogDebug("Retrieved {Count} temperature readings", temperatures.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving temperatures from IPMI");
        }

        var highest = temperatures.Count > 0 ? temperatures.Max() : 0;
        return new TemperatureStatus
        {
            HighestTempCelsius = highest,
            AllTemperatures = temperatures,
            Timestamp = DateTime.UtcNow,
            LastUpdateTime = DateTime.Now
        };
    }

    /// <summary>
    /// Get fan speed information from IPMI sensors
    /// </summary>
    public async Task<FanStatus> GetFanStatusAsync(CancellationToken cancellationToken = default)
    {
        var fans = new List<FanInfo>();

        try
        {
            var command = BuildCommand("sensor reading");
            var output = await ExecuteCommandAsync(command);

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for fan sensors
                if (line.Contains("Fan") && line.Contains("RPM"))
                {
                    var parts = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (parts.Length >= 2)
                    {
                        var fanName = parts[0].Trim();
                        var rpmValue = parts[1].Trim();

                        if (int.TryParse(rpmValue, out var rpm))
                        {
                            fans.Add(new FanInfo
                            {
                                Name = fanName,
                                RPM = rpm,
                                Timestamp = DateTime.Now
                            });
                        }
                    }
                }
            }

            _logger.LogDebug("Retrieved {Count} fan readings", fans.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving fan status from IPMI");
        }

        return new FanStatus
        {
            Fans = fans,
            Timestamp = DateTime.UtcNow,
            LastUpdateTime = DateTime.Now
        };
    }

    /// <summary>
    /// Set fan speed to manual percentage
    /// </summary>
    public async Task<bool> SetFanSpeedAsync(int percentage, CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert percentage to hex value (0-255 range)
            int hexValue = (int)((percentage / 100.0) * 255);
            
            var command = BuildCommand($"raw 0x30 0x45 0x01 {hexValue:x2}");
            await ExecuteCommandAsync(command);

            _logger.LogInformation("Fan speed set to {Percentage}%", percentage);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting fan speed to {Percentage}%", percentage);
            return false;
        }
    }

    /// <summary>
    /// Restore dynamic fan control
    /// </summary>
    public async Task<bool> RestoreDynamicControlAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var command = BuildCommand("raw 0x30 0x45 0x01 0x01");
            await ExecuteCommandAsync(command);

            _logger.LogInformation("Dynamic fan control restored");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error restoring dynamic fan control");
            return false;
        }
    }

    /// <summary>
    /// Test IPMI connection
    /// </summary>
    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var command = BuildCommand("mc info");

            // print command for debugging
            _logger.LogInformation("Testing IPMI connection with command: {Command}", command);

            var output = await ExecuteCommandAsync(command);

            // Check if output contains expected BMC info
            bool success = output.Contains("Device ID") || output.Contains("Manufacturer");

            if (success)
            {
                _logger.LogInformation("IPMI connection test successful");
            }
            else
            {
                _logger.LogWarning("IPMI connection test failed - unexpected output");
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPMI connection test failed");
            return false;
        }
    }

    /// <summary>
    /// Build ipmitool command based on configuration
    /// </summary>
    private string BuildCommand(string arguments)
    {
        var cmd = new List<string> { "ipmitool" };

        // If ID, User, and Password are all provided, use network-based IPMI
        if (!string.IsNullOrEmpty(_idracIp) && _idracIp != "127.0.0.1")
        {
            cmd.Add($"-I {_ipmiInterface}");
            cmd.Add($"-H {_idracIp}");
            
            if (!string.IsNullOrEmpty(_idracUser))
            {
                cmd.Add($"-U {_idracUser}");
            }

            if (!string.IsNullOrEmpty(_idracPassword))
            {
                cmd.Add($"-P {_idracPassword}");
            }

            if (_cipherSuite > 0)
            {
                cmd.Add($"-C {_cipherSuite}");
            }
        }
        // Otherwise, use in-band (local) IPMI
        else
        {
            _logger.LogDebug("Using local in-band IPMI");
        }

        cmd.Add(arguments);

        return string.Join(" ", cmd.Select(arg => arg.Contains(" ") ? $"{arg}" : arg));
    }

    /// <summary>
    /// Execute ipmitool command asynchronously
    /// </summary>
    private Task<string> ExecuteCommandAsync(string command)
    {
        var tcs = new TaskCompletionSource<string>();

        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };

            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    output.AppendLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    error.AppendLine(e.Data);
                }
            };

            process.Exited += (sender, e) =>
            {
                if (process.ExitCode == 0)
                {
                    tcs.SetResult(output.ToString());
                }
                else
                {
                    var errorMsg = error.ToString();
                    _logger.LogWarning("ipmitool exited with code {ExitCode}: {Error}", 
                        process.ExitCode, errorMsg);
                    tcs.SetException(new InvalidOperationException(
                        $"ipmitool command failed: {errorMsg}"));
                }
                process.Dispose();
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            tcs.SetException(ex);
        }

        return tcs.Task;
    }

    public void Dispose()
    {
        // No resources to dispose
    }
}