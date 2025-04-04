#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;

namespace Orleans.Runtime
{
    /// <summary>
    /// Monitors runtime and component health periodically, reporting complaints.
    /// </summary>
    internal class Watchdog(IOptions<ClusterMembershipOptions> clusterMembershipOptions, IEnumerable<IHealthCheckParticipant> participants, ILogger<Watchdog> logger) : IDisposable
    {
        private static readonly TimeSpan PlatformWatchdogHeartbeatPeriod = TimeSpan.FromMilliseconds(1000);
        private readonly CancellationTokenSource _cancellation = new();
        private readonly TimeSpan _componentHealthCheckPeriod = clusterMembershipOptions.Value.LocalHealthDegradationMonitoringPeriod;
        private readonly List<IHealthCheckParticipant> _participants = participants.ToList();
        private readonly ILogger _logger = logger;
        private ValueStopwatch _platformWatchdogStopwatch;
        private ValueStopwatch _componentWatchdogStopwatch;

        // GC pause duration since process start.
        private TimeSpan _cumulativeGCPauseDuration;

        private DateTime _lastComponentHealthCheckTime;
        private Thread? _platformWatchdogThread;
        private Thread? _componentWatchdogThread;

        public void Start()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Starting silo watchdog");
            }

            if (_platformWatchdogThread is not null)
            {
                throw new InvalidOperationException("Watchdog.Start may not be called more than once");
            }

            var now = DateTime.UtcNow;
            _platformWatchdogStopwatch = ValueStopwatch.StartNew();
            _cumulativeGCPauseDuration = GC.GetTotalPauseDuration();

            _platformWatchdogThread = new Thread(RunPlatformWatchdog)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.Platform",
            };
            _platformWatchdogThread.Start();

            _componentWatchdogStopwatch = ValueStopwatch.StartNew();
            _lastComponentHealthCheckTime = DateTime.UtcNow;

            _componentWatchdogThread = new Thread(RunComponentWatchdog)
            {
                IsBackground = true,
                Name = "Orleans.Runtime.Watchdog.Component",
            };

            _componentWatchdogThread.Start();

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Silo watchdog started successfully.");
            }
        }

        public void Stop()
        {
            Dispose();
        }

        protected void RunPlatformWatchdog()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    CheckRuntimeHealth();
                }
                catch (Exception exc)
                {
                    _logger.LogError((int)ErrorCode.Watchdog_InternalError, exc, "Platform watchdog encountered an internal error");
                }

                _platformWatchdogStopwatch.Restart();
                _cumulativeGCPauseDuration = GC.GetTotalPauseDuration();
                _cancellation.Token.WaitHandle.WaitOne(PlatformWatchdogHeartbeatPeriod);
            }
        }

        private void CheckRuntimeHealth()
        {
            if (Debugger.IsAttached)
            {
                return;
            }

            var pauseDurationSinceLastTick = GC.GetTotalPauseDuration() - _cumulativeGCPauseDuration;
            var timeSinceLastTick = _platformWatchdogStopwatch.Elapsed;
            if (timeSinceLastTick > PlatformWatchdogHeartbeatPeriod.Multiply(2))
            {
                var gc = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
                _logger.LogWarning(
                    (int)ErrorCode.SiloHeartbeatTimerStalled,
                    ".NET Runtime Platform stalled for {TimeSinceLastTick}. Total GC Pause duration during that period: {pauseDurationSinceLastTick}. We are now using a total of {TotalMemory}MB memory. Collection counts per generation: 0: {GCGen0Count}, 1: {GCGen1Count}, 2: {GCGen2Count}",
                    timeSinceLastTick,
                    pauseDurationSinceLastTick,
                    GC.GetTotalMemory(false) / (1024 * 1024),
                    gc[0],
                    gc[1],
                    gc[2]);
            }

            var timeSinceLastParticipantCheck = _componentWatchdogStopwatch.Elapsed;
            if (timeSinceLastParticipantCheck > _componentHealthCheckPeriod.Multiply(2))
            {
                _logger.LogWarning(
                    (int)ErrorCode.SiloHeartbeatTimerStalled,
                    "Participant check thread has not completed for {TimeSinceLastTick}, potentially indicating lock contention or deadlock, CPU starvation, or another execution anomaly.",
                    timeSinceLastParticipantCheck);
            }
        }

        protected void RunComponentWatchdog()
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    CheckComponentHealth();
                }
                catch (Exception exc)
                {
                    _logger.LogError((int)ErrorCode.Watchdog_InternalError, exc, "Component health check encountered an internal error");
                }

                _componentWatchdogStopwatch.Restart();
                _cancellation.Token.WaitHandle.WaitOne(_componentHealthCheckPeriod);
            }
        }

        private void CheckComponentHealth()
        {
            WatchdogInstruments.HealthChecks.Add(1);
            var numFailedChecks = 0;
            StringBuilder? complaints = null;

            // Restart the timer before the check to reduce false positives for the stall checker.
            _componentWatchdogStopwatch.Restart();
            foreach (var participant in _participants)
            {
                try
                {
                    var ok = participant.CheckHealth(_lastComponentHealthCheckTime, out var complaint);
                    if (!ok)
                    {
                        complaints ??= new StringBuilder();
                        if (complaints.Length > 0)
                        {
                            complaints.Append(' ');
                        }

                        complaints.Append($"{participant.GetType()} failed health check with complaint \"{complaint}\".");
                        ++numFailedChecks;
                    }
                }
                catch (Exception exc)
                {
                    _logger.LogWarning(
                        (int)ErrorCode.Watchdog_ParticipantThrownException,
                        exc,
                        "Health check participant {Participant} has thrown an exception from its CheckHealth method.",
                        participant.GetType());
                }
            }

            if (complaints != null)
            {
                WatchdogInstruments.FailedHealthChecks.Add(1);
                if (!Debugger.IsAttached)
                {
                    _logger.LogWarning((int)ErrorCode.Watchdog_HealthCheckFailure, "{FailedChecks} of {ParticipantCount} components reported issues. Complaints: {Complaints}", numFailedChecks, _participants.Count, complaints);
                }
            }

            _lastComponentHealthCheckTime = DateTime.UtcNow;
        }

        public void Dispose()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                _componentWatchdogThread?.Join();
            }
            catch
            {
                // Ignore.
            }

            try
            {
                _platformWatchdogThread?.Join();
            }
            catch
            {
                // Ignore.
            }
        }
    }
}
